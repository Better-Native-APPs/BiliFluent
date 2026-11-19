// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace BiliFluent.Mpv.RelayCheck;

internal static partial class PlaybackSmokeSupport
{
    internal sealed class FrameProbe : IDisposable
    {
        private const int Capacity = 120000;
        private readonly NativeMpv _mpv;
        private readonly ProbeSample[] _samples = new ProbeSample[Capacity];
        private readonly Thread _thread;
        private readonly nint _timer;
        private readonly ManualResetEventSlim _ready = new(false);
        private bool _stop;
        private bool _disposed;
        private int _count;
        private int _timerError;

        internal FrameProbe(NativeMpv mpv)
        {
            _mpv = mpv;
            Origin = Stopwatch.GetTimestamp();
            _timer = CreateWaitableTimerExW(0, 0, 2, 0x1F0003);
            if (_timer == 0) throw new InvalidOperationException("A high-resolution waitable timer is unavailable.");
            _thread = new Thread(Run) { IsBackground = true, Name = "playback-smoke-frame-probe" };
            _thread.Start();
            _ready.Wait();
            if (_timerError != 0)
            {
                _thread.Join();
                _ = CloseHandle(_timer);
                _ready.Dispose();
                throw new InvalidOperationException("The frame probe timer could not be warmed before timing.");
            }
        }

        internal long Origin { get; }
        internal double Now => Wall(Stopwatch.GetTimestamp());
        internal long LoopAllocatedBytes { get; private set; }
        internal int SampleCount => Volatile.Read(ref _count);
        internal double Wall(long timestamp) => MillisecondsBetween(Origin, timestamp);

        internal WindowMetrics Measure(double from, double to, double initialSpeed, IReadOnlyList<SpeedTransition> transitions)
        {
            if (_timerError != 0 || SampleCount >= Capacity) throw new InvalidOperationException("The frame probe timer or sample buffer failed.");
            var reads = new List<OccupiedEvent>();
            var lags = new List<LagEvent>();
            var sync = new List<double>();
            double maximumRead = 0, maximumLag = 0, maximumPollGap = 0, maximumFrameGap = 0, maximumAudioGap = 0, missingAudioMs = 0;
            var polls = 0;
            var errors = 0;
            var idlePolls = 0;
            var cachedPolls = 0;
            var missingAudioPolls = 0;
            var audioRegressions = 0;
            NumericRead? previousPosition = null;
            NumericRead? previousAudio = null;
            long? audioMissingStart = null;
            var audioChanged = 0L;
            var previousPoll = 0L;
            var count = SampleCount;
            for (var index = 0; index < count; index++)
            {
                var sample = _samples[index];
                var wall = Wall(sample.Ended);
                if (wall > to) break;
                var inWindow = wall >= from;
                if (inWindow)
                {
                    polls++;
                    if (sample.Idle.Value) idlePolls++;
                    if (sample.Cached.Value) cachedPolls++;
                    if (!sample.Position.Available || sample.Speed.Error < 0 || sample.Idle.Error < 0 || sample.Cached.Error < 0 || sample.Paused.Error < 0) errors++;
                    if (sample.Avsync.Available) sync.Add(sample.Avsync.Value * 1000);
                    ObserveRead("time-pos", sample.Position.Started, sample.Position.Ended);
                    ObserveRead("audio-pts", sample.Audio.Started, sample.Audio.Ended);
                    ObserveRead("speed", sample.Speed.Started, sample.Speed.Ended);
                    ObserveRead("avsync", sample.Avsync.Started, sample.Avsync.Ended);
                    ObserveRead("core-idle", sample.Idle.Started, sample.Idle.Ended);
                    ObserveRead("paused-for-cache", sample.Cached.Started, sample.Cached.Ended);
                    ObserveRead("pause", sample.Paused.Started, sample.Paused.Ended);
                    if (previousPoll != 0) maximumPollGap = Math.Max(maximumPollGap, MillisecondsBetween(previousPoll, sample.Ended));
                }

                previousPoll = sample.Ended;
                var position = sample.Position;
                if (!position.Available || sample.Paused.Value)
                {
                    previousPosition = null;
                }
                else if (previousPosition is { } previous && position.Value != previous.Value)
                {
                    var step = position.Value - previous.Value;
                    if (step > 0 && step <= 2 && inWindow)
                    {
                        var wallGap = MillisecondsBetween(previous.Ended, position.Ended);
                        var expected = ExpectedWallMs(previous, position, initialSpeed, transitions, out var crossed);
                        var lag = wallGap - expected;
                        maximumFrameGap = Math.Max(maximumFrameGap, wallGap);
                        maximumLag = Math.Max(maximumLag, lag);
                        if (lag >= 25) lags.Add(new(Wall(position.Ended), Wall(previous.Ended), wallGap, step, expected, lag, crossed));
                    }

                    previousPosition = position;
                }
                else if (previousPosition is null) previousPosition = position;

                var audio = sample.Audio;
                if (!audio.Available)
                {
                    audioMissingStart ??= audio.Started;
                    if (inWindow)
                    {
                        missingAudioPolls++;
                        missingAudioMs = Math.Max(missingAudioMs, Wall(audio.Ended) - Math.Max(from, Wall(audioMissingStart.Value)));
                    }
                }
                else
                {
                    if (audioMissingStart is { } missing && inWindow)
                        missingAudioMs = Math.Max(missingAudioMs, Wall(audio.Ended) - Math.Max(from, Wall(missing)));
                    audioMissingStart = null;
                    if (previousAudio is { } beforeAudio && audio.Value < beforeAudio.Value - 0.000001)
                    {
                        if (inWindow) audioRegressions++;
                        audioChanged = audio.Ended;
                    }
                    else if (previousAudio is null || audio.Value > previousAudio.Value.Value + 0.000001)
                    {
                        if (audioChanged != 0 && inWindow && !sample.Paused.Value)
                            maximumAudioGap = Math.Max(maximumAudioGap, Wall(audio.Ended) - Math.Max(from, Wall(audioChanged)));
                        audioChanged = audio.Ended;
                    }

                    if (inWindow && audioChanged != 0 && !sample.Paused.Value)
                        maximumAudioGap = Math.Max(maximumAudioGap, Wall(audio.Ended) - Math.Max(from, Wall(audioChanged)));
                    previousAudio = audio;
                }
            }

            return new(maximumRead, reads.Count, maximumLag, lags.Count, maximumFrameGap, maximumPollGap, maximumAudioGap,
                Math.Max(0, missingAudioMs), polls, idlePolls, cachedPolls, errors, missingAudioPolls, audioRegressions, Distribution.Of(sync), reads, lags);

            void ObserveRead(string name, long started, long ended)
            {
                var duration = MillisecondsBetween(started, ended);
                maximumRead = Math.Max(maximumRead, duration);
                if (duration >= 25) reads.Add(new(name, Wall(started), Wall(ended), duration));
            }
        }

        internal (double? VideoMs, double? AudioMs) FirstProgress(double from, double startPosition, double speed)
        {
            double? video = null, audio = null;
            NumericRead? previousAudio = null;
            var count = SampleCount;
            for (var index = 0; index < count; index++)
            {
                var sample = _samples[index];
                if (Wall(sample.Started) < from) continue;
                if (sample.Position.Available && sample.Position.Value >= startPosition - 0.05
                    && sample.Position.Value < startPosition + speed * 2 && !sample.Idle.Value)
                    video ??= Wall(sample.Position.Ended) - from;
                if (sample.Audio.Available && sample.Audio.Value >= startPosition - 0.1 && sample.Audio.Value < startPosition + speed * 2)
                {
                    if (previousAudio is { } previous && sample.Audio.Value > previous.Value + 0.000001)
                        audio ??= Wall(sample.Audio.Ended) - from;
                    previousAudio = sample.Audio;
                }

                if (video is not null && audio is not null) break;
            }

            return (video, audio);
        }

        internal void WriteCsv(string path)
        {
            using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            writer.WriteLine("poll_start_ms,poll_end_ms,position_read_start_ms,position_read_end_ms,position_read_ms,time_pos,audio_read_end_ms,audio_read_ms,audio_pts,speed_read_ms,speed,avsync_read_ms,avsync_ms,idle_read_ms,core_idle,cache_read_ms,paused_for_cache,pause_read_ms,pause,position_error,audio_error,speed_error,avsync_error");
            var count = SampleCount;
            for (var index = 0; index < count; index++)
            {
                var sample = _samples[index];
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{Wall(sample.Started):0.000000},{Wall(sample.Ended):0.000000},{Wall(sample.Position.Started):0.000000},{Wall(sample.Position.Ended):0.000000},{sample.Position.Milliseconds:0.000000},{sample.Position.Value:0.000000},{Wall(sample.Audio.Ended):0.000000},{sample.Audio.Milliseconds:0.000000},{sample.Audio.Value:0.000000},{sample.Speed.Milliseconds:0.000000},{sample.Speed.Value:0.000000},{sample.Avsync.Milliseconds:0.000000},{sample.Avsync.Value * 1000:0.000000},{sample.Idle.Milliseconds:0.000000},{sample.Idle.Value},{sample.Cached.Milliseconds:0.000000},{sample.Cached.Value},{sample.Paused.Milliseconds:0.000000},{sample.Paused.Value},{sample.Position.Error},{sample.Audio.Error},{sample.Speed.Error},{sample.Avsync.Error}"));
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Volatile.Write(ref _stop, true);
            _thread.Join();
            _ = CloseHandle(_timer);
            _ready.Dispose();
        }

        private static double ExpectedWallMs(NumericRead before, NumericRead after, double initialSpeed,
            IReadOnlyList<SpeedTransition> transitions, out bool crossed)
        {
            var speed = initialSpeed;
            var position = before.Value;
            var expected = 0d;
            crossed = false;
            foreach (var change in transitions)
            {
                if (change.Call.Started > after.Ended) break;
                if (change.Call.Started <= before.Ended)
                {
                    speed = change.To;
                    continue;
                }

                var boundary = Math.Clamp(change.Position, position, after.Value);
                expected += (boundary - position) * 1000 / speed;
                position = boundary;
                speed = change.To;
                crossed = true;
            }

            return expected + (after.Value - position) * 1000 / speed;
        }

        private void Run()
        {
            var warmDue = -40000L;
            if (SetWaitableTimer(_timer, ref warmDue, 0, 0, 0, 0) == 0 || WaitForSingleObject(_timer, 100) != 0)
            {
                _timerError = 1;
                _ready.Set();
                return;
            }

            _ = _mpv.Probe();
            _ready.Set();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var interval = Math.Max(1, Stopwatch.Frequency * 4 / 1000);
            var next = Stopwatch.GetTimestamp() + interval;
            while (!Volatile.Read(ref _stop) && _count < Capacity)
            {
                var remaining = Math.Max(1, next - Stopwatch.GetTimestamp());
                var due = -Math.Max(1, (long)Math.Ceiling(remaining * 10000000d / Stopwatch.Frequency));
                if (SetWaitableTimer(_timer, ref due, 0, 0, 0, 0) == 0 || WaitForSingleObject(_timer, 100) != 0)
                {
                    _timerError = 1;
                    break;
                }

                if (Volatile.Read(ref _stop)) break;
                _samples[_count] = _mpv.Probe();
                Volatile.Write(ref _count, _count + 1);
                next += interval;
                var now = Stopwatch.GetTimestamp();
                if (next <= now) next = now + interval;
            }

            LoopAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern int SetWaitableTimer(nint timer, ref long due, int period, nint completion, nint argument, int resume);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern int CloseHandle(nint handle);
    }

    internal sealed record SpeedTransition(double From, double To, double Position, CallResult Call, bool Measured);
    internal sealed record OccupiedEvent(string Property, double StartMs, double EndMs, double ReadMs);
    internal sealed record LagEvent(double EndMs, double PreviousChangeMs, double WallGapMs, double PositionStepSeconds, double ExpectedMs, double LagMs, bool CrossedSpeed);
    internal sealed record WindowMetrics(double MaximumReadMs, int OccupiedCount, double MaximumLagMs, int LagCount,
        double MaximumFrameGapMs, double MaximumPollGapMs, double MaximumAudioGapMs, double MaximumMissingAudioPtsMs,
        int Polls, int IdlePolls, int CachedPolls, int PropertyErrors, int MissingAudioPolls, int AudioRegressions,
        Distribution AvsyncMs, List<OccupiedEvent> OccupiedEvents, List<LagEvent> LagEvents);

    internal sealed record Distribution(double? Minimum, double? Median, double? P95, double? Maximum)
    {
        internal static Distribution Of(IEnumerable<double> source)
        {
            var values = source.Where(double.IsFinite).Order().ToArray();
            return values.Length == 0 ? new(null, null, null, null)
                : new(values[0], Percentile(values, 0.5), Percentile(values, 0.95), values[^1]);
        }

        private static double Percentile(double[] values, double percentile)
        {
            var index = (values.Length - 1) * percentile;
            var low = (int)Math.Floor(index);
            var high = (int)Math.Ceiling(index);
            return values[low] + (values[high] - values[low]) * (index - low);
        }
    }

    internal sealed record Counters(long? FrameDrop, long? VoDelayed, long? DecoderDrop, long? Mistimed)
    {
        internal static Counters Read(NativeMpv mpv) => new(mpv.GetCounter("frame-drop-count"), mpv.GetCounter("vo-delayed-frame-count"),
            mpv.GetCounter("decoder-frame-drop-count"), mpv.GetCounter("mistimed-frame-count"));

        internal Counters Minus(Counters before) => new(FrameDrop - before.FrameDrop, VoDelayed - before.VoDelayed,
            DecoderDrop - before.DecoderDrop, Mistimed - before.Mistimed);
    }
}
