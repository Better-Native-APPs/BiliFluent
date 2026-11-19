// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Immutable;

namespace Richasy.MpvKernel.Relay;

/// <summary>A delivered byte interval in player-file coordinates, with an exclusive end.</summary>
public readonly record struct RelayDeliveredRange(long Start, long End);

/// <summary>An immutable, sorted snapshot of merged delivery intervals for one registered stream.</summary>
public sealed class RelayDeliveryCoverage(ImmutableArray<RelayDeliveredRange> ranges, bool isIndeterminate)
{
    /// <summary>Adjacent and overlapping intervals are merged; coordinates match the stream's Length.</summary>
    public ImmutableArray<RelayDeliveredRange> Ranges { get; } = ranges;

    /// <summary>The interval limit was exceeded; no completeness decision may use this snapshot.</summary>
    public bool IsIndeterminate { get; } = isIndeterminate;
}

/// <summary>Records delivery only; interpretation belongs to the caller's playback policy.</summary>
internal sealed class RelayDeliveryTracker
{
    internal const int MaximumRanges = 64;
    private readonly Lock _sync = new();
    private readonly List<RelayDeliveredRange> _ranges = [];
    private bool _indeterminate;

    public void Record(long start, long end)
    {
        // Called from the transfer loop: a malformed interval is not recorded, never thrown.
        if (start < 0 || end <= start) return;
        lock (_sync)
        {
            if (_indeterminate) return;
            var first = 0;
            while (first < _ranges.Count && _ranges[first].End < start) first++;
            var last = first;
            while (last < _ranges.Count && _ranges[last].Start <= end)
            {
                start = Math.Min(start, _ranges[last].Start);
                end = Math.Max(end, _ranges[last].End);
                last++;
            }

            if (last > first) _ranges.RemoveRange(first, last - first);
            if (_ranges.Count == MaximumRanges)
            {
                _indeterminate = true;
                return;
            }

            _ranges.Insert(first, new(start, end));
        }
    }

    public RelayDeliveryCoverage Snapshot()
    {
        lock (_sync) return new([.. _ranges], _indeterminate);
    }
}
