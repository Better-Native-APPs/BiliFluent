// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Relay;

namespace BiliFluent.Mpv.RelayCheck;

internal static partial class AudioResumeSmoke
{
    private sealed record CorrectionRecord(double? BeforePosition, double? AfterPosition, double? PositionDelta,
        string? BeforeFrameSha256, string? AfterFrameSha256, bool? SameFrame, double DurationMs,
        bool PausedAtStart, bool PausedAtEnd, bool Completed, bool ResumedDuringCorrection,
        string Kind, long? BeforeAid, long? AfterAid, bool? DeselectedAoEmpty, bool? AudioUriPreserved, int NewVideoRequests, int NewAudioRequests);

    // Raw-native policy controls. Production code is not changed until these controls are measured.
    // The threshold probe counts the harness's real ao-reload commands, not the three native
    // notifications per command; a production policy must not mistake notification count for that.
    private sealed class CorrectionProbe : IAsyncDisposable
    {
        private readonly MpvClient _client;
        private readonly LoopbackMediaRelay _relay;
        private readonly TraceLogger _trace;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _gate = new(1);
        private readonly ConcurrentBag<Task> _jobs = [];
        private readonly object _sync = new();
        private readonly double _audioBuffer;
        private readonly int _debounceMs;
        private readonly string _framePrefix;
        private int _correctionSequence;
        private Task _latest = Task.CompletedTask;
        private int _version;
        private int _reloadsSinceSeek;
        private int _active;
        private int _ownAudioChanges;
        private bool _armed;
        private bool _resuming;
        private int _disposed;

        public CorrectionProbe(MpvClient client, LoopbackMediaRelay relay, TraceLogger trace, Dictionary<string, string> options, double audioBuffer, string framePrefix)
        {
            _client = client;
            _relay = relay;
            _trace = trace;
            _audioBuffer = audioBuffer;
            _framePrefix = framePrefix;
            Policy = options.GetValueOrDefault("policy", "library");
            if (Policy is not ("library" or "resume" or "paused" or "threshold-resume" or "threshold-paused" or "reselect"))
                throw new ArgumentOutOfRangeException(nameof(options));
            Threshold = double.Parse(options.GetValueOrDefault("threshold", "0.3"), CultureInfo.InvariantCulture);
            _debounceMs = int.Parse(options.GetValueOrDefault("debounce-ms", "1000"), CultureInfo.InvariantCulture);
            if (!double.IsFinite(Threshold) || Threshold < 0 || _debounceMs < 0) throw new ArgumentOutOfRangeException(nameof(options));
            if (Policy.StartsWith("threshold", StringComparison.Ordinal) && (!double.IsFinite(audioBuffer) || audioBuffer <= 0))
                throw new InvalidOperationException("Native audio-buffer is unavailable.");
            _client.SeekStarted += OnSeekStarted;
            _trace.AudioReconfigured += OnAudioReconfigured;
        }

        public string Policy { get; }
        public double Threshold { get; }
        public ConcurrentQueue<CorrectionRecord> Records { get; } = new();

        public void BeginPause() { lock (_sync) _armed = true; }

        public void NoteControlledReload()
        {
            Interlocked.Increment(ref _reloadsSinceSeek);
            ScheduleCorrection();
        }

        public void BeginResume()
        {
            lock (_sync)
            {
                _resuming = true;
                _version++;
            }
        }

        public async Task RealignAtResumeAsync()
        {
            if (Policy == "library" || Volatile.Read(ref _reloadsSinceSeek) == 0) return;
            if (Policy.StartsWith("threshold", StringComparison.Ordinal) && !WorthCorrecting() && Volatile.Read(ref _active) == 0) return;
            _trace.Mark("resume-realignment", _client);
            var previous = Volatile.Read(ref _seeksStarted);
            Ensure(_client.Command("seek", "0", "relative+exact"), "resume realignment");
            // mpv's command reply only means "queued". Keep pause until its seek event has
            // actually reset both playback chains, rather than letting an old video frame run.
            if (!await WaitAsync(() => Volatile.Read(ref _seeksStarted) > previous, 10_000))
                throw new TimeoutException("Resume realignment did not start.");
        }

        public async Task WaitUntilSettledAsync()
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 35)
            {
                Task latest;
                lock (_sync) latest = _latest;
                await latest.WaitAsync(TimeSpan.FromSeconds(35) - clock.Elapsed);
                lock (_sync)
                {
                    if (ReferenceEquals(latest, _latest) && Volatile.Read(ref _active) == 0) return;
                }

                await Task.Delay(20);
            }

            throw new TimeoutException("Paused correction did not settle.");
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _trace.AudioReconfigured -= OnAudioReconfigured;
            _client.SeekStarted -= OnSeekStarted;
            await _stop.CancelAsync();
            await Task.WhenAll(_jobs);
            _gate.Dispose();
            _stop.Dispose();
        }

        private bool WorthCorrecting() => Volatile.Read(ref _reloadsSinceSeek) * _audioBuffer >= Threshold;

        private void OnSeekStarted(object? sender, EventArgs args)
        {
            Interlocked.Increment(ref _seeksStarted);
            Interlocked.Exchange(ref _reloadsSinceSeek, 0);
        }

        private void OnAudioReconfigured()
        {
            if (Volatile.Read(ref _ownAudioChanges) == 0) ScheduleCorrection();
        }

        private void ScheduleCorrection()
        {
            lock (_sync)
            {
                if (!_armed || _resuming || Policy is not ("paused" or "threshold-paused" or "reselect")) return;
                var version = ++_version;
                _latest = CorrectAfterDebounceAsync(version);
                _jobs.Add(_latest);
            }
        }

        private async Task CorrectAfterDebounceAsync(int version)
        {
            var entered = false;
            var active = false;
            try
            {
                await Task.Delay(_debounceMs, _stop.Token);
                await _gate.WaitAsync(_stop.Token);
                entered = true;
                double beforePosition;
                string beforeFrame;
                bool beforePause;
                long seekSerial;
                long? beforeAid = null;
                string? beforeAudioUri = null;
                bool? aoEmpty = null;
                var reselect = Policy == "reselect";
                var observedReloads = 0;
                var videoRequests = 0;
                var audioRequests = 0;
                var clock = new Stopwatch();
                var ordinal = Interlocked.Increment(ref _correctionSequence);
                lock (_sync)
                {
                    if (_resuming || version != _version || (Policy == "threshold-paused" && !WorthCorrecting())) return;
                    beforePosition = ReadDouble(_client, "time-pos");
                    beforePause = Flag(_client, "pause");
                    if (!beforePause) return;
                    beforeFrame = CaptureFrame(ordinal, "before");
                    seekSerial = Volatile.Read(ref _seeksStarted);
                    videoRequests = _relay.GetStatistics("video")?.Requests ?? 0;
                    audioRequests = _relay.GetStatistics("audio")?.Requests ?? 0;
                    Interlocked.Increment(ref _active);
                    active = true;
                    clock.Start();
                    if (reselect)
                    {
                        beforeAid = ReadAid();
                        if (beforeAid is not > 0) throw new InvalidOperationException("No selected audio track.");
                        beforeAudioUri = _client.GetPropertyString("current-tracks/audio/external-filename");
                        observedReloads = Volatile.Read(ref _reloadsSinceSeek);
                        Interlocked.Increment(ref _ownAudioChanges);
                        _trace.Mark("paused-audio-reselect", _client);
                        Ensure(_client.SetPropertyString("aid", "no"), "audio deselection");
                        aoEmpty = string.IsNullOrEmpty(_client.GetPropertyString("current-ao"));
                        Ensure(_client.SetPropertyString("aid", beforeAid.Value.ToString(CultureInfo.InvariantCulture)), "audio reselection");
                    }
                    else
                    {
                        _trace.Mark("paused-realignment", _client);
                        Ensure(_client.Command("seek", "0", "relative+exact"), "paused realignment");
                    }
                }

                var completed = reselect
                    ? await WaitAsync(() => ReadAid() == beforeAid && _client.GetPropertyString("current-ao") == "wasapi", 30_000)
                    : await WaitForSeekAsync(seekSerial);
                if (reselect) ClearObservedReloads(observedReloads);
                var afterPosition = ReadDouble(_client, "time-pos");
                var afterPause = Flag(_client, "pause");
                var resumed = false;
                lock (_sync) resumed = _resuming;
                var afterFrame = afterPause && !resumed ? CaptureFrame(ordinal, "after") : null;
                Records.Enqueue(new(Finite(beforePosition), Finite(afterPosition), Finite(afterPosition - beforePosition), beforeFrame, afterFrame,
                    afterFrame is not null ? beforeFrame == afterFrame : null, clock.Elapsed.TotalMilliseconds,
                    beforePause, afterPause, completed, resumed, reselect ? "audio-reselect" : "seek", beforeAid, reselect ? ReadAid() : null,
                    aoEmpty, reselect ? beforeAudioUri is not null && beforeAudioUri == _client.GetPropertyString("current-tracks/audio/external-filename") : null,
                    (_relay.GetStatistics("video")?.Requests ?? 0) - videoRequests, (_relay.GetStatistics("audio")?.Requests ?? 0) - audioRequests));
                _trace.Mark("paused-realignment-complete", _client);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally
            {
                if (active) Interlocked.Decrement(ref _active);
                if (Policy == "reselect" && active) Interlocked.Decrement(ref _ownAudioChanges);
                if (entered) _gate.Release();
            }
        }

        private long _seeksStarted;

        private async Task<bool> WaitForSeekAsync(long previous)
        {
            var clock = Stopwatch.StartNew();
            while (Volatile.Read(ref _seeksStarted) <= previous || Flag(_client, "seeking"))
            {
                if (clock.ElapsedMilliseconds >= 30_000) return false;
                await Task.Delay(20, _stop.Token);
            }

            return true;
        }

        private static double? Finite(double value) => double.IsFinite(value) ? value : null;

        private long? ReadAid() => _client.GetProperty("aid", out long aid) == Richasy.MpvKernel.MpvError.Success ? aid : null;

        private void ClearObservedReloads(int observed)
        {
            while (true)
            {
                var current = Volatile.Read(ref _reloadsSinceSeek);
                if (Interlocked.CompareExchange(ref _reloadsSinceSeek, Math.Max(0, current - observed), current) == current) return;
            }
        }

        private string CaptureFrame(int ordinal, string suffix)
        {
            // video-pts no longer exists in this mpv. Its software screenshot fallback takes
            // the actual current VO frame, including for vo=null, so compare its PNG bytes.
            var path = _framePrefix + $"-correction-{ordinal}-{suffix}.png";
            Ensure(_client.Command("screenshot-to-file", path, "video"), "paused frame capture");
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        }
    }
}
