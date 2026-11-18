// Copyright (c) Bili Copilot. All rights reserved.

using Richasy.MpvKernel.Relay;

namespace Richasy.MpvKernel.Player.Models;

/// <summary>
/// The two streams of a media that are read from addresses of their own.
/// </summary>
public enum MpvRelayStreamKind
{
    /// <summary>The video stream.</summary>
    Video,

    /// <summary>The separate audio stream, or the only stream of a media without video.</summary>
    Audio,
}

/// <summary>
/// The addresses one stream can be read from through the relay.
/// </summary>
/// <param name="upstreams">The addresses, in the order they should be tried.</param>
/// <param name="current">The index of the address to start with.</param>
public sealed class MpvRelayAddresses(IReadOnlyList<RelayUpstream> upstreams, int current = 0)
{
    /// <summary>
    /// The addresses, in the order they should be tried.
    /// </summary>
    public IReadOnlyList<RelayUpstream> Upstreams { get; } = upstreams ?? throw new ArgumentNullException(nameof(upstreams));

    /// <summary>
    /// The index of the address to start with.
    /// </summary>
    public int Current { get; } = current;
}

/// <summary>
/// What the relay needs to serve the streams of a media source.
/// </summary>
public sealed class MpvRelaySource
{
    /// <summary>
    /// The video stream; null when the media has none.
    /// </summary>
    public MpvRelayAddresses? Video { get; init; }

    /// <summary>
    /// The audio stream; null when the media has none of its own.
    /// </summary>
    public MpvRelayAddresses? Audio { get; init; }

    /// <summary>
    /// The headers of the requests to the addresses, the user agent among them: what the player
    /// would send if it read the addresses itself.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

/// <summary>
/// The relay gave up an address of a stream and moved on by itself.
/// </summary>
/// <param name="kind">The stream.</param>
/// <param name="failure">What failed and what is in use now.</param>
public sealed class MpvRelayUpstreamFailedEventArgs(MpvRelayStreamKind kind, RelayUpstreamFailedEventArgs failure) : EventArgs
{
    /// <summary>
    /// The stream.
    /// </summary>
    public MpvRelayStreamKind Kind { get; } = kind;

    /// <summary>
    /// What failed and what is in use now.
    /// </summary>
    public RelayUpstreamFailedEventArgs Failure { get; } = failure;
}
