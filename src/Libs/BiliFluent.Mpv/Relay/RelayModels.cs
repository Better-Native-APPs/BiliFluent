// Copyright (c) Bili Copilot. All rights reserved.

namespace Richasy.MpvKernel.Relay;

/// <summary>
/// How a relay is set up. The defaults are the values of <see cref="RelayDefaults"/>.
/// </summary>
public sealed class LoopbackMediaRelayOptions
{
    /// <summary>
    /// Whether the certificate of an upstream node is validated.
    /// </summary>
    public bool VerifyTls { get; init; } = true;

    /// <summary>
    /// See <see cref="RelayDefaults.ConnectTimeout"/>.
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = RelayDefaults.ConnectTimeout;

    /// <summary>
    /// See <see cref="RelayDefaults.ResponseTimeout"/>.
    /// </summary>
    public TimeSpan ResponseTimeout { get; init; } = RelayDefaults.ResponseTimeout;

    /// <summary>
    /// See <see cref="RelayDefaults.ReadTimeout"/>.
    /// </summary>
    public TimeSpan ReadTimeout { get; init; } = RelayDefaults.ReadTimeout;

    /// <summary>
    /// See <see cref="RelayDefaults.RequestHeadTimeout"/>.
    /// </summary>
    public TimeSpan RequestHeadTimeout { get; init; } = RelayDefaults.RequestHeadTimeout;
}

/// <summary>
/// A part of a stream the application already holds; the relay answers it without asking upstream.
/// </summary>
/// <param name="offset">The offset of the first byte in the stream.</param>
/// <param name="data">The bytes.</param>
public sealed class RelayPreload(long offset, ReadOnlyMemory<byte> data)
{
    /// <summary>
    /// The offset of the first byte in the stream.
    /// </summary>
    public long Offset { get; } = offset >= 0 ? offset : throw new ArgumentOutOfRangeException(nameof(offset));

    /// <summary>
    /// The bytes.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; } = data;
}

/// <summary>
/// A stream to serve under a key.
/// </summary>
public sealed class RelayStreamRegistration
{
    /// <summary>
    /// The addresses the stream can be read from, in the order they should be tried.
    /// </summary>
    public required IReadOnlyList<RelayUpstream> Upstreams { get; init; }

    /// <summary>
    /// The index of the address to start with.
    /// </summary>
    public int Current { get; init; }

    /// <summary>
    /// The headers of every upstream request, the user agent among them. The requests of the
    /// player to the relay carry none of them.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// The length of the stream in bytes, when it is known up front; otherwise the first upstream answer tells.
    /// </summary>
    public long? Length { get; init; }

    /// <summary>
    /// Parts of the stream held in memory, for a switch that must not wait for the network.
    /// </summary>
    public IReadOnlyList<RelayPreload>? Preloaded { get; init; }

    internal RelayVirtualOffsetMap? VirtualMap { get; init; }
}

/// <summary>
/// Why an upstream address was given up.
/// </summary>
public enum RelayFailureReason
{
    /// <summary>The node could not be connected to, or the request could not be sent.</summary>
    Connect,

    /// <summary>The node answered with a status other than success.</summary>
    Status,

    /// <summary>The node answered without the range or the length the relay needs.</summary>
    Unsupported,

    /// <summary>The node serves a resource of another length: not the same file.</summary>
    Length,

    /// <summary>The connection broke, or ended early, in the middle of the body.</summary>
    Read,

    /// <summary>The node did not answer, or delivered no data, in time.</summary>
    Timeout,
}

/// <summary>
/// An upstream address failed and the relay moved on to another one by itself.
/// </summary>
/// <param name="key">The key of the stream.</param>
/// <param name="failed">The address that failed.</param>
/// <param name="current">The address in use now.</param>
/// <param name="reason">What happened.</param>
public sealed class RelayUpstreamFailedEventArgs(string key, RelayUpstream failed, RelayUpstream current, RelayFailureReason reason) : EventArgs
{
    /// <summary>
    /// The key of the stream.
    /// </summary>
    public string Key { get; } = key;

    /// <summary>
    /// The address that failed.
    /// </summary>
    public RelayUpstream Failed { get; } = failed;

    /// <summary>
    /// The address in use now. It is the failed one again when the stream has no other.
    /// </summary>
    public RelayUpstream Current { get; } = current;

    /// <summary>
    /// What happened.
    /// </summary>
    public RelayFailureReason Reason { get; } = reason;
}

/// <summary>
/// What became of new addresses for a stream.
/// </summary>
public enum RelayUpdateResult
{
    /// <summary>The stream is read from the new addresses from now on.</summary>
    Updated,

    /// <summary>No stream is registered under the key.</summary>
    UnknownKey,

    /// <summary>The new addresses serve a resource of another length; the old ones stay in use.</summary>
    LengthMismatch,

    /// <summary>None of the new addresses answered; the old ones stay in use.</summary>
    Unreachable,
}

/// <summary>
/// What a relay knows about one stream.
/// </summary>
/// <param name="UpstreamBytes">Bytes read from upstream nodes and passed on.</param>
/// <param name="PreloadedBytes">Bytes answered from memory.</param>
/// <param name="BytesPerSecond">The rate upstream bytes were passed on at, over <see cref="RelayDefaults.RateWindow"/>.</param>
/// <param name="FirstByteTime">How long the last upstream request took to be answered; null before the first one.</param>
/// <param name="Current">The address in use.</param>
/// <param name="Requests">Requests of the player for this stream.</param>
/// <param name="ActiveRequests">Requests whose response is still being sent.</param>
/// <param name="Length">The length of the stream in bytes; null while unknown.</param>
/// <param name="Failovers">Times the relay moved on from a failed address by itself.</param>
/// <param name="Handovers">Times a response went on from another address in the middle of its body.</param>
public readonly record struct RelayStreamStatistics(
    long UpstreamBytes,
    long PreloadedBytes,
    double BytesPerSecond,
    TimeSpan? FirstByteTime,
    RelayUpstream Current,
    int Requests,
    int ActiveRequests,
    long? Length,
    int Failovers,
    int Handovers)
{
    /// <summary>The greatest exclusive byte offset delivered to the player, in player-file coordinates.</summary>
    public long ForwardBytes { get; init; }

    /// <summary>The corresponding original-file offset; null while delivery remains inside the rewritten head.</summary>
    public long? ForwardUpstreamOffset { get; init; }

    /// <summary>UTC time upstream data was last received; null before any upstream body byte arrived.</summary>
    public DateTimeOffset? LastByteReceivedAt { get; init; }

    /// <summary>UTC time data, including memory data, was last delivered to the player.</summary>
    public DateTimeOffset? LastDeliveredAt { get; init; }

    /// <summary>The delivery rate including memory, over the same window as BytesPerSecond.</summary>
    public double DeliveredBytesPerSecond { get; init; }
}
