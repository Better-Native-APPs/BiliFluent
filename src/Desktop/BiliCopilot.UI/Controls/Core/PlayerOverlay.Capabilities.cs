// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;

namespace BiliCopilot.UI.Controls.Core;

/// <summary>
/// How the shared player views follow the media capabilities of <see cref="ViewModels.Core.PlayerViewModel"/>.
/// Both transport bars, their buttons and this overlay bind an element to the capability it needs through
/// the rules below, so no view asks what kind of media plays.
/// </summary>
public sealed partial class PlayerOverlay
{
    // The volume ceiling of the player when nothing else is configured.
    private const double DefaultVolumeCeiling = 100;

    /// <summary>
    /// Whether an element that needs a capability stays in the layout. It leaves only for an admitted media
    /// that lacks the capability: before the first media every capability is false, and the views keep the
    /// ordinary layout they have always shown in that moment.
    /// </summary>
    internal static bool IsCapabilityShown(bool capability, bool hasAdmittedMedia)
        => capability || !hasAdmittedMedia;

    /// <summary>The visibility form of <see cref="IsCapabilityShown(bool, bool)"/>.</summary>
    internal static Visibility GetCapabilityVisibility(bool capability, bool hasAdmittedMedia)
        => IsCapabilityShown(capability, hasAdmittedMedia) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>An element with its own condition that also needs a capability.</summary>
    internal static Visibility GetCapabilityVisibility(bool condition, bool capability, bool hasAdmittedMedia)
        => condition && IsCapabilityShown(capability, hasAdmittedMedia) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// A media without a timeline has nothing to seek, skip or speed up. The transport bars give it one row of
    /// commands in place of their on-demand rows; that row is created only for an admitted media of this kind.
    /// </summary>
    internal static bool IsTimelineFree(bool canSeek, bool hasAdmittedMedia)
        => hasAdmittedMedia && !canSeek;

    /// <summary>
    /// The top of a volume slider. The player reports its ceiling once the media is set up; a ceiling that
    /// was never set must not leave a slider with no range.
    /// </summary>
    internal static double GetVolumeCeiling(double maxVolume)
        => maxVolume > 0 ? maxVolume : DefaultVolumeCeiling;

    /// <summary>The danmaku of a room is drawn while its picture plays, not while it loads, pauses or has ended.</summary>
    internal static bool IsRoomPlaying(LiveMediaLoadState state)
        => state == LiveMediaLoadState.Ready;

    /// <summary>The title over a playback error. A room whose picture cannot be shown says so in plain words.</summary>
    internal static string GetErrorTitle(bool isLiveFailure)
        => ResourceToolkit.GetLocalizedString(isLiveFailure ? StringNames.LiveFailedTitle : StringNames.MpvError);

    /// <summary>
    /// The text under <see cref="GetErrorTitle(bool)"/>. The player words the failure of a room for its log, so a
    /// room shows the same plain advice whatever the failure was; other media show what the player reports.
    /// </summary>
    internal static string GetErrorDescription(string? errorMessage, bool isLiveFailure)
        => isLiveFailure ? ResourceToolkit.GetLocalizedString(StringNames.LiveFailedDescription) : errorMessage ?? string.Empty;
}
