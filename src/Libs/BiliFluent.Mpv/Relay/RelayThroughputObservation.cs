// Copyright (c) Bili Copilot. All rights reserved.

namespace Richasy.MpvKernel.Relay;

/// <summary>
/// Achieved upstream throughput during a contiguous transfer not held back by the player.
/// A floor proves only that the network can deliver at least this rate.
/// </summary>
/// <param name="BytesPerSecond">Body bytes per second, excluding the first read of the transfer.</param>
/// <param name="Bytes">Body bytes measured in the transfer.</param>
/// <param name="Duration">Wall time covered by the measured bytes.</param>
/// <param name="UpstreamWait">Time spent awaiting upstream reads within that wall time.</param>
/// <param name="IsNetworkLimited">Whether upstream waits account for at least half the wall time.</param>
/// <param name="Sequence">Process-wide increasing identifier of this cumulative update.</param>
/// <param name="UpdatedAt">UTC time this cumulative update was published.</param>
public sealed record RelayThroughputObservation(
    double BytesPerSecond,
    long Bytes,
    TimeSpan Duration,
    TimeSpan UpstreamWait,
    bool IsNetworkLimited,
    long Sequence,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Identifies the same contiguous transfer across its cumulative updates.</summary>
    public long SegmentId { get; init; }
}
