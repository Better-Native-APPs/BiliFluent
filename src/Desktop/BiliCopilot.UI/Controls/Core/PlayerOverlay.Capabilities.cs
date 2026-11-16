// Copyright (c) BiliFluent. All rights reserved.

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

    /// <summary>The dot of the live status is full while the media plays and dimmed otherwise.</summary>
    internal static double GetLiveStatusDotOpacity(MpvPlayerState state)
        => state is MpvPlayerState.Playing ? 1 : LiveStatusIdleOpacity;
}
