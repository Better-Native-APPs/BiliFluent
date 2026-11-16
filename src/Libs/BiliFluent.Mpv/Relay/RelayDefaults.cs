// Copyright (c) Bili Copilot. All rights reserved.

namespace Richasy.MpvKernel.Relay;

/// <summary>
/// Every timeout and limit of the loopback media relay.
/// </summary>
public static class RelayDefaults
{
    /// <summary>
    /// Bytes of random data in the access token of a relay.
    /// </summary>
    public const int TokenBytes = 32;

    /// <summary>
    /// The longest request head the relay reads from a client.
    /// </summary>
    public const int MaxRequestHeadBytes = 16 * 1024;

    /// <summary>
    /// The size of the buffer one response is copied through. Nothing else is held in memory:
    /// a player that reads slowly stops the relay from reading upstream.
    /// </summary>
    public const int BufferBytes = 64 * 1024;

    /// <summary>
    /// Upstream requests one response makes at the least before it gives up without having
    /// received a byte. With more addresses than this, each one is tried once.
    /// </summary>
    public const int MinimumAttempts = 2;

    /// <summary>
    /// Redirects followed for one upstream request.
    /// </summary>
    public const int MaxRedirects = 5;

    /// <summary>
    /// How long a client may take to send its request head.
    /// </summary>
    public static readonly TimeSpan RequestHeadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long connecting to an upstream node may take, the TLS handshake included.
    /// </summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long an upstream node may take to answer a request with its response head, connecting included.
    /// </summary>
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// An upstream node that is connected and delivers no data for this long, while the relay
    /// waits for it, has stalled. Time the relay spends waiting for the player does not count.
    /// </summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The span the byte rate of a stream is averaged over.
    /// </summary>
    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long disposing the relay waits for its connections to end.
    /// </summary>
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);
}
