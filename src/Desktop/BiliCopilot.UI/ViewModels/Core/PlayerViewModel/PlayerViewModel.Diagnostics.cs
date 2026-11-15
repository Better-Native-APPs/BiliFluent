// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
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
                _displaySyncRequested, displayWindow, DescribeDanmakuForDiagnostics);
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
        private readonly Task _worker;
        private long _lastHeartbeatTimestamp = Stopwatch.GetTimestamp();
        private long _maximumHeartbeatTicks;
        private int _heartbeatGen2 = GC.CollectionCount(2);
        private TimeSpan _heartbeatGcPause = GC.GetTotalPauseDuration();
        private volatile string _renderer = "n/a";
        private volatile string _danmaku = "n/a";
        private volatile bool _stopped;

        public PlaybackDiagnosticsSession(MpvClient client, DispatcherQueue dispatcherQueue, ILogger diagnosticsLogger,
            bool curlDisableRequested, bool displaySyncRequested, nint displayWindow,
            Func<(string Renderer, string Danmaku)> describeDanmaku)
        {
            _displaySyncRequested = displaySyncRequested;
            _displayWindow = displayWindow;
            _client = client;
            _queue = dispatcherQueue;
            _logger = diagnosticsLogger;
            _curlDisableRequested = curlDisableRequested;
            _describeDanmaku = describeDanmaku;
            (_renderer, _danmaku) = describeDanmaku();
            _client.IsTimePositionNotificationCountingEnabled = true;
            _heartbeat = dispatcherQueue.CreateTimer();
            _heartbeat.Interval = TimeSpan.FromMilliseconds(100);
            _heartbeat.Tick += OnHeartbeat;
            _heartbeat.Start();
            PlaybackStutterDiagnostics.Acquire(diagnosticsLogger);
            _worker = Task.Run(SampleAsync);
        }

        public async ValueTask DisposeAsync()
        {
            Stop();
            // This session owns the actual worker; native teardown must join it, not just stop the timer.
#pragma warning disable VSTHRD003
            await _worker.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }

        private void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            PlaybackStutterDiagnostics.Release();
            _client.IsTimePositionNotificationCountingEnabled = false;
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
                var curlEnabled = ReadText("options/curl-enabled");
                var videoSyncOption = ReadText("options/video-sync");
                var (displayHertz, displayCount) = ReadDisplayInfo(_displayWindow);
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
                    if (_client.GetProperty("pause", out bool userPaused) == MpvError.Success && userPaused)
                    {
                        // Nothing is being presented while the user has paused; keep the baselines current.
                        previousCounters = counters;
                        previousNotifications = notifications;
                        previousUiPosts = uiPosts;
                        previousRuntime = runtime;
                        previousAvsyncChange = avsyncChange;
                        previousTimestamp = timestamp;
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
                    var containerFps = FormatNumber(ReadDouble("container-fps"), "0.###");
                    var filterFps = FormatNumber(ReadDouble("estimated-vf-fps"), "0.###");
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
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation(
                            "PlaybackStutter | sample interval-ms={IntervalMs} frame-drop-count-delta={FrameDropDelta} vo-delayed-frame-count-delta={VoDelayedDelta} mistimed-frame-count-delta={MistimedDelta} decoder-frame-drop-count-delta={DecoderDropDelta} paused-for-cache={PausedForCache} cache-buffering-state={BufferingState} demuxer-cache-duration={CacheDuration} hwdec-current={Hwdec} current-vo={Vo} time-pos-notifications={TimePosNotifications} ui-heartbeat-max-ms={HeartbeatMaxMs} gc0-delta={Gen0Delta} gc1-delta={Gen1Delta} gc2-delta={Gen2Delta} gc-pause-ms={GcPauseMs} alloc-mb={AllocatedMb} ui-posts={UiPosts} renderer={Renderer} danmaku={Danmaku} display-fps={DisplayFps} container-fps={ContainerFps} estimated-vf-fps={FilterFps} estimated-display-fps={EstimatedDisplayFps} video-sync={VideoSync} avsync-ms={AvsyncMs} total-avsync-change-delta-ms={AvsyncChangeDeltaMs} vsync-jitter={VsyncJitter} vsync-ratio={VsyncRatio} vo-fresh-peak-ms={FreshPeakMs} vo-fresh-avg-ms={FreshAverageMs}",
                            intervalText, frameDropDelta, voDelayedDelta, mistimedDelta, decoderDropDelta,
                            pausedForCache, bufferingState, cacheDuration, hwdec, vo, notificationDelta, heartbeatText,
                            gen0Delta, gen1Delta, gen2Delta, gcPauseText, allocatedText, uiPostDelta, renderer, danmaku,
                            displayFps, containerFps, filterFps, estimatedDisplayFps, videoSync, avsync, avsyncChangeDelta,
                            vsyncJitter, vsyncRatio, freshPeak, freshAverage);
                    }

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
