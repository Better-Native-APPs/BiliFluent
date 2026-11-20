// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Resolvers;
using BiliCopilot.UI.Toolkits;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Core.Enums;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
#if DEBUG
using Richasy.MpvKernel.Player;
using Richasy.MpvKernel.Player.Models;
#endif

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private MpvClient? _diagnosticsClient;
    private PlaybackDiagnosticsSession? _playbackDiagnostics;
    private Task _playbackDiagnosticsDrain = Task.CompletedTask;
    private bool _curlDisableRequested;
    private bool _displaySyncRequested;

    internal void RefreshPlaybackDiagnostics(bool startCurrentPlayback = true)
    {
        if (IsPlaybackClosing || !SettingsToolkit.ReadLocalSetting(SettingNames.PlaybackStutterDiagnosticsEnabled, false))
        {
            _ = SuspendPlaybackDiagnosticsAsync();
            return;
        }

        if (Client is not { IsDisposed: false } client) return;
        if (!ReferenceEquals(_diagnosticsClient, client))
        {
            DetachPlaybackDiagnostics();
            _diagnosticsClient = client;
            client.ReachFileLoading += OnDiagnosticsFileLoading;
            client.ReachFileEnd += OnDiagnosticsFileEnd;
            client.Shutdown += OnDiagnosticsFileEnd;
        }

        if (startCurrentPlayback && _playbackDiagnostics is null
            && Player?.PlaybackState is MpvPlayerState.Playing or MpvPlayerState.Paused or MpvPlayerState.Buffering or MpvPlayerState.Seeking)
        {
            StartPlaybackDiagnostics(client);
        }
    }

    private void OnDiagnosticsFileLoading(object? sender, EventArgs args)
    {
        var version = Volatile.Read(ref _mediaVersion);
        queue.TryEnqueue(() =>
        {
            if (IsCurrentPlayback(version) && ReferenceEquals(sender, _diagnosticsClient))
            {
                StartPlaybackDiagnostics(_diagnosticsClient!);
            }
        });
    }

    private void OnDiagnosticsFileEnd(object? sender, EventArgs args)
    {
        var version = Volatile.Read(ref _mediaVersion);
        queue.TryEnqueue(() =>
        {
            if (IsCurrentPlayback(version) && ReferenceEquals(sender, _diagnosticsClient))
            {
                _ = StopPlaybackDiagnosticsAsync();
            }
        });
    }

    private void StartPlaybackDiagnostics(MpvClient client)
    {
        _ = StopPlaybackDiagnosticsAsync();
        try
        {
            var displayWindow = Window is { IsDisposed: false } host ? host.RenderHwnd : IntPtr.Zero;
            _playbackDiagnostics = new PlaybackDiagnosticsSession(client, queue, logger, _curlDisableRequested,
                _displaySyncRequested, displayWindow, DescribeDanmakuForDiagnostics, DescribeNetworkForDiagnostics
#if DEBUG
                , Player
#endif
                );
        }
        catch (Exception ex)
        {
            // Diagnostics are optional; a failed start must never reach playback.
            logger.LogWarning("PlaybackStutter | start failed error={Error}", ex.GetType().Name);
        }
    }

    private Task StopPlaybackDiagnosticsAsync()
    {
        var session = _playbackDiagnostics;
        _playbackDiagnostics = null;
        if (session is not null)
        {
            // Retain every retired worker until it has stopped using the native handle.
            _playbackDiagnosticsDrain = OwnWork(Task.WhenAll(_playbackDiagnosticsDrain, session.DisposeAsync().AsTask()));
        }

        return _playbackDiagnosticsDrain;
    }

    private Task SuspendPlaybackDiagnosticsAsync()
    {
        DetachPlaybackDiagnostics();
        return StopPlaybackDiagnosticsAsync();
    }

    // Read on the UI thread by the heartbeat; both values are constants, never media data.
    private (string Renderer, string Danmaku) DescribeDanmakuForDiagnostics()
        => (PlaybackStutterDiagnostics.RendererName(Danmaku.Renderer), IsDanmakuEnabled && Danmaku.IsShowDanmaku ? "on" : "off");

    private void DetachPlaybackDiagnostics()
    {
        if (_diagnosticsClient is not { } client) return;
        client.ReachFileLoading -= OnDiagnosticsFileLoading;
        client.ReachFileEnd -= OnDiagnosticsFileEnd;
        client.Shutdown -= OnDiagnosticsFileEnd;
        _diagnosticsClient = null;
    }

    private sealed class PlaybackDiagnosticsSession : IAsyncDisposable
    {
        private readonly MpvClient _client;
        private readonly DispatcherQueue _queue;
        private readonly ILogger _logger;
        private readonly DispatcherQueueTimer _heartbeat;
        private readonly PeriodicTimer _samples = new(TimeSpan.FromSeconds(1));
        private static readonly long _uiGapThresholdTicks = Stopwatch.Frequency * 150 / 1000;
        private const int ProbeIntervalMilliseconds = 4;
        private const double MaximumFrameStepSeconds = 2;
        private const double MinimumReportedFrameGapMilliseconds = 50;
        private const double ResumeWatchSeconds = 20; // Samples carry the time since a resume this long.
        private const uint HighResolutionTimer = 0x00000002; // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
        private const uint TimerAllAccess = 0x001F0003; // TIMER_ALL_ACCESS
#if DEBUG
        private const string BuildConfiguration = "Debug";
#else
        private const string BuildConfiguration = "Release";
#endif
        private readonly object _heartbeatLock = new();
        private readonly bool _curlDisableRequested;
        private readonly bool _displaySyncRequested;
        private readonly nint _displayWindow;
        private readonly Func<(string Renderer, string Danmaku)> _describeDanmaku;
        private readonly Func<NetworkDiagnostics> _describeNetwork;
        private readonly Task _worker;
        private readonly Task _frameProbe;
        private readonly object _frameGapLock = new();
        private long _frameGapMaximumTicks;
        private int _frameGapIntervals;
        private int _frameGapOverTwice;
        private int _frameProbePolls;
        private long _frameProbeReadTicks;
        private double _nominalFrameMilliseconds;
        private long _lastHeartbeatTimestamp = Stopwatch.GetTimestamp();
        private long _maximumHeartbeatTicks;
        private int _heartbeatGen2 = GC.CollectionCount(2);
        private TimeSpan _heartbeatGcPause = GC.GetTotalPauseDuration();
        private volatile string _renderer = "n/a";
        private volatile string _danmaku = "n/a";
        private volatile bool _stopped;
        private readonly object _snapshotLock = new();
        private Task _snapshots = Task.CompletedTask;
        private long _pausedAt;
        private long _resumedAt;
#if DEBUG
        private readonly PlaybackStallWatch _stallWatch;
        private readonly MpvPlayer? _stallPlayer;
#endif

        public PlaybackDiagnosticsSession(MpvClient client, DispatcherQueue dispatcherQueue, ILogger diagnosticsLogger,
            bool curlDisableRequested, bool displaySyncRequested, nint displayWindow,
            Func<(string Renderer, string Danmaku)> describeDanmaku, Func<NetworkDiagnostics> describeNetwork
#if DEBUG
            , MpvPlayer? player
#endif
            )
        {
#if DEBUG
            _stallWatch = new PlaybackStallWatch(Path.Combine(
                Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalFolder.Path, "Logger", "stall-dumps"), diagnosticsLogger);
            _stallPlayer = player;
#endif
            _describeNetwork = describeNetwork;
            _displaySyncRequested = displaySyncRequested;
            _displayWindow = displayWindow;
            _client = client;
            _queue = dispatcherQueue;
            _logger = diagnosticsLogger;
            _curlDisableRequested = curlDisableRequested;
            _describeDanmaku = describeDanmaku;
            (_renderer, _danmaku) = describeDanmaku();
            _client.IsTimePositionNotificationCountingEnabled = true;
            _client.PauseChanged += OnPauseChanged;
#if DEBUG
            _client.ReachFileLoading += OnStallSettling;
            _client.ReachFileLoaded += OnStallSettling;
            _client.SeekStarted += OnStallSettling;
            if (_stallPlayer is not null) _stallPlayer.SeamlessSwitchProgress += OnStallSeamlessProgress;
#endif
            _heartbeat = dispatcherQueue.CreateTimer();
            _heartbeat.Interval = TimeSpan.FromMilliseconds(100);
            _heartbeat.Tick += OnHeartbeat;
            _heartbeat.Start();
            PlaybackStutterDiagnostics.Acquire(diagnosticsLogger);
            _worker = Task.Run(SampleAsync);
            // A dedicated thread: a busy thread pool must not look like a frame gap.
            _frameProbe = Task.Factory.StartNew(ProbeFrames, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
#if DEBUG
            _stallWatch.Start();
#endif
        }

        public async ValueTask DisposeAsync()
        {
            Stop();
            // This session owns the actual workers; native teardown must join them, not just stop the timer.
#pragma warning disable VSTHRD003
            Task snapshots;
            lock (_snapshotLock) snapshots = _snapshots;
            await Task.WhenAll(_worker, _frameProbe, snapshots
#if DEBUG
                , _stallWatch.Completion
#endif
                ).ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }

        private void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            PlaybackStutterDiagnostics.Release();
            _client.IsTimePositionNotificationCountingEnabled = false;
            _client.PauseChanged -= OnPauseChanged;
#if DEBUG
            _stallWatch.Stop();
            _client.ReachFileLoading -= OnStallSettling;
            _client.ReachFileLoaded -= OnStallSettling;
            _client.SeekStarted -= OnStallSettling;
            if (_stallPlayer is not null) _stallPlayer.SeamlessSwitchProgress -= OnStallSeamlessProgress;
#endif
            _samples.Dispose();
            _heartbeat.Tick -= OnHeartbeat;
            try
            {
                _heartbeat.Stop();
            }
            catch (Exception ex)
            {
                // The dispatcher may already be gone during shutdown; the worker is released above.
                _logger.LogWarning("PlaybackStutter | heartbeat stop failed error={Error}", ex.GetType().Name);
            }
        }

        private void OnHeartbeat(DispatcherQueueTimer sender, object args)
        {
            var timestamp = Stopwatch.GetTimestamp();
            var gen2 = GC.CollectionCount(2);
            var gcPause = GC.GetTotalPauseDuration();
            long gapTicks;
            lock (_heartbeatLock)
            {
                gapTicks = timestamp - _lastHeartbeatTimestamp;
                _maximumHeartbeatTicks = Math.Max(_maximumHeartbeatTicks, gapTicks);
                _lastHeartbeatTimestamp = timestamp;
            }

            // Written immediately, so that the gap lines up with the slow lines around it.
            if (gapTicks > _uiGapThresholdTicks && _logger.IsEnabled(LogLevel.Information))
            {
                var gap = PlaybackStutterDiagnostics.FormatMilliseconds(gapTicks * 1000d / Stopwatch.Frequency);
                var gen2Delta = gen2 - _heartbeatGen2;
                var pauseDelta = PlaybackStutterDiagnostics.FormatMilliseconds((gcPause - _heartbeatGcPause).TotalMilliseconds);
                _logger.LogInformation("PlaybackStutter | ui-gap ms={Ms} gc2={Gen2Delta} gc-pause-ms={GcPauseMs}", gap, gen2Delta, pauseDelta);
            }

            _heartbeatGen2 = gen2;
            _heartbeatGcPause = gcPause;
            (_renderer, _danmaku) = _describeDanmaku();
        }

        // Raised on the mpv event thread the moment the pause changes. The state is read right away
        // on a worker: a read that hangs in the audio output must not hold up the event loop.
        private void OnPauseChanged(object? sender, bool paused)
        {
            if (_stopped) return;
            var timestamp = Stopwatch.GetTimestamp();
            var pausedSeconds = "n/a";
            if (paused)
            {
                Interlocked.Exchange(ref _pausedAt, timestamp);
            }
            else
            {
                var pausedAt = Interlocked.Exchange(ref _pausedAt, 0);
                Volatile.Write(ref _resumedAt, timestamp);
#if DEBUG
                _stallWatch.NotifySettling(timestamp);
#endif
                if (pausedAt != 0)
                    pausedSeconds = Stopwatch.GetElapsedTime(pausedAt, timestamp).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
            }

            var name = paused ? "pause-snapshot" : "resume-snapshot";
            lock (_snapshotLock)
            {
                // One at a time and in order; DisposeAsync joins the last one before native teardown.
                var previous = _snapshots;
                _snapshots = Task.Run(async () =>
                {
#pragma warning disable VSTHRD003
                    await previous.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                    if (_stopped) return;
                    try
                    {
                        WriteSnapshot(name, pausedSeconds);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("PlaybackStutter | snapshot failed error={Error}", ex.GetType().Name);
                    }
                });
            }
        }

#if DEBUG
        private void OnStallSettling(object? sender, EventArgs args) => _stallWatch.NotifySettling();

        // Covers both video and audio tier changes, including their final selection phase.
        // Observe the existing library event directly; a busy UI must not delay suppression.
        private void OnStallSeamlessProgress(object? sender, MpvSeamlessSwitchEventArgs args) => _stallWatch.NotifySettling();
#endif

        private void WriteSnapshot(string name, string pausedSeconds)
        {
            if (!_logger.IsEnabled(LogLevel.Information)) return;
            var network = _describeNetwork();
            var position = ReadDouble("time-pos");
            var audioPosition = ReadDouble("audio-pts");
            var positionText = FormatNumber(position, "0.###");
            var audioPositionText = FormatNumber(audioPosition, "0.###");
            // Positive when the audio position is ahead of the picture.
            var audioLead = FormatNumber((audioPosition - position) * 1000d, "0");
            var coreIdle = ReadFlag("core-idle");
            var pausedForCache = ReadFlag("paused-for-cache");
            var audioOutput = ReadText("current-ao");
            // The name of a chosen device stays local; only whether one was chosen is logged.
            var device = _client.GetPropertyString("audio-device");
            var audioDevice = string.IsNullOrEmpty(device) ? "n/a" : device == "auto" ? "auto" : "set";
            var audioParams = ReadCounter("audio-params/samplerate").HasValue ? "yes" : "no";
            var outputRate = ReadCounter("audio-out-params/samplerate");
            var outputParams = outputRate.HasValue ? "yes" : "no";
            var outputRateText = outputRate?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
            var outputChannels = ReadCounter("audio-out-params/channel-count")?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
            var mute = ReadFlag("mute");
            var volume = FormatNumber(ReadDouble("volume"), "0.#");
            var videoCache = FormatNumber(network.VideoCacheSeconds, "0.###");
            var audioCache = FormatNumber(network.AudioCacheSeconds, "0.###");
            var forwardCache = FormatNumber(ReadDouble("demuxer-cache-duration"), "0.###");
            var avsync = FormatNumber(ReadDouble("avsync") * 1000d, "0.00");
            var cdn = network.VideoCdn + "/" + network.AudioCdn;
            // Asked of the audio device itself, so they come last: everything above is read by then.
            var outputVolume = FormatNumber(ReadDouble("ao-volume"), "0.#");
            var outputMute = ReadFlag("ao-mute");
            _logger.LogInformation(
                "PlaybackStutter | {Name} paused-secs={PausedSecs} time-pos={TimePos} audio-pts={AudioPts} audio-lead-ms={AudioLeadMs} core-idle={CoreIdle} paused-for-cache={PausedForCache} current-ao={Ao} audio-device={AudioDevice} audio-params={AudioParams} audio-out-params={AudioOutParams} ao-samplerate={AoSampleRate} ao-channels={AoChannels} ao-volume={AoVolume} ao-mute={AoMute} mute={Mute} volume={Volume} v-cache-secs={VCacheSecs} a-cache-secs={ACacheSecs} fw-cache-secs={FwCacheSecs} avsync-ms={AvsyncMs} cdn={Cdn}",
                name, pausedSeconds, positionText, audioPositionText, audioLead, coreIdle, pausedForCache, audioOutput,
                audioDevice, audioParams, outputParams, outputRateText, outputChannels, outputVolume, outputMute, mute, volume,
                videoCache, audioCache, forwardCache, avsync, cdn);
        }

        private string ReadFlag(string property)
            => _client.GetProperty(property, out bool value) == MpvError.Success ? value ? "yes" : "no" : "n/a";

        private double ReadMaximumHeartbeatInterval(long timestamp)
        {
            lock (_heartbeatLock)
            {
                // Include an unfinished gap, so a blocked UI is visible in the same second.
                var ticks = Math.Max(_maximumHeartbeatTicks, timestamp - _lastHeartbeatTimestamp);
                _maximumHeartbeatTicks = 0;
                return ticks * 1000d / Stopwatch.Frequency;
            }
        }

        private async Task SampleAsync()
        {
            try
            {
                if (_stopped) return;
                var previousCounters = ReadFrameCounters();
                var previousNotifications = _client.TimePositionNotificationCount;
                var previousUiPosts = _client.UiPostCount;
                var previousRuntime = ReadRuntimeCounters();
                var previousAvsyncChange = ReadDouble("total-avsync-change");
                var previousTimestamp = Stopwatch.GetTimestamp();
                var previousForwardBytes = _describeNetwork().ForwardBytes;
                var previousAudioPosition = ReadDouble("audio-pts");
                var curlEnabled = ReadText("options/curl-enabled");
                var videoSyncOption = ReadText("options/video-sync");
                var (displayHertz, displayCount) = ReadDisplayInfo(_displayWindow);
                UpdateNominalFrameInterval(ReadDouble("estimated-vf-fps"), ReadDouble("container-fps"));
                if (_stopped) return;
                var curlDisableRequested = _curlDisableRequested ? "yes" : "no";
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    var serverGc = GCSettings.IsServerGC ? "yes" : "no";
                    var latencyMode = GCSettings.LatencyMode.ToString();
                    var concurrentGc = ReadConcurrentGc();
                    var displaySyncRequested = _displaySyncRequested ? "yes" : "no";
                    _logger.LogInformation(
                        "PlaybackStutter | start curl-enabled={CurlEnabled} curl-disable-requested={CurlDisableRequested} ui-heartbeat-interval-ms=100 build={Build} server-gc={ServerGc} gc-latency-mode={LatencyMode} gc-concurrent={ConcurrentGc} video-sync={VideoSync} display-sync-requested={DisplaySyncRequested} display-hz={DisplayHz} displays={Displays}",
                        curlEnabled, curlDisableRequested, BuildConfiguration, serverGc, latencyMode, concurrentGc,
                        videoSyncOption, displaySyncRequested, displayHertz, displayCount);
                }

                while (await _samples.WaitForNextTickAsync().ConfigureAwait(false))
                {
                    if (_stopped) return;
                    var timestamp = Stopwatch.GetTimestamp();
                    var notifications = _client.TimePositionNotificationCount;
                    var uiPosts = _client.UiPostCount;
                    var runtime = ReadRuntimeCounters();
                    var avsyncChange = ReadDouble("total-avsync-change");
                    var counters = ReadFrameCounters();
                    var heartbeatInterval = ReadMaximumHeartbeatInterval(timestamp);
                    var network = _describeNetwork();
                    var audioPosition = ReadDouble("audio-pts");
                    if (_client.GetProperty("pause", out bool userPaused) == MpvError.Success && userPaused)
                    {
                        previousForwardBytes = network.ForwardBytes;
                        previousAudioPosition = audioPosition;
                        // Nothing is being presented while the user has paused; keep the baselines current.
                        previousCounters = counters;
                        previousNotifications = notifications;
                        previousUiPosts = uiPosts;
                        previousRuntime = runtime;
                        previousAvsyncChange = avsyncChange;
                        previousTimestamp = timestamp;
                        _ = TakeFrameGapStatistics();
                        continue;
                    }

                    var pausedForCache = _client.GetProperty("paused-for-cache", out bool paused) == MpvError.Success
                        ? paused ? "yes" : "no" : "n/a";
                    var bufferingState = ReadCounter("cache-buffering-state")?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
                    var cacheDuration = _client.GetProperty("demuxer-cache-duration", out double duration) == MpvError.Success && double.IsFinite(duration)
                        ? duration.ToString("0.###", CultureInfo.InvariantCulture) : "n/a";
                    var hwdec = ReadText("hwdec-current");
                    var vo = ReadText("current-vo");
                    var displayFps = FormatNumber(ReadDouble("display-fps"), "0.###");
                    var containerFpsValue = ReadDouble("container-fps");
                    var filterFpsValue = ReadDouble("estimated-vf-fps");
                    var containerFps = FormatNumber(containerFpsValue, "0.###");
                    var filterFps = FormatNumber(filterFpsValue, "0.###");
                    // The statistics of this second were judged against the previous nominal interval.
                    var frameGaps = TakeFrameGapStatistics();
                    var hadNominal = Volatile.Read(ref _nominalFrameMilliseconds) > 0;
                    UpdateNominalFrameInterval(filterFpsValue, containerFpsValue);
                    var frameGapMaximum = frameGaps.Intervals > 0
                        ? PlaybackStutterDiagnostics.FormatMilliseconds(frameGaps.MaximumTicks * 1000d / Stopwatch.Frequency) : "n/a";
                    var frameGapOverTwice = hadNominal ? frameGaps.OverTwice.ToString(CultureInfo.InvariantCulture) : "n/a";
                    var probePolls = frameGaps.Polls;
                    var probeRead = PlaybackStutterDiagnostics.FormatMilliseconds(frameGaps.ReadTicks * 1000d / Stopwatch.Frequency);
                    var estimatedDisplayFps = FormatNumber(ReadDouble("estimated-display-fps"), "0.###");
                    var videoSync = ReadText("video-sync");
                    var avsync = FormatNumber(ReadDouble("avsync") * 1000d, "0.00");
                    var avsyncChangeDelta = FormatNumber((avsyncChange - previousAvsyncChange) * 1000d, "0.00");
                    var vsyncJitter = FormatNumber(ReadDouble("vsync-jitter"), "0.####");
                    var vsyncRatio = FormatNumber(ReadDouble("vsync-ratio"), "0.###");
                    var hasPassTimes = _client.GetFreshPassTimes(out var passPeak, out var passAverage) == MpvError.Success;
                    var freshPeak = hasPassTimes ? (passPeak / 1e6).ToString("0.0", CultureInfo.InvariantCulture) : "n/a";
                    var freshAverage = hasPassTimes ? (passAverage / 1e6).ToString("0.0", CultureInfo.InvariantCulture) : "n/a";
                    var interval = Stopwatch.GetElapsedTime(previousTimestamp, timestamp).TotalMilliseconds;
                    if (_stopped) return;

                    var intervalText = interval.ToString("0.0", CultureInfo.InvariantCulture);
                    var frameDropDelta = CounterDelta(counters.FrameDrop, previousCounters.FrameDrop);
                    var voDelayedDelta = CounterDelta(counters.VoDelayed, previousCounters.VoDelayed);
                    var mistimedDelta = CounterDelta(counters.Mistimed, previousCounters.Mistimed);
                    var decoderDropDelta = CounterDelta(counters.DecoderDrop, previousCounters.DecoderDrop);
                    var notificationDelta = notifications - previousNotifications;
                    var heartbeatText = heartbeatInterval.ToString("0.0", CultureInfo.InvariantCulture);
                    var gen0Delta = runtime.Gen0 - previousRuntime.Gen0;
                    var gen1Delta = runtime.Gen1 - previousRuntime.Gen1;
                    var gen2Delta = runtime.Gen2 - previousRuntime.Gen2;
                    var gcPauseText = (runtime.Pause - previousRuntime.Pause).TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture);
                    var allocatedText = ((runtime.Allocated - previousRuntime.Allocated) / 1048576d).ToString("0.0", CultureInfo.InvariantCulture);
                    var uiPostDelta = uiPosts - previousUiPosts;
                    var renderer = _renderer;
                    var danmaku = _danmaku;
                    var cdn = network.VideoCdn + "/" + network.AudioCdn;
                    var quality = network.Quality?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
                    var videoKbps = network.VideoBandwidth > 0 ? (network.VideoBandwidth / 1000).ToString(CultureInfo.InvariantCulture) : "n/a";
                    var audioKbps = network.AudioBandwidth > 0 ? (network.AudioBandwidth / 1000).ToString(CultureInfo.InvariantCulture) : "n/a";
                    var rawInputRate = network.RawInputRate;
                    var forwardBytesDelta = network.ForwardBytes - previousForwardBytes;
                    var cdnSwitches = network.Switches;
                    // Both rates are about the main file, the only one mpv measures: what playback takes
                    // from it at the current speed, and what was downloaded of it.
                    var requiredKbps = network.MainBandwidth > 0
                        ? (network.MainBandwidth / 1000d * (ReadDouble("speed") is > 0 and var playbackSpeed ? playbackSpeed : 1d)).ToString("0", CultureInfo.InvariantCulture)
                        : "n/a";
                    var downloadKbps = FormatNumber(network.DownloadRate * 8 / 1000, "0");
                    var audioPositionText = FormatNumber(audioPosition, "0.###");
                    var audioCache = FormatNumber(network.AudioCacheSeconds, "0.###");
                    var videoCache = FormatNumber(network.VideoCacheSeconds, "0.###");
                    var watchableCache = FormatNumber(network.WatchableCacheSeconds, "0.###");
                    var skippedAhead = FormatNumber(network.SkippedAheadSeconds, "0.###");
                    var cacheExtra = FormatNumber(network.CacheExtraMegabytes, "0.###");
                    // Whether sound is being played: an audio position that stands still means no audio data.
                    var audioAdvancing = audioPosition.HasValue && previousAudioPosition.HasValue
                        ? audioPosition > previousAudioPosition ? "yes" : "no" : "n/a";
                    var resumedAt = Volatile.Read(ref _resumedAt);
                    var sinceResume = resumedAt != 0 && Stopwatch.GetElapsedTime(resumedAt, timestamp).TotalSeconds is <= ResumeWatchSeconds and var resumedFor
                        ? resumedFor.ToString("0.0", CultureInfo.InvariantCulture) : "n/a";
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation(
                            "PlaybackStutter | sample interval-ms={IntervalMs} frame-drop-count-delta={FrameDropDelta} vo-delayed-frame-count-delta={VoDelayedDelta} mistimed-frame-count-delta={MistimedDelta} decoder-frame-drop-count-delta={DecoderDropDelta} paused-for-cache={PausedForCache} cache-buffering-state={BufferingState} demuxer-cache-duration={CacheDuration} hwdec-current={Hwdec} current-vo={Vo} time-pos-notifications={TimePosNotifications} ui-heartbeat-max-ms={HeartbeatMaxMs} gc0-delta={Gen0Delta} gc1-delta={Gen1Delta} gc2-delta={Gen2Delta} gc-pause-ms={GcPauseMs} alloc-mb={AllocatedMb} ui-posts={UiPosts} renderer={Renderer} danmaku={Danmaku} display-fps={DisplayFps} container-fps={ContainerFps} estimated-vf-fps={FilterFps} estimated-display-fps={EstimatedDisplayFps} video-sync={VideoSync} avsync-ms={AvsyncMs} total-avsync-change-delta-ms={AvsyncChangeDeltaMs} vsync-jitter={VsyncJitter} vsync-ratio={VsyncRatio} vo-fresh-peak-ms={FreshPeakMs} vo-fresh-avg-ms={FreshAverageMs} frame-gap-max-ms={FrameGapMaxMs} frame-gap-over2x={FrameGapOverTwice} frame-probe-polls={FrameProbePolls} frame-probe-read-ms={FrameProbeReadMs} cdn={Cdn} qn={Quality} v-kbps={VideoKbps} a-kbps={AudioKbps} raw-input-rate={RawInputRate} fw-bytes-delta={ForwardBytesDelta} cdn-switches={CdnSwitches} fw-cache-secs={ForwardCacheSecs} dl-kbps={DownloadKbps} need-kbps={RequiredKbps} audio-pts={AudioPts} a-cache-secs={AudioCacheSecs} v-cache-secs={VideoCacheSecs} audio-advancing={AudioAdvancing} since-resume-secs={SinceResumeSecs} abr-est-kbps={AbrEstimateKbps} abr-obs-kbps={AbrObservationKbps} abr-obs-kind={AbrObservationKind} abr-mode={AbrMode} fw-watchable-secs={WatchableSecs} skip-ahead-secs={SkipAheadSecs} cache-extra-mb={CacheExtraMb}",
                            intervalText, frameDropDelta, voDelayedDelta, mistimedDelta, decoderDropDelta,
                            pausedForCache, bufferingState, cacheDuration, hwdec, vo, notificationDelta, heartbeatText,
                            gen0Delta, gen1Delta, gen2Delta, gcPauseText, allocatedText, uiPostDelta, renderer, danmaku,
                            displayFps, containerFps, filterFps, estimatedDisplayFps, videoSync, avsync, avsyncChangeDelta,
                            vsyncJitter, vsyncRatio, freshPeak, freshAverage,
                            frameGapMaximum, frameGapOverTwice, probePolls, probeRead,
                            cdn, quality, videoKbps, audioKbps, rawInputRate, forwardBytesDelta, cdnSwitches,
                            cacheDuration, downloadKbps, requiredKbps, audioPositionText, audioCache, videoCache,
                            audioAdvancing, sinceResume,
                            FormatNumber(network.AdaptiveEstimate / AdaptiveQualityThresholds.BitsPerKilobit, "0"),
                            FormatNumber(network.AdaptiveObservation / AdaptiveQualityThresholds.BitsPerKilobit, "0"), network.AdaptiveObservationKind, network.AdaptiveMode,
                            watchableCache, skippedAhead, cacheExtra);
                    }

                    previousForwardBytes = network.ForwardBytes;
                    previousAudioPosition = audioPosition;

                    previousCounters = counters;
                    previousNotifications = notifications;
                    previousUiPosts = uiPosts;
                    previousRuntime = runtime;
                    previousAvsyncChange = avsyncChange;
                    previousTimestamp = timestamp;
                }
            }
            catch (Exception ex)
            {
                // Diagnostic failures must not affect playback or expose native/media exception details.
                _logger.LogWarning("PlaybackStutter | stopped error={Error}", ex.GetType().Name);
                _queue.TryEnqueue(Stop);
            }
        }

        private (long? FrameDrop, long? VoDelayed, long? Mistimed, long? DecoderDrop) ReadFrameCounters()
            => (ReadCounter("frame-drop-count"), ReadCounter("vo-delayed-frame-count"),
                ReadCounter("mistimed-frame-count"), ReadCounter("decoder-frame-drop-count"));

        // time-pos is the timestamp of the video frame that mpv handed to the video output last, so
        // it changes once per frame. The interval between two changes is the pace at which frames
        // are queued; how many refreshes the display then holds each frame is not visible here.
        private void ProbeFrames()
        {
            Thread.CurrentThread.Name = "PlaybackFrameProbe";
            var timer = CreateWaitableTimerExW(0, 0, HighResolutionTimer, TimerAllAccess);
            try
            {
                var hasPosition = false;
                var hasChange = false;
                var lastPosition = 0d;
                var lastChange = 0L;
                while (!_stopped)
                {
                    WaitForNextProbe(timer);
                    if (_stopped) return;
                    var readStarted = Stopwatch.GetTimestamp();
                    var position = 0d;
#if DEBUG
                    var playing = ReadProbeIdle(out bool idle) == MpvError.Success && !idle
                        && ReadProbePosition(out position) == MpvError.Success && double.IsFinite(position);
#else
                    var playing = _client.GetProperty("core-idle", out bool idle) == MpvError.Success && !idle
                        && _client.GetProperty("time-pos", out position) == MpvError.Success && double.IsFinite(position);
#endif
                    var timestamp = Stopwatch.GetTimestamp();
#if DEBUG
                    _stallWatch.Observe(playing, position, timestamp);
#endif
                    lock (_frameGapLock)
                    {
                        _frameProbePolls++;
                        _frameProbeReadTicks += timestamp - readStarted;
                    }

                    if (!playing)
                    {
                        // Paused, seeking, buffering or ended: what follows is not a frame interval.
                        hasPosition = false;
                        hasChange = false;
                        continue;
                    }

                    if (!hasPosition)
                    {
                        lastPosition = position;
                        hasPosition = true;
                        continue;
                    }

                    if (position == lastPosition) continue;
                    var step = position - lastPosition;
                    var gapTicks = timestamp - lastChange;
                    lastPosition = position;
                    lastChange = timestamp;
                    if (!hasChange || step < 0 || step > MaximumFrameStepSeconds)
                    {
                        // The first change after an interruption, or a jump, only starts the next interval.
                        hasChange = true;
                        continue;
                    }

                    var gap = gapTicks * 1000d / Stopwatch.Frequency;
                    var nominal = Volatile.Read(ref _nominalFrameMilliseconds);
                    lock (_frameGapLock)
                    {
                        _frameGapIntervals++;
                        _frameGapMaximumTicks = Math.Max(_frameGapMaximumTicks, gapTicks);
                        if (nominal > 0 && gap > 2 * nominal) _frameGapOverTwice++;
                    }

                    if (gap > Math.Max(MinimumReportedFrameGapMilliseconds, 2.5 * nominal) && _logger.IsEnabled(LogLevel.Information))
                    {
                        var gapText = PlaybackStutterDiagnostics.FormatMilliseconds(gap);
                        var nominalText = nominal > 0 ? PlaybackStutterDiagnostics.FormatMilliseconds(nominal) : "n/a";
                        var positionText = position.ToString("0.0", CultureInfo.InvariantCulture);
                        var stepText = PlaybackStutterDiagnostics.FormatMilliseconds(step * 1000d);
                        _logger.LogInformation("PlaybackStutter | frame-gap ms={Ms} nominal-ms={NominalMs} pos={Position} pts-step-ms={StepMs}",
                            gapText, nominalText, positionText, stepText);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("PlaybackStutter | frame probe stopped error={Error}", ex.GetType().Name);
            }
            finally
            {
                if (timer != 0) _ = CloseHandle(timer);
            }
        }

#if DEBUG
        private MpvError ReadProbeIdle(out bool idle)
        {
            _stallWatch.BeginRead();
            try { return _client.GetProperty("core-idle", out idle); }
            finally { _stallWatch.EndRead(); }
        }

        private MpvError ReadProbePosition(out double position)
        {
            _stallWatch.BeginRead();
            try { return _client.GetProperty("time-pos", out position); }
            finally { _stallWatch.EndRead(); }
        }
#endif

        private static void WaitForNextProbe(nint timer)
        {
            var due = -ProbeIntervalMilliseconds * 10000L; // Relative, in 100 ns units.
            if (timer != 0 && SetWaitableTimer(timer, ref due, 0, 0, 0, 0) != 0)
            {
                _ = WaitForSingleObject(timer, 100);
                return;
            }

            // Without a high resolution timer this follows the system timer; frame-probe-polls shows the rate.
            Thread.Sleep(ProbeIntervalMilliseconds);
        }

        private (long MaximumTicks, int Intervals, int OverTwice, int Polls, long ReadTicks) TakeFrameGapStatistics()
        {
            lock (_frameGapLock)
            {
                var statistics = (_frameGapMaximumTicks, _frameGapIntervals, _frameGapOverTwice, _frameProbePolls, _frameProbeReadTicks);
                _frameGapMaximumTicks = 0;
                _frameGapIntervals = 0;
                _frameGapOverTwice = 0;
                _frameProbePolls = 0;
                _frameProbeReadTicks = 0;
                return statistics;
            }
        }

        // The measured rate of the decoded frames, or else the container's; at the current playback speed.
        private void UpdateNominalFrameInterval(double? filterFps, double? containerFps)
        {
            var fps = filterFps > 0 ? filterFps : containerFps;
            var speed = ReadDouble("speed") is > 0 and var value ? value : 1d;
            Volatile.Write(ref _nominalFrameMilliseconds, fps > 0 ? 1000d / (fps.Value * speed) : 0d);
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll")]
        private static extern int SetWaitableTimer(nint timer, ref long dueTime, int period, nint completionRoutine, nint argument, int resume);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll")]
        private static extern int CloseHandle(nint handle);

        private static (int Gen0, int Gen1, int Gen2, TimeSpan Pause, long Allocated) ReadRuntimeCounters()
            => (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                GC.GetTotalPauseDuration(), GC.GetTotalAllocatedBytes(false));

        // The runtime setting when it is published; otherwise Batch is the mode without concurrency.
        private static string ReadConcurrentGc()
            => (AppContext.TryGetSwitch("System.GC.Concurrent", out var enabled)
                ? enabled : GCSettings.LatencyMode != GCLatencyMode.Batch) ? "yes" : "no";

        // The refresh rate of the display that holds the player, and the number of displays.
        // Device names stay local; only the two numbers are logged.
        private static unsafe (string Hertz, string Count) ReadDisplayInfo(nint window)
        {
            const int MonitorCountMetric = 80; // SM_CMONITORS
            const int NearestMonitor = 2; // MONITOR_DEFAULTTONEAREST
            const int CurrentSettings = -1; // ENUM_CURRENT_SETTINGS
            const int MonitorInfoSize = 104; // MONITORINFOEXW
            const int DeviceNameOffset = 40; // MONITORINFOEXW.szDevice
            const int DisplayModeSize = 220; // DEVMODEW
            const int DisplayModeSizeOffset = 68; // DEVMODEW.dmSize
            const int DisplayFrequencyOffset = 184; // DEVMODEW.dmDisplayFrequency

            var monitors = GetSystemMetrics(MonitorCountMetric);
            var count = monitors > 0 ? monitors.ToString(CultureInfo.InvariantCulture) : "n/a";
            var monitor = window == 0 ? 0 : MonitorFromWindow(window, NearestMonitor);
            if (monitor == 0) return ("n/a", count);

            var info = stackalloc byte[MonitorInfoSize];
            new Span<byte>(info, MonitorInfoSize).Clear();
            *(uint*)info = MonitorInfoSize;
            if (GetMonitorInfoW(monitor, info) == 0) return ("n/a", count);

            var mode = stackalloc byte[DisplayModeSize];
            new Span<byte>(mode, DisplayModeSize).Clear();
            *(ushort*)(mode + DisplayModeSizeOffset) = DisplayModeSize;
            if (EnumDisplaySettingsW((char*)(info + DeviceNameOffset), CurrentSettings, mode) == 0) return ("n/a", count);

            // 0 and 1 stand for the hardware default rate, which is not a number of hertz.
            var hertz = *(uint*)(mode + DisplayFrequencyOffset);
            return (hertz > 1 ? hertz.ToString(CultureInfo.InvariantCulture) : "n/a", count);
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll")]
        private static extern nint MonitorFromWindow(nint window, int flags);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern unsafe int GetMonitorInfoW(nint monitor, byte* info);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern unsafe int EnumDisplaySettingsW(char* deviceName, int modeIndex, byte* mode);

        private double? ReadDouble(string property)
            => _client.GetProperty(property, out double value) == MpvError.Success && double.IsFinite(value) ? value : null;

        private static string FormatNumber(double? value, string format)
            => value?.ToString(format, CultureInfo.InvariantCulture) ?? "n/a";

        private long? ReadCounter(string property)
            => _client.GetProperty(property, out long value) == MpvError.Success ? value : null;

        private string ReadText(string property)
        {
            var value = _client.GetPropertyString(property);
            return string.IsNullOrEmpty(value) ? "n/a" : value;
        }

        private static string CounterDelta(long? current, long? previous)
        {
            if (!current.HasValue || !previous.HasValue) return "n/a";
            // mpv can reset counters on reload; do not turn that into a negative drop count.
            var delta = current.Value >= previous.Value ? current.Value - previous.Value : current.Value;
            return delta.ToString(CultureInfo.InvariantCulture);
        }
    }
}
