// Copyright (c) Bili Copilot. All rights reserved.

using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.Live.Playback;

public sealed record LivePlaybackRequest(LiveInformation Live, int? PreferQuality = null, int? PreferEotf = null);

// Headers/URLs are sensitive: deliberately not a record with an auto-generated verbose ToString.
public sealed class LiveResolvedMedia
{
    public required LivePlaybackRequest Request { get; init; }
    public required LiveMediaInformation Live { get; init; }
    public IReadOnlyList<LiveStreamCandidate> Candidates { get; init; } = Array.Empty<LiveStreamCandidate>();
    public required Uri StreamUri { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
}

// No generated ToString: the URI contains signed credentials.
public sealed class LiveStreamCandidate
{
    public required Uri Uri { get; init; }
    public required string Format { get; init; }
    public required string Codec { get; init; }
}
