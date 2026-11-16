// Copyright (c) Bili Copilot. All rights reserved.

using System.Buffers.Binary;
using Richasy.MpvKernel.Dash;
using Richasy.MpvKernel.Relay;

namespace BiliFluent.Mpv.RelayCheck;

internal static class DashChecks
{
    public static void Run(Report report)
    {
        report.Check("dash range: parse", DashByteRange.TryParse("0-991", out var range) && range == new DashByteRange(0, 991) && range.Length == 992);
        foreach (var invalid in new[] { null, "", "-1-9", "9-1", "1-", "1-2-3", "0-9223372036854775807" })
        {
            report.Check("dash range: invalid", !DashByteRange.TryParse(invalid, out _));
        }

        foreach (var version in new byte[] { 0, 1 })
        {
            var time = version == 0 ? 2000UL : (1UL << 40) + 7;
            var head = MakeHead(version, time, 23);
            var index = Parse(head);
            report.Equal($"sidx v{version}: version", version, index.Version);
            report.Equal($"sidx v{version}: timescale", 1000U, index.Timescale);
            report.Equal($"sidx v{version}: reference ID", 37U, index.ReferenceId);
            report.Equal($"sidx v{version}: references", 3, index.Fragments.Count);
            report.Equal($"sidx v{version}: first offset", head.Length + 23L, index.Fragments[0].Offset);
            report.Equal($"sidx v{version}: exact time", time, index.Fragments[0].PresentationTime);
            report.Equal($"sidx v{version}: accumulation", time + 1500, index.Fragments[2].PresentationTime);
            report.Equal($"sidx v{version}: sizes accumulate", head.Length + 23L + 6006, index.EndOffset);
            report.Check($"sidx v{version}: keyframes", index.Fragments.All(fragment => fragment.StartsWithKeyFrame));
            report.Equal($"sidx v{version}: FragmentAt before", 0, index.FragmentAt(-1));
            report.Equal($"sidx v{version}: FragmentAt boundary", 1, index.FragmentAt(index.Fragments[1].Time));
            report.Equal($"sidx v{version}: FragmentAt inside", 1, index.FragmentAt(index.Fragments[1].Time + 0.1));
            report.Equal($"sidx v{version}: FragmentAt after", 2, index.FragmentAt(index.EndTime + 100));
            for (var first = 0; first < 3; first++)
            {
                var rebuilt = index.BuildHead(first);
                var again = Parse(rebuilt);
                report.Equal($"head v{version}/{first}: rewritten v1", (byte)1, again.Version);
                report.Check($"head v{version}/{first}: initialization unchanged", rebuilt.AsSpan(0, 16).SequenceEqual(head.AsSpan(0, 16)));
                report.Equal($"head v{version}/{first}: count", 3 - first, again.Fragments.Count);
                report.Equal($"head v{version}/{first}: first_offset zero", (long)rebuilt.Length, again.Fragments[0].Offset);
                report.Equal($"head v{version}/{first}: exact earliest time", index.Fragments[first].PresentationTime, again.Fragments[0].PresentationTime);
                report.Check($"head v{version}/{first}: references round trip", again.Fragments.Select((fragment, i) =>
                    fragment.Size == index.Fragments[first + i].Size && fragment.DurationTicks == index.Fragments[first + i].DurationTicks
                    && fragment.Sap == index.Fragments[first + i].Sap && fragment.PresentationTime == index.Fragments[first + i].PresentationTime).All(ok => ok));
            }
        }

        Reject(report, "sidx: unsupported version", b => b[24] = 2, unsupported: true);
        Reject(report, "sidx: wrong box", b => b[20] = (byte)'x');
        Reject(report, "sidx: zero timescale", b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(32), 0));
        Reject(report, "sidx: truncated count", b => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(54), 9));
        Reject(report, "sidx: nested references", b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(56), 0x80000001), unsupported: true);
        Reject(report, "sidx: zero duration", b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(60), 0));
        Reject(report, "sidx: overflowing offset", b => BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(44), ulong.MaxValue));
        report.Check("sidx: SAP at a later timestamp is not a starting keyframe", !new DashFragment(0, 1000, 1000, 0, 100, 0x90000001).StartsWithKeyFrame);
        report.Check("sidx: unspecified access point is not accepted", !new DashFragment(0, 1000, 1000, 0, 100, 0x80000000).StartsWithKeyFrame);

        var map = new RelayVirtualOffsetMap(128, 4096, 8192, 65536);
        report.Equal("virtual map: length", 57472L, map.Length);
        report.Equal("virtual map: prefix", 4224L, map.PrefixLength);
        report.Equal("virtual map: upstream tail", 12288L, map.UpstreamTailOffset);
        report.Check("virtual map: head has no original offset", !map.TryMapToUpstream(0, out _) && !map.TryMapToUpstream(127, out _));
        foreach (var offset in new[] { 128L, 129L, 4223L, 4224L, 5000L, map.Length })
        {
            report.Check("virtual map: exact round trip", map.TryMapToUpstream(offset, out var real) && real == 8192 + offset - 128 && map.MapToVirtual(real) == offset);
        }

        report.Check("virtual map: outside rejected", !map.TryMapToUpstream(-1, out _) && !map.TryMapToUpstream(map.Length + 1, out _));
    }

    private static DashIndex Parse(byte[] head) => DashIndex.Parse(head, new(0, 15), new(16, head.Length - 1));

    private static void Reject(Report report, string name, Action<byte[]> corrupt, bool unsupported = false)
    {
        var bytes = MakeHead(1, 0, 0);
        corrupt(bytes);
        var rejected = false;
        try
        {
            Parse(bytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            rejected = unsupported ? ex is NotSupportedException : ex is InvalidDataException;
        }

        report.Check(name, rejected);
    }

    private static byte[] MakeHead(byte version, ulong time, ulong firstOffset)
    {
        var size = (version == 0 ? 32 : 40) + 36;
        var bytes = new byte[16 + size];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 16);
        "ftyp"u8.CopyTo(bytes.AsSpan(4));
        "isom"u8.CopyTo(bytes.AsSpan(8));
        var box = bytes.AsSpan(16);
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)size);
        "sidx"u8.CopyTo(box[4..]);
        box[8] = version;
        BinaryPrimitives.WriteUInt32BigEndian(box[12..], 37);
        BinaryPrimitives.WriteUInt32BigEndian(box[16..], 1000);
        var cursor = 20;
        if (version == 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(box[cursor..], (uint)time);
            BinaryPrimitives.WriteUInt32BigEndian(box[(cursor + 4)..], (uint)firstOffset);
            cursor += 8;
        }
        else
        {
            BinaryPrimitives.WriteUInt64BigEndian(box[cursor..], time);
            BinaryPrimitives.WriteUInt64BigEndian(box[(cursor + 8)..], firstOffset);
            cursor += 16;
        }

        BinaryPrimitives.WriteUInt16BigEndian(box[(cursor + 2)..], 3);
        cursor += 4;
        for (var i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(box[cursor..], (uint)((i + 1) * 1001));
            BinaryPrimitives.WriteUInt32BigEndian(box[(cursor + 4)..], (uint)(1000 >> i));
            BinaryPrimitives.WriteUInt32BigEndian(box[(cursor + 8)..], 0x90000000);
            cursor += 12;
        }

        return bytes;
    }
}
