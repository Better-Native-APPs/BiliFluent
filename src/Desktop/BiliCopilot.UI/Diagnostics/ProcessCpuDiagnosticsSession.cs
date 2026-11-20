// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace BiliCopilot.UI.Diagnostics;

internal sealed class ProcessCpuDiagnosticsSession
{
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(5));
    private readonly uint _uiThreadId;
    private readonly ILogger _logger;
    private int _stopped;
    private bool _playing;
    private string _pageTypeName = "n/a";

    internal ProcessCpuDiagnosticsSession(uint uiThreadId, ILogger logger)
    {
        _uiThreadId = uiThreadId;
        _logger = logger;
        Completion = Task.Run(SampleAsync);
    }

    internal Task Completion { get; }

    internal void UpdateContext(bool playing, string pageTypeName)
    {
        Volatile.Write(ref _playing, playing);
        Volatile.Write(ref _pageTypeName, pageTypeName);
    }

    internal void Stop()
    {
        Interlocked.Exchange(ref _stopped, 1);
        _timer.Dispose(); // Wakes the worker immediately; never joins or posts to a dispatcher.
    }

    private async Task SampleAsync()
    {
        try
        {
            using var reader = new ProcessCpuThreadReader();
            var diagnostics = new ProcessCpuDiagnostics();
            if (Volatile.Read(ref _stopped) != 0) return;
            var baseline = reader.Capture();
            _ = diagnostics.Advance(reader.Threads, baseline, _uiThreadId);
            while (await _timer.WaitForNextTickAsync().ConfigureAwait(false))
            {
                if (Volatile.Read(ref _stopped) != 0) return;
                var started = Stopwatch.GetTimestamp();
                var counters = reader.Capture();
                var sample = diagnostics.Advance(reader.Threads, counters, _uiThreadId);
                var samplingMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (Volatile.Read(ref _stopped) != 0) return;
                if (sample.ShouldLog && _logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("{CpuSample}", ProcessCpuDiagnostics.Format(sample, Environment.ProcessorCount,
                        Volatile.Read(ref _playing), Volatile.Read(ref _pageTypeName), samplingMilliseconds));
            }
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _stopped) == 0)
            {
                try { _logger.LogWarning("Perf | cpu stopped error={Error}", ex.GetType().Name); }
                catch { /* Optional diagnostics cannot fault an unobserved worker. */ }
            }
        }
        finally
        {
            Stop();
        }
    }
}
