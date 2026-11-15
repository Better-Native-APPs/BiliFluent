// Copyright (c) Bili Copilot. All rights reserved.

using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.Live.Playback;

public enum LiveDecodePreference { Auto = 0, Software = 1, D3D11 = 2 }
public enum LiveBackendState { Idle, Loading, Playing, Paused, Ended, Failed }

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

public sealed record LiveBackendStatus(LiveBackendState State, double Volume);

// Only live-edge playback controls are exposed; resume resolves a fresh stream in LiveSession.
public interface ILivePlaybackBackend : IAsyncDisposable
{
    event EventHandler<LiveBackendStatus>? StatusChanged;
    Task OpenAsync(LiveResolvedMedia media, CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task SetVolumeAsync(double volume, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
