// Copyright (c) Bili Copilot. All rights reserved.

namespace Richasy.MpvKernel.Core;

public sealed partial class MpvClient
{
    private bool _countTimePositionNotifications;
    private long _timePositionNotificationCount;
    private long _uiPostCount;

    /// <summary>
    /// Enables counting raw time-pos notifications before UI dispatch.
    /// </summary>
    public bool IsTimePositionNotificationCountingEnabled
    {
        get => Volatile.Read(ref _countTimePositionNotifications);
        set => Volatile.Write(ref _countTimePositionNotifications, value);
    }

    /// <summary>
    /// Gets the cumulative notification count while diagnostic counting is enabled.
    /// </summary>
    public long TimePositionNotificationCount => Interlocked.Read(ref _timePositionNotificationCount);

    /// <summary>
    /// Gets the cumulative number of player event posts to the UI context while diagnostic counting is enabled.
    /// </summary>
    public long UiPostCount => Interlocked.Read(ref _uiPostCount);

    /// <summary>
    /// Sums the peak and the average render time of the passes of fresh frames, in nanoseconds,
    /// as the video output reports them in <c>vo-passes</c>. Blocks like the other direct reads.
    /// </summary>
    public MpvError GetFreshPassTimes(out long peakNanoseconds, out long averageNanoseconds)
    {
        if (IsDisposed)
        {
            peakNanoseconds = 0;
            averageNanoseconds = 0;
            return MpvError.Uninitialized;
        }

        return _handle.GetFreshPassTimes(out peakNanoseconds, out averageNanoseconds);
    }

    internal void CountUiPost()
    {
        if (IsTimePositionNotificationCountingEnabled)
        {
            Interlocked.Increment(ref _uiPostCount);
        }
    }
}
