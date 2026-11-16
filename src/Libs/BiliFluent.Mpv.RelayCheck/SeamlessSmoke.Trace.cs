// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Richasy.MpvKernel.Player.Models;

namespace BiliFluent.Mpv.RelayCheck;

internal static partial class SeamlessSmoke
{
    // No native reads or console writes run in the narrow selection window. Only audited,
    // structured relay fields are retained; native messages/URLs/headers are never recorded.
    private sealed class SafeTraceLogger : ILogger
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ConcurrentQueue<TraceEvent> _events = new();
        private readonly ConcurrentDictionary<long, TraceWindow> _windows = new();
        private double _origin;

        private double Now => _clock.Elapsed.TotalMilliseconds - Volatile.Read(ref _origin);

        public void BindClock(double sampleClock) => Volatile.Write(ref _origin, _clock.Elapsed.TotalMilliseconds - sampleClock);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Trace;

        public void RecordProgress(MpvSeamlessSwitchEventArgs progress)
        {
            var now = Now;
            _events.Enqueue(new(now, "phase", new()
            {
                ["Id"] = progress.RequestId, ["Kind"] = progress.Kind.ToString(), ["Phase"] = progress.Phase.ToString(),
                ["Key"] = progress.RelayKey, ["Status"] = progress.Result?.Status.ToString(), ["Reason"] = progress.Result?.Reason.ToString(),
            }));
            if (progress.Phase == MpvSeamlessSwitchPhase.Adding && progress.RelayKey is { } key)
                _windows[progress.RequestId] = new(progress.RequestId, progress.Kind.ToString(), key, now);
            if (_windows.TryGetValue(progress.RequestId, out var window))
            {
                if (progress.Phase == MpvSeamlessSwitchPhase.Selecting) window.SelectingMs = now;
                if (progress.Phase == MpvSeamlessSwitchPhase.Finished)
                {
                    window.FinishedMs = now;
                    window.Status = progress.Result?.Status.ToString();
                    window.Reason = progress.Result?.Reason.ToString();
                    if (progress.Result?.IsSuccess == true) window.SelectedMs = now;
                }
            }
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is not IReadOnlyList<KeyValuePair<string, object?>> fields || fields.Count == 0 || fields[^1].Value is not string template) return;
            var name = template.StartsWith("Relay | read ", StringComparison.Ordinal) ? "read"
                : template.StartsWith("Relay | request ", StringComparison.Ordinal) ? "request"
                : template.StartsWith("Relay | upstream failed ", StringComparison.Ordinal) ? "failover" : null;
            if (name is null) return;
            _events.Enqueue(new(Now, name, fields.Where(pair => pair.Key != "{OriginalFormat}").ToDictionary(pair => pair.Key, pair => pair.Value)));
        }

        public Task WriteAsync(string path)
            => File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Windows = _windows.Values.OrderBy(window => window.Id), Events = _events.ToArray() }, JsonOptions));
    }

    private sealed record TraceEvent(double EndMs, string Event, Dictionary<string, object?> Data);

    private sealed class TraceWindow(long id, string kind, string key, double addingMs)
    {
        public long Id { get; } = id;
        public string Kind { get; } = kind;
        public string Key { get; } = key;
        public double AddingMs { get; } = addingMs;
        public double? SelectingMs { get; set; }
        public double? SelectedMs { get; set; }
        public double? FinishedMs { get; set; }
        public string? Status { get; set; }
        public string? Reason { get; set; }
    }
}
