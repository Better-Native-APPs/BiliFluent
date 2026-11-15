// Copyright (c) Bili Copilot. All rights reserved.

namespace Richasy.MpvKernel.Core;

public sealed partial class MpvClient
{
    private bool _countTimePositionNotifications;
    private long _timePositionNotificationCount;

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
}
