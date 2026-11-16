// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using Richasy.MpvKernel.Core.Enums;

namespace BiliCopilot.UI.Controls.Core;

/// <summary>
/// How the shared player views follow the media capabilities of <see cref="ViewModels.Core.PlayerViewModel"/>.
/// Both transport bars, their buttons and this overlay bind an element to the capability it needs through
/// the rules below, so no view asks what kind of media plays.
/// </summary>
public sealed partial class PlayerOverlay
{
    // The dot of the live status while the media is not playing.
    private const double LiveStatusIdleOpacity = 0.45;

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
    /// The live status takes the place of the timeline: it shows for an admitted media that has none.
    /// </summary>
    internal static Visibility GetLiveStatusVisibility(bool canSeek, bool hasAdmittedMedia)
        => hasAdmittedMedia && !canSeek ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// A room brings its own danmaku: the room delivers the messages and takes what the user sends. The views
    /// learn it from the connector, the same way the template selectors pick the parts of a room.
    /// </summary>
    internal static bool HasRoomDanmaku(IPlayerConnectorViewModel? connector)
        => connector is LiveConnectorViewModel;

    /// <summary>The connector of the room that plays, or null for a media without a room.</summary>
    internal static LiveConnectorViewModel? GetRoomConnector(IPlayerConnectorViewModel? connector)
        => connector as LiveConnectorViewModel;

    /// <summary>The danmaku switch of a bar shows for loaded on-demand danmaku and for a room.</summary>
    internal static Visibility GetDanmakuSwitchVisibility(bool isDanmakuControlVisible, IPlayerConnectorViewModel? connector)
        => isDanmakuControlVisible || HasRoomDanmaku(connector) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The danmaku of a room is drawn while its picture plays, not while it loads, pauses or has ended.</summary>
    internal static bool IsRoomPlaying(LiveMediaLoadState state)
        => state == LiveMediaLoadState.Ready;

    /// <summary>
    /// The stream of a room ended, or its connection dropped, after it had played. While the player is already
    /// asking for the picture again, the connecting hint stands alone. The state of a room that was left never
    /// shows over the media that follows it.
    /// </summary>
    internal static Visibility GetRoomEndedVisibility(bool isLiveMedia, LiveMediaLoadState state, bool isConnecting)
        => isLiveMedia && state == LiveMediaLoadState.Ended && !isConnecting ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The title over a playback error. A room whose picture cannot be shown says so in plain words.</summary>
    internal static string GetErrorTitle(bool isLiveMedia)
        => ResourceToolkit.GetLocalizedString(isLiveMedia ? StringNames.LiveFailedTitle : StringNames.MpvError);

    /// <summary>
    /// The text under <see cref="GetErrorTitle(bool)"/>. The player words the failure of a room for its log, so a
    /// room shows the same plain advice whatever the failure was; other media show what the player reports.
    /// </summary>
    internal static string GetErrorDescription(string? errorMessage, bool isLiveMedia)
        => isLiveMedia ? ResourceToolkit.GetLocalizedString(StringNames.LiveFailedDescription) : errorMessage ?? string.Empty;

    /// <summary>The dot of the live status is full while the media plays and dimmed otherwise.</summary>
    internal static double GetLiveStatusDotOpacity(MpvPlayerState state)
        => state is MpvPlayerState.Playing ? 1 : LiveStatusIdleOpacity;
}
