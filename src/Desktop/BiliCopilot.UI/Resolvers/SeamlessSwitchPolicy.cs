// Copyright (c) BiliFluent. All rights reserved.

using Richasy.MpvKernel.Player.Models;

namespace BiliCopilot.UI.Resolvers;

internal enum SeamlessSwitchAction
{
    Ignore,
    Commit,
    Defer,
    Reload,
    RetryVideo,
}

/// <summary>Application decisions without a dispatcher, native player or network requests.</summary>
internal static class SeamlessSwitchPolicy
{
    public static bool CanAttempt(bool onDemand, bool initialized, bool playing, bool paused, bool relayed, bool described)
        => onDemand && initialized && playing && !paused && relayed && described;

    public static SeamlessSwitchAction Decide(MpvSeamlessSwitchResult result, bool video, int retries,
        bool latest, bool sameFile, bool stablePlayback)
    {
        if (!latest || !sameFile) return SeamlessSwitchAction.Ignore;
        if (result.Status == MpvSeamlessSwitchStatus.Switched) return SeamlessSwitchAction.Commit;
        if (result.Status == MpvSeamlessSwitchStatus.NotSupported)
            return result.Reason == MpvSeamlessSwitchReason.Paused ? SeamlessSwitchAction.Defer : SeamlessSwitchAction.Reload;
        if (result.Status == MpvSeamlessSwitchStatus.Cancelled)
            return result.Reason is MpvSeamlessSwitchReason.Superseded or MpvSeamlessSwitchReason.CallerCancelled or MpvSeamlessSwitchReason.Disposed
                ? SeamlessSwitchAction.Ignore : SeamlessSwitchAction.Defer;
        return video && result.Reason == MpvSeamlessSwitchReason.MissedBoundary && retries == 0 && stablePlayback
            ? SeamlessSwitchAction.RetryVideo : SeamlessSwitchAction.Reload;
    }

    public static bool RequiresReloadForSeek(double position, bool external, double? minimumSeekTime)
        => external && (minimumSeekTime is null || position < minimumSeekTime.Value);

    public static bool RequiresReloadForSeek(double position, double currentPosition, bool externalVideo,
        double? minimumSeekTime, bool separateAudio)
        // Initial DASH files have independent caches. A backward seek after resume can join
        // early audio packets to the old cached interval; replace both complete files instead.
        => RequiresReloadForSeek(position, externalVideo, minimumSeekTime)
            || (!externalVideo && separateAudio && position < currentPosition);
}

/// <summary>Both audio and video share one latest-intent sequence.</summary>
internal sealed class SeamlessRequestSequence
{
    private long _latest;

    public long Next() => ++_latest;

    public bool IsLatest(long request) => request == _latest;
}

/// <summary>A submitted seek is not a displayed position. Keep its target until native playback settles there.</summary>
internal sealed class PlaybackPositionIntent
{
    private long _version;
    private long? _submittedVersion;

    public long Version => _version;

    public double? PendingPosition { get; private set; }

    public long Request(double position)
    {
        PendingPosition = position;
        _submittedVersion = null;
        return ++_version;
    }

    public bool IsLatest(long version) => version == _version;

    public void MarkSubmitted(long version)
    {
        if (IsLatest(version)) _submittedVersion = version;
    }

    public void Reset()
    {
        PendingPosition = null;
        _submittedVersion = null;
        ++_version;
    }

    public double? Resolve(double? actual, double? fallback = null)
        => PendingPosition ?? Valid(actual) ?? Valid(fallback);

    public bool Observe(double position, bool seeking, bool buffering, bool idle, bool paused, double speed = 1)
    {
        // Network samples arrive once a second; normal progress at a faster playback rate
        // must not leave a completed seek pending merely because the sample missed its first frame.
        if (_submittedVersion != _version || PendingPosition is not { } target || seeking || buffering || (idle && !paused)
            || !double.IsFinite(position) || position < 0 || position < target - 2
            || position > target + Math.Max(2, speed * 2)) return false;
        PendingPosition = null;
        return true;
    }

    private static double? Valid(double? position)
        => position is { } value && double.IsFinite(value) && value >= 0 ? value : null;
}
