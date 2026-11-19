// Copyright (c) BiliFluent. All rights reserved.

namespace BiliCopilot.UI.ViewModels.Core;

internal enum SystemMediaPlaybackStatus
{
    Closed,
    Stopped,
    Changing,
    Playing,
    Paused,
}

internal enum SystemMediaRequest
{
    None,
    Play,
    Pause,
    Stop,
    Next,
    Previous,
    FastForward,
    Rewind,
    PlaybackPosition,
    PlaybackRate,
}

internal enum SystemMediaAction
{
    None,
    Play,
    Pause,
    Next,
    Previous,
    SkipForward,
    SkipBackward,
    Seek,
    ChangeRate,
}

internal readonly record struct SystemMediaSnapshot(
    long MediaGeneration,
    bool IsActive,
    bool CanAcceptInput,
    bool IsPlaybackInitialized,
    bool CanSeek,
    bool CanChangePlaybackRate,
    bool CanNavigatePlaylist,
    bool NextAvailable,
    bool PreviousAvailable,
    SystemMediaPlaybackStatus Status,
    double PositionSeconds,
    double DurationSeconds,
    double PlaybackRate)
{
    public bool AllowRecoveryPauseOrSeek { get; init; }
}

internal readonly record struct SystemMediaButtons(
    bool Play,
    bool Pause,
    bool Stop,
    bool Next,
    bool Previous,
    bool FastForward,
    bool Rewind,
    bool Seek,
    bool ChangeRate);

internal readonly record struct SystemMediaTimeline(
    TimeSpan StartTime,
    TimeSpan EndTime,
    TimeSpan MinSeekTime,
    TimeSpan MaxSeekTime,
    TimeSpan Position)
{
    public bool IsAvailable => EndTime > StartTime;
}

// The policy has no Windows or player dependencies; the same values drive SMTC and headless checks.
internal static class SystemMediaTransportPolicy
{
    public static SystemMediaTimeline CreateTimeline(SystemMediaSnapshot snapshot)
    {
        if (!snapshot.IsActive || !snapshot.IsPlaybackInitialized || !snapshot.CanSeek
            || !double.IsFinite(snapshot.DurationSeconds) || snapshot.DurationSeconds <= 0
            || snapshot.DurationSeconds >= TimeSpan.MaxValue.TotalSeconds)
        {
            return default;
        }

        var duration = TimeSpan.FromSeconds(snapshot.DurationSeconds);
        var position = double.IsFinite(snapshot.PositionSeconds)
            ? Math.Clamp(snapshot.PositionSeconds, 0, snapshot.DurationSeconds) : 0;
        return new(TimeSpan.Zero, duration, TimeSpan.Zero,
            snapshot.CanAcceptInput || snapshot.AllowRecoveryPauseOrSeek ? duration : TimeSpan.Zero, TimeSpan.FromSeconds(position));
    }

    public static SystemMediaButtons GetButtons(SystemMediaSnapshot snapshot)
    {
        var transport = snapshot.IsActive && snapshot.CanAcceptInput;
        var pauseOrSeek = snapshot.IsActive && (snapshot.CanAcceptInput || snapshot.AllowRecoveryPauseOrSeek);
        var seek = pauseOrSeek && CreateTimeline(snapshot).IsAvailable;
        return new(pauseOrSeek, pauseOrSeek, pauseOrSeek,
            transport && snapshot.CanNavigatePlaylist && snapshot.NextAvailable,
            transport && snapshot.CanNavigatePlaylist && snapshot.PreviousAvailable,
            seek, seek, seek,
            transport && snapshot.IsPlaybackInitialized && snapshot.CanChangePlaybackRate);
    }

    public static SystemMediaAction GetAction(SystemMediaRequest request, SystemMediaButtons buttons)
        => request switch
        {
            SystemMediaRequest.Play when buttons.Play => SystemMediaAction.Play,
            SystemMediaRequest.Pause when buttons.Pause => SystemMediaAction.Pause,
            // Stop deliberately preserves the viewing position and keeps the paused timeline visible.
            SystemMediaRequest.Stop when buttons.Stop => SystemMediaAction.Pause,
            SystemMediaRequest.Next when buttons.Next => SystemMediaAction.Next,
            SystemMediaRequest.Previous when buttons.Previous => SystemMediaAction.Previous,
            SystemMediaRequest.FastForward when buttons.FastForward => SystemMediaAction.SkipForward,
            SystemMediaRequest.Rewind when buttons.Rewind => SystemMediaAction.SkipBackward,
            SystemMediaRequest.PlaybackPosition when buttons.Seek => SystemMediaAction.Seek,
            SystemMediaRequest.PlaybackRate when buttons.ChangeRate => SystemMediaAction.ChangeRate,
            _ => SystemMediaAction.None,
        };

    public static bool TryGetSeekPosition(SystemMediaSnapshot snapshot, double requested, out double position)
    {
        position = 0;
        if (!GetButtons(snapshot).Seek || !double.IsFinite(requested)) return false;
        position = Math.Clamp(requested, 0, snapshot.DurationSeconds);
        return true;
    }

    public static bool IsSupportedPlaybackRate(double requested)
        => double.IsFinite(requested) && requested is >= 0.1 and <= 3;

    public static double GetPlaybackRate(SystemMediaSnapshot snapshot)
        => snapshot.IsActive && snapshot.CanChangePlaybackRate
            && double.IsFinite(snapshot.PlaybackRate) && snapshot.PlaybackRate > 0 ? snapshot.PlaybackRate : 1;
}

internal sealed class SystemMediaTimelinePublisher
{
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(5);
    private bool _hasPublished;
    private SystemMediaSnapshot _published;
    private SystemMediaTimeline _timeline;
    private long _publishedAt;
    private SystemMediaSnapshot _observed;
    private long _observedAt;
    private bool _hasObservation;

    public void Reset()
    {
        _hasPublished = false;
        _hasObservation = false;
    }

    public bool TryCreateUpdate(SystemMediaSnapshot snapshot, long now, bool force, out SystemMediaTimeline timeline)
    {
        timeline = SystemMediaTransportPolicy.CreateTimeline(snapshot);
        var changed = !_hasPublished || snapshot.MediaGeneration != _published.MediaGeneration
            || snapshot.Status != _published.Status || snapshot.IsActive != _published.IsActive
            || SystemMediaTransportPolicy.GetPlaybackRate(snapshot) != SystemMediaTransportPolicy.GetPlaybackRate(_published)
            || timeline.EndTime != _timeline.EndTime || timeline.MinSeekTime != _timeline.MinSeekTime
            || timeline.MaxSeekTime != _timeline.MaxSeekTime;
        var periodic = timeline.IsAvailable && snapshot.Status == SystemMediaPlaybackStatus.Playing
            && now - _publishedAt >= UpdateInterval.TotalMilliseconds;

        // A position discontinuity also catches seeks that finish between two playback-state notifications.
        // Normal position notifications never force a timeline write, including high-rate playback.
        var discontinuity = false;
        if (_hasObservation && timeline.IsAvailable && _observed.IsPlaybackInitialized
            && snapshot.MediaGeneration == _observed.MediaGeneration && snapshot.Status == _observed.Status)
        {
            var elapsed = Math.Max(0, now - _observedAt) / 1000d;
            var expected = _observed.PositionSeconds + (snapshot.Status == SystemMediaPlaybackStatus.Playing
                ? elapsed * SystemMediaTransportPolicy.GetPlaybackRate(_observed) : 0);
            discontinuity = Math.Abs(snapshot.PositionSeconds - expected) > 1;
        }

        _observed = snapshot;
        _observedAt = now;
        _hasObservation = true;
        if (!force && !changed && !periodic && !discontinuity) return false;
        _published = snapshot;
        _timeline = timeline;
        _publishedAt = now;
        _hasPublished = true;
        return true;
    }
}
