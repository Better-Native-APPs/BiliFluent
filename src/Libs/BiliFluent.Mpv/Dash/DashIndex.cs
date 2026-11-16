// Copyright (c) Bili Copilot. All rights reserved.

using System.Buffers.Binary;

namespace Richasy.MpvKernel.Dash;

/// <summary>
/// An immutable DASH fragment index read from a version 0 or 1 sidx. No network access is performed.
/// </summary>
public sealed class DashIndex
{
    private readonly byte[] _initialization;
    private readonly uint _flags;

    private DashIndex(byte[] initialization, byte version, uint flags, uint referenceId, uint timescale, DashFragment[] fragments)
    {
        _initialization = initialization;
        Version = version;
        _flags = flags;
        ReferenceId = referenceId;
        Timescale = timescale;
        Fragments = Array.AsReadOnly(fragments);
    }

    /// <summary>The version of the original sidx.</summary>
    public byte Version { get; }

    /// <summary>The original sidx reference_ID.</summary>
    public uint ReferenceId { get; }

    /// <summary>Ticks per second.</summary>
    public uint Timescale { get; }

    /// <summary>The original initialization segment's byte count.</summary>
    public int InitializationLength => _initialization.Length;

    /// <summary>The fragments, in presentation and file order.</summary>
    public IReadOnlyList<DashFragment> Fragments { get; }

    /// <summary>The byte just after the final indexed fragment in the original file.</summary>
    public long EndOffset => Fragments[^1].EndOffset;

    /// <summary>The presentation time just after the final fragment, in seconds.</summary>
    public double EndTime => (Fragments[^1].PresentationTime + Fragments[^1].DurationTicks) / (double)Timescale;

    /// <summary>
    /// Parses a head fetched from byte zero through index_range. Both ranges are inclusive and
    /// use original-file offsets. Malformed boxes throw InvalidDataException; hierarchical sidx
    /// references and other versions throw NotSupportedException. Input memory is copied.
    /// </summary>
    public static DashIndex Parse(ReadOnlyMemory<byte> head, DashByteRange initialization, DashByteRange indexRange)
    {
        if (initialization.Last >= head.Length || indexRange.Last >= head.Length || initialization.Last >= indexRange.First)
        {
            throw new InvalidDataException("DASH ranges are outside the head or overlap.");
        }

        var box = head.Span.Slice(checked((int)indexRange.First), checked((int)indexRange.Length));
        if (box.Length < 8 || !box.Slice(4, 4).SequenceEqual("sidx"u8)) throw new InvalidDataException("DASH index_range does not start with sidx.");
        var boxSize = (ulong)BinaryPrimitives.ReadUInt32BigEndian(box);
        var headerSize = 8;
        if (boxSize == 1)
        {
            if (box.Length < 16) throw new InvalidDataException("Truncated extended sidx size.");
            boxSize = BinaryPrimitives.ReadUInt64BigEndian(box[8..]);
            headerSize = 16;
        }
        else if (boxSize == 0)
        {
            boxSize = (ulong)box.Length;
        }

        if (boxSize > (ulong)box.Length || boxSize < (ulong)(headerSize + 24)) throw new InvalidDataException("Invalid sidx box size.");
        box = box[..checked((int)boxSize)];
        var version = box[headerSize];
        if (version is not (0 or 1)) throw new NotSupportedException("Unsupported sidx version.");
        var cursor = headerSize + 4;
        var flags = BinaryPrimitives.ReadUInt32BigEndian(box[headerSize..]) & 0x00FFFFFF;
        var referenceId = Read32(box, ref cursor);
        var timescale = Read32(box, ref cursor);
        if (timescale == 0) throw new InvalidDataException("sidx timescale is zero.");
        var time = version == 0 ? Read32(box, ref cursor) : Read64(box, ref cursor);
        var firstOffset = version == 0 ? Read32(box, ref cursor) : Read64(box, ref cursor);
        if (cursor > box.Length - 4) throw new InvalidDataException("Truncated sidx reference count.");
        var count = BinaryPrimitives.ReadUInt16BigEndian(box.Slice(cursor + 2, 2));
        cursor += 4;
        if (count == 0 || box.Length - cursor != count * 12) throw new InvalidDataException("Invalid sidx reference count.");

        try
        {
            var offset = checked(indexRange.First + (long)boxSize + (long)firstOffset);
            var fragments = new DashFragment[count];
            for (var i = 0; i < count; i++)
            {
                var size = Read32(box, ref cursor);
                if ((size & 0x80000000) != 0) throw new NotSupportedException("Hierarchical sidx references are not supported.");
                var duration = Read32(box, ref cursor);
                var sap = Read32(box, ref cursor);
                if (size == 0 || duration == 0) throw new InvalidDataException("sidx contains an empty fragment.");
                fragments[i] = new(time, duration, timescale, offset, size, sap);
                time = checked(time + duration);
                offset = checked(offset + size);
            }

            return new(head.Slice(checked((int)initialization.First), checked((int)initialization.Length)).ToArray(), version, flags, referenceId, timescale, fragments);
        }
        catch (OverflowException)
        {
            throw new InvalidDataException("sidx offsets or timestamps overflow.");
        }
    }

    /// <summary>
    /// Finds the last fragment starting at or before seconds. Values outside the indexed time
    /// interval clamp to the first or last fragment; NaN and infinity are rejected.
    /// </summary>
    public int FragmentAt(double seconds)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        var lo = 0;
        var hi = Fragments.Count - 1;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo + 1) / 2);
            if (Fragments[mid].Time <= seconds) lo = mid;
            else hi = mid - 1;
        }

        return lo;
    }

    /// <summary>
    /// Returns initialization + a version 1 sidx containing firstFragment onward. Exact original
    /// timestamps and SAP flags are retained; earliest_presentation_time is that fragment's time
    /// and first_offset is zero. Original media fragments themselves must not be rewritten.
    /// </summary>
    public byte[] BuildHead(int firstFragment)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstFragment);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(firstFragment, Fragments.Count);
        var count = Fragments.Count - firstFragment;
        var size = 40 + (12 * count);
        var result = new byte[checked(_initialization.Length + size)];
        _initialization.CopyTo(result, 0);
        var box = result.AsSpan(_initialization.Length);
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)size);
        "sidx"u8.CopyTo(box[4..]);
        BinaryPrimitives.WriteUInt32BigEndian(box[8..], 0x01000000 | _flags);
        BinaryPrimitives.WriteUInt32BigEndian(box[12..], ReferenceId);
        BinaryPrimitives.WriteUInt32BigEndian(box[16..], Timescale);
        BinaryPrimitives.WriteUInt64BigEndian(box[20..], Fragments[firstFragment].PresentationTime);
        BinaryPrimitives.WriteUInt64BigEndian(box[28..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(box[38..], checked((ushort)count));
        for (var i = 0; i < count; i++)
        {
            var fragment = Fragments[firstFragment + i];
            var entry = box[(40 + (12 * i))..];
            BinaryPrimitives.WriteUInt32BigEndian(entry, fragment.Size);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], fragment.DurationTicks);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], fragment.Sap);
        }

        return result;
    }

    private static uint Read32(ReadOnlySpan<byte> box, ref int cursor)
    {
        if (cursor > box.Length - 4) throw new InvalidDataException("Truncated sidx field.");
        var value = BinaryPrimitives.ReadUInt32BigEndian(box[cursor..]);
        cursor += 4;
        return value;
    }

    private static ulong Read64(ReadOnlySpan<byte> box, ref int cursor)
    {
        if (cursor > box.Length - 8) throw new InvalidDataException("Truncated sidx field.");
        var value = BinaryPrimitives.ReadUInt64BigEndian(box[cursor..]);
        cursor += 8;
        return value;
    }
}
