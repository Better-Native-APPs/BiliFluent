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
}

/// <summary>Both audio and video share one latest-intent sequence.</summary>
internal sealed class SeamlessRequestSequence
{
    private long _latest;

    public long Next() => ++_latest;

    public bool IsLatest(long request) => request == _latest;
}
