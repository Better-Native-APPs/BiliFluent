// Copyright (c) Bili Copilot. All rights reserved.

namespace Richasy.MpvKernel.Dash;

/// <summary>One direct media reference of an ISO BMFF sidx.</summary>
/// <param name="PresentationTime">The exact absolute timestamp, in timescale ticks.</param>
/// <param name="DurationTicks">The duration, in timescale ticks.</param>
/// <param name="Timescale">Ticks per second.</param>
/// <param name="Offset">The first byte in the original upstream file.</param>
/// <param name="Size">The number of bytes.</param>
/// <param name="Sap">The unmodified stream-access-point flags and delta.</param>
public readonly record struct DashFragment(ulong PresentationTime, uint DurationTicks, uint Timescale, long Offset, uint Size, uint Sap)
{
    /// <summary>The absolute presentation time, in seconds.</summary>
    public double Time => PresentationTime / (double)Timescale;

    /// <summary>The duration, in seconds.</summary>
    public double Duration => DurationTicks / (double)Timescale;

    /// <summary>The offset just after the fragment.</summary>
    public long EndOffset => checked(Offset + Size);

    /// <summary>Whether a type 1 or 2 access point starts exactly at this fragment's first timestamp.</summary>
    public bool StartsWithKeyFrame => (Sap & 0x80000000) != 0 && ((Sap >> 28) & 7) is 1 or 2 && (Sap & 0x0FFFFFFF) == 0;
}
