// Copyright (c) Bili Copilot. All rights reserved.

namespace Richasy.MpvKernel.Relay;

/// <summary>
/// Averages a byte count over a sliding window. Thread-safe.
/// </summary>
public sealed class RelayRateMeter
{
    private const int BucketCount = 12;
    private readonly long[] _bytes = new long[BucketCount];
    private readonly long[] _slots = new long[BucketCount];
    private readonly long _bucketMilliseconds;
    private readonly Lock _sync = new();
    private long _firstMilliseconds = -1;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayRateMeter"/> class.
    /// </summary>
    /// <param name="window">The span the rate is averaged over.</param>
    public RelayRateMeter(TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        _bucketMilliseconds = Math.Max(1, (long)window.TotalMilliseconds / BucketCount);
        Array.Fill(_slots, -1);
    }

    /// <summary>
    /// Counts bytes that arrived at <paramref name="nowMilliseconds"/> on a monotonic clock.
    /// </summary>
    public void Add(long nowMilliseconds, long bytes)
    {
        var slot = nowMilliseconds / _bucketMilliseconds;
        var index = (int)(slot % BucketCount);
        lock (_sync)
        {
            if (_firstMilliseconds < 0)
            {
                _firstMilliseconds = nowMilliseconds;
            }

            if (_slots[index] != slot)
            {
                _slots[index] = slot;
                _bytes[index] = 0;
            }

            _bytes[index] += bytes;
        }
    }

    /// <summary>
    /// Bytes per second over the window that ends at <paramref name="nowMilliseconds"/>. Before
    /// a whole window has passed since the first byte, the time since then is used instead.
    /// </summary>
    public double GetRate(long nowMilliseconds)
    {
        var slot = nowMilliseconds / _bucketMilliseconds;
        long sum = 0;
        long first;
        lock (_sync)
        {
            first = _firstMilliseconds;
            for (var i = 0; i < BucketCount; i++)
            {
                if (_slots[i] >= 0 && slot - _slots[i] < BucketCount && _slots[i] <= slot)
                {
                    sum += _bytes[i];
                }
            }
        }

        if (first < 0 || sum == 0)
        {
            return 0;
        }

        var window = _bucketMilliseconds * BucketCount;
        var elapsed = Math.Clamp(nowMilliseconds - first, _bucketMilliseconds, window);
        return sum * 1000d / elapsed;
    }
}
