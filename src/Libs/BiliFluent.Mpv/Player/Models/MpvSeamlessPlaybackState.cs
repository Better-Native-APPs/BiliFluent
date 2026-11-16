// Copyright (c) Bili Copilot. All rights reserved.

using Richasy.MpvKernel.Dash;
using Richasy.MpvKernel.Relay;

namespace Richasy.MpvKernel.Player.Models;

/// <summary>One selected track and the relay coordinates needed by an application-side watchdog.</summary>
/// <param name="TrackId">The selected mpv track ID.</param>
/// <param name="IsExternal">Whether the selected track is an independent file.</param>
/// <param name="MinimumSeekTimeSeconds">Zero for complete files, the virtual video's earliest time, or null for an unmanaged external track.</param>
/// <param name="RelayKey">The selected stream's relay key, when known. Never includes the loopback token.</param>
/// <param name="FragmentIndex">The original-file fragment index of a library-prepared target.</param>
/// <param name="OffsetMap">The virtual-to-original map; null for complete files.</param>
/// <param name="Statistics">The selected key's current relay statistics.</param>
public sealed record MpvSeamlessStreamState(int? TrackId, bool IsExternal, double? MinimumSeekTimeSeconds, string? RelayKey,
    DashIndex? FragmentIndex, RelayVirtualOffsetMap? OffsetMap, RelayStreamStatistics? Statistics);

/// <summary>A native track snapshot combined with library relay state; no playback policy is applied.</summary>
/// <param name="Video">The selected video.</param>
/// <param name="Audio">The selected audio.</param>
/// <param name="IsSwitchPending">Whether a seamless request is still running.</param>
public sealed record MpvSeamlessPlaybackState(MpvSeamlessStreamState Video, MpvSeamlessStreamState Audio, bool IsSwitchPending);
