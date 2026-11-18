// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;

namespace Richasy.Danmaku.Legacy;

/// <summary>
/// Process-wide switch for timing the Win2D renderer. It is off by default, and
/// while it is off the renderer only reads <see cref="IsEnabled"/>.
/// </summary>
public static class DanmakuRenderDiagnostics
{
    private static readonly long SlowThresholdTicks = Stopwatch.Frequency / 20; // 50 ms
    private static volatile bool _isEnabled;
    private static volatile Action<string, double, double, int> _slowOperation;

    /// <summary>
    /// Whether the renderer times its hit tests and draws.
    /// </summary>
    public static bool IsEnabled => _isEnabled;

    /// <summary>
    /// Starts timing. The callback receives the operation name, its duration in milliseconds,
    /// the time spent waiting for render list locks in milliseconds and the number of active
    /// items; a negative value means that the operation does not measure it. It runs on the
    /// thread of the slow operation, which is the render thread for draws.
    /// </summary>
    public static void Enable(Action<string, double, double, int> slowOperation)
    {
        _slowOperation = slowOperation;
        _isEnabled = true;
    }

    /// <summary>
    /// Stops timing.
    /// </summary>
    public static void Disable()
    {
        _isEnabled = false;
        _slowOperation = null;
    }

    internal static void Report(string name, long elapsedTicks, long lockWaitTicks, int itemCount)
    {
        if (elapsedTicks <= SlowThresholdTicks)
        {
            return;
        }

        double lockWait = lockWaitTicks < 0 ? -1 : lockWaitTicks * 1000d / Stopwatch.Frequency;
        _slowOperation?.Invoke(name, elapsedTicks * 1000d / Stopwatch.Frequency, lockWait, itemCount);
    }
}
