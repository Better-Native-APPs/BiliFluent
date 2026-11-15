// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using Microsoft.Extensions.Logging;
using Richasy.Danmaku.Legacy;
using System.Diagnostics;
using System.Globalization;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// Process-wide switch and log sink of the playback stutter diagnostics.
/// </summary>
/// <remarks>
/// Running diagnostic sessions hold it on. Call sites read <see cref="IsEnabled"/> once
/// and do nothing else while it is off. No address, title or user identity is ever logged.
/// </remarks>
internal static class PlaybackStutterDiagnostics
{
    private static readonly long _slowThresholdTicks = Stopwatch.Frequency / 20; // 50 ms
    private static readonly object _gate = new();
    private static volatile bool _isEnabled;
    private static volatile ILogger? _logger;
    private static int _sessions;

    internal static bool IsEnabled => _isEnabled;

    internal static void Acquire(ILogger logger)
    {
        lock (_gate)
        {
            _logger = logger;
            if (++_sessions != 1) return;
            DanmakuRenderDiagnostics.Enable(OnSlowRender);
            _isEnabled = true;
        }
    }

    internal static void Release()
    {
        lock (_gate)
        {
            if (_sessions == 0 || --_sessions != 0) return;
            _isEnabled = false;
            DanmakuRenderDiagnostics.Disable();
            _logger = null;
        }
    }

    /// <summary>
    /// Whether more than 50 ms have passed since a <see cref="Stopwatch.GetTimestamp"/> value.
    /// </summary>
    internal static bool IsSlow(long startTimestamp, out double milliseconds)
    {
        var ticks = Stopwatch.GetTimestamp() - startTimestamp;
        milliseconds = ticks * 1000d / Stopwatch.Frequency;
        return ticks > _slowThresholdTicks;
    }

    internal static void ReportSlow(string name, double milliseconds, string details)
    {
        if (_logger is not { } logger || !logger.IsEnabled(LogLevel.Information)) return;
        var elapsed = FormatMilliseconds(milliseconds);
        logger.LogInformation("PlaybackStutter | slow name={Name} ms={Ms} {Details}", name, elapsed, details);
    }

    internal static string FormatMilliseconds(double milliseconds)
        => milliseconds.ToString("0.0", CultureInfo.InvariantCulture);

    internal static string RendererName(DanmakuRendererType renderer)
        => renderer == DanmakuRendererType.Win2D ? "win2d" : "mpv";

    // Runs on the thread of the slow operation: the UI thread for hit tests, the render thread for draws.
    private static void OnSlowRender(string name, double milliseconds, double lockWaitMilliseconds, int itemCount)
    {
        var details = lockWaitMilliseconds >= 0
            ? "lock-wait-ms=" + FormatMilliseconds(lockWaitMilliseconds)
            : "items=" + itemCount.ToString(CultureInfo.InvariantCulture);
        ReportSlow(name, milliseconds, details);
    }
}
