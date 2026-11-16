// Copyright (c) Bili Copilot. All rights reserved.

using Richasy.MpvKernel.Dash;
using Richasy.MpvKernel.Relay;

namespace Richasy.MpvKernel.Player.Models;

/// <summary>Formats for which the experimental seamless route must not be used.</summary>
[Flags]
public enum MpvSeamlessRestrictions
{
    /// <summary>Ordinary, finite SDR DASH media.</summary>
    None = 0,

    /// <summary>A live timeline.</summary>
    Live = 1,

    /// <summary>HDR, including unverified tone-mapping transitions.</summary>
    Hdr = 2,

    /// <summary>Dolby video or audio.</summary>
    Dolby = 4,

    /// <summary>Lossless or another unverified audio format.</summary>
    Lossless = 8,
}

/// <summary>
/// A target supplied independently of IMpvMediaSourceResolver. No credentials or metadata are
/// fetched by this type. An application can keep its existing resolver and reload fallback.
/// </summary>
public sealed class MpvSeamlessTarget
{
    /// <summary>The target file's nodes, in failover order.</summary>
    public required IReadOnlyList<RelayUpstream> Upstreams { get; init; }

    /// <summary>The initial node's index.</summary>
    public int Current { get; init; }

    /// <summary>Headers sent only by the relay to upstream nodes.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>The inclusive segment_base initialization range. Null disables this route.</summary>
    public DashByteRange? Initialization { get; init; }

    /// <summary>The inclusive segment_base index_range. Null disables this route.</summary>
    public DashByteRange? IndexRange { get; init; }

    /// <summary>An explicit human-readable track title; a library title is used when omitted.</summary>
    public string? Title { get; init; }

    /// <summary>Any value other than None disables this route, without altering playback.</summary>
    public MpvSeamlessRestrictions Restrictions { get; init; }
}
