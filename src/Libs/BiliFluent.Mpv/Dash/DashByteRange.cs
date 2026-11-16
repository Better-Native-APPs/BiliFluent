// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;

namespace Richasy.MpvKernel.Dash;

/// <summary>A closed byte range from DASH segment_base metadata.</summary>
public readonly record struct DashByteRange
{
    /// <summary>Creates an inclusive range; negative or reversed offsets are rejected.</summary>
    public DashByteRange(long first, long last)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfLessThan(last, first);
        ArgumentOutOfRangeException.ThrowIfEqual(last, long.MaxValue);
        First = first;
        Last = last;
    }

    /// <summary>The first byte, inclusive.</summary>
    public long First { get; }

    /// <summary>The last byte, inclusive.</summary>
    public long Last { get; }

    /// <summary>The number of bytes.</summary>
    public long Length => Last - First + 1;

    /// <summary>Parses the API's decimal "first-last" form without throwing.</summary>
    public static bool TryParse(string? value, out DashByteRange range)
    {
        range = default;
        if (value is null) return false;
        var dash = value.IndexOf('-');
        if (dash <= 0
            || !long.TryParse(value.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out var first)
            || !long.TryParse(value.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var last)
            || first < 0 || last < first || last == long.MaxValue) return false;
        range = new(first, last);
        return true;
    }
}
