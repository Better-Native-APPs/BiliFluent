// Copyright (c) Bili Copilot. All rights reserved.

namespace Richasy.MpvKernel.Relay;

/// <summary>
/// The coordinates of a virtual MP4: a new head, original fragments in memory, then the rest
/// of the original file. Head bytes have no original-file coordinate.
/// </summary>
public sealed class RelayVirtualOffsetMap
{
    /// <summary>Creates a checked, immutable mapping. Fragment bytes are contiguous in the original file.</summary>
    public RelayVirtualOffsetMap(int headLength, int preloadedFragmentBytes, long firstFragmentOffset, long upstreamLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(headLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preloadedFragmentBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(firstFragmentOffset);
        ArgumentOutOfRangeException.ThrowIfLessThan(upstreamLength, firstFragmentOffset);
        if (preloadedFragmentBytes > upstreamLength - firstFragmentOffset) throw new ArgumentOutOfRangeException(nameof(preloadedFragmentBytes));
        HeadLength = headLength;
        PrefixLength = checked((long)headLength + preloadedFragmentBytes);
        FirstFragmentOffset = firstFragmentOffset;
        UpstreamTailOffset = checked(firstFragmentOffset + preloadedFragmentBytes);
        UpstreamLength = upstreamLength;
        Length = checked(headLength + (upstreamLength - firstFragmentOffset));
    }

    /// <summary>The new initialization + sidx length.</summary>
    public int HeadLength { get; }

    /// <summary>The byte just after the head and preloaded fragments in virtual coordinates.</summary>
    public long PrefixLength { get; }

    /// <summary>The first fragment's offset in the original file.</summary>
    public long FirstFragmentOffset { get; }

    /// <summary>The first byte not in memory, in original-file coordinates.</summary>
    public long UpstreamTailOffset { get; }

    /// <summary>The original file's full length, used when checking replacement nodes.</summary>
    public long UpstreamLength { get; }

    /// <summary>The length the player sees.</summary>
    public long Length { get; }

    /// <summary>
    /// Maps a fragment or tail offset to the original file. The exclusive end is also accepted
    /// for progress statistics. Returns false for the rewritten head and out-of-file offsets.
    /// </summary>
    public bool TryMapToUpstream(long virtualOffset, out long upstreamOffset)
    {
        upstreamOffset = 0;
        if (virtualOffset < HeadLength || virtualOffset > Length) return false;
        upstreamOffset = FirstFragmentOffset + (virtualOffset - HeadLength);
        return true;
    }

    /// <summary>Maps an original fragment/tail offset, including the exclusive end, to the virtual file.</summary>
    public long MapToVirtual(long upstreamOffset)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(upstreamOffset, FirstFragmentOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(upstreamOffset, UpstreamLength);
        return HeadLength + (upstreamOffset - FirstFragmentOffset);
    }
}
