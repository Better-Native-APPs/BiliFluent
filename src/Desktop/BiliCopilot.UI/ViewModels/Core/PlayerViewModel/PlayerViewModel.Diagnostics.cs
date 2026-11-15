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

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private MpvClient? _diagnosticsClient;
    private PlaybackDiagnosticsSession? _playbackDiagnostics;
    private Task _playbackDiagnosticsDrain = Task.CompletedTask;
    private bool _curlDisableRequested;

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
            _playbackDiagnostics = new PlaybackDiagnosticsSession(client, queue, logger, _curlDisableRequested);
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
        private readonly object _heartbeatLock = new();
        private readonly bool _curlDisableRequested;
        private readonly Task _worker;
        private long _lastHeartbeatTimestamp = Stopwatch.GetTimestamp();
        private long _maximumHeartbeatTicks;
        private volatile bool _stopped;

        public PlaybackDiagnosticsSession(MpvClient client, DispatcherQueue dispatcherQueue, ILogger diagnosticsLogger, bool curlDisableRequested)
        {
            _client = client;
            _queue = dispatcherQueue;
            _logger = diagnosticsLogger;
            _curlDisableRequested = curlDisableRequested;
            _client.IsTimePositionNotificationCountingEnabled = true;
            _heartbeat = dispatcherQueue.CreateTimer();
            _heartbeat.Interval = TimeSpan.FromMilliseconds(100);
            _heartbeat.Tick += OnHeartbeat;
            _heartbeat.Start();
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
            lock (_heartbeatLock)
            {
                _maximumHeartbeatTicks = Math.Max(_maximumHeartbeatTicks, timestamp - _lastHeartbeatTimestamp);
                _lastHeartbeatTimestamp = timestamp;
            }
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
                var previousTimestamp = Stopwatch.GetTimestamp();
                var curlEnabled = ReadText("options/curl-enabled");
                if (_stopped) return;
                var curlDisableRequested = _curlDisableRequested ? "yes" : "no";
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "PlaybackStutter | start curl-enabled={CurlEnabled} curl-disable-requested={CurlDisableRequested} ui-heartbeat-interval-ms=100",
                        curlEnabled, curlDisableRequested);
                }

                while (await _samples.WaitForNextTickAsync().ConfigureAwait(false))
                {
                    if (_stopped) return;
                    var timestamp = Stopwatch.GetTimestamp();
                    var notifications = _client.TimePositionNotificationCount;
                    var counters = ReadFrameCounters();
                    var heartbeatInterval = ReadMaximumHeartbeatInterval(timestamp);
                    if (_client.GetProperty("pause", out bool userPaused) == MpvError.Success && userPaused)
                    {
                        // Nothing is being presented while the user has paused; keep the baselines current.
                        previousCounters = counters;
                        previousNotifications = notifications;
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
                    var interval = Stopwatch.GetElapsedTime(previousTimestamp, timestamp).TotalMilliseconds;
                    if (_stopped) return;

                    var intervalText = interval.ToString("0.0", CultureInfo.InvariantCulture);
                    var frameDropDelta = CounterDelta(counters.FrameDrop, previousCounters.FrameDrop);
                    var voDelayedDelta = CounterDelta(counters.VoDelayed, previousCounters.VoDelayed);
                    var mistimedDelta = CounterDelta(counters.Mistimed, previousCounters.Mistimed);
                    var decoderDropDelta = CounterDelta(counters.DecoderDrop, previousCounters.DecoderDrop);
                    var notificationDelta = notifications - previousNotifications;
                    var heartbeatText = heartbeatInterval.ToString("0.0", CultureInfo.InvariantCulture);
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation(
                            "PlaybackStutter | sample interval-ms={IntervalMs} frame-drop-count-delta={FrameDropDelta} vo-delayed-frame-count-delta={VoDelayedDelta} mistimed-frame-count-delta={MistimedDelta} decoder-frame-drop-count-delta={DecoderDropDelta} paused-for-cache={PausedForCache} cache-buffering-state={BufferingState} demuxer-cache-duration={CacheDuration} hwdec-current={Hwdec} current-vo={Vo} time-pos-notifications={TimePosNotifications} ui-heartbeat-max-ms={HeartbeatMaxMs}",
                            intervalText, frameDropDelta, voDelayedDelta, mistimedDelta, decoderDropDelta,
                            pausedForCache, bufferingState, cacheDuration, hwdec, vo, notificationDelta, heartbeatText);
                    }

                    previousCounters = counters;
                    previousNotifications = notifications;
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
