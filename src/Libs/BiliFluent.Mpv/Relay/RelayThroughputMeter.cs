// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;

namespace Richasy.MpvKernel.Relay;

/// <summary>One upstream response, split wherever player backpressure interrupts its throughput.</summary>
internal sealed class RelayThroughputMeter
{
    internal const long MinimumBytes = 256 * 1024;
    internal const long FastMinimumBytes = 2 * 1024 * 1024;
    internal static readonly TimeSpan MinimumDuration = TimeSpan.FromMilliseconds(200);
    internal static readonly TimeSpan FastMinimumDuration = TimeSpan.FromMilliseconds(20);
    internal static readonly TimeSpan MaximumWriteWait = TimeSpan.FromMilliseconds(25);
    internal static readonly TimeSpan ResumeReadWait = TimeSpan.FromMilliseconds(2);
    private static long _sequence;
    private static long _segmentSequence;
    private long _segmentId;
    private long _started;
    private long _bytes;
    private long _upstreamWait;
    private bool _active;
    private bool _needsDrain;

    public void ReadCompleted(int count, long started, long completed)
    {
        if (_active)
        {
            _bytes += count;
            _upstreamWait += completed - started;
        }
        else if (!_needsDrain || Stopwatch.GetElapsedTime(started, completed) >= ResumeReadWait)
        {
            // Neither the first body read nor the read proving a drained socket is network evidence.
            _started = completed;
            _segmentId = Interlocked.Increment(ref _segmentSequence);
            _bytes = 0;
            _upstreamWait = 0;
            _active = true;
            _needsDrain = false;
        }
    }

    public RelayThroughputObservation? WriteCompleted(long started, long completed)
    {
        var blocked = Stopwatch.GetElapsedTime(started, completed) > MaximumWriteWait;
        var observation = Observe(blocked ? started : completed);
        if (blocked)
        {
            _active = false;
            _needsDrain = true;
        }

        return observation;
    }

    public RelayThroughputObservation? End(long completed)
    {
        var observation = Observe(completed);
        _active = false;
        _needsDrain = true;
        return observation;
    }

    private RelayThroughputObservation? Observe(long completed)
    {
        if (!_active) return null;
        var duration = Stopwatch.GetElapsedTime(_started, completed);
        if (!(_bytes >= MinimumBytes && duration >= MinimumDuration)
            && !(_bytes >= FastMinimumBytes && duration >= FastMinimumDuration)) return null;

        var wait = Stopwatch.GetElapsedTime(0, _upstreamWait);
        return new(_bytes / duration.TotalSeconds, _bytes, duration, wait,
            wait >= duration / 2, Interlocked.Increment(ref _sequence), DateTimeOffset.UtcNow) { SegmentId = _segmentId };
    }
}
