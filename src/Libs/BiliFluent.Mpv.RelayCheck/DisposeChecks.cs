// Copyright (c) Bili Copilot. All rights reserved.

using System.Reflection;
using Microsoft.Extensions.Logging;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;

namespace BiliFluent.Mpv.RelayCheck;

internal static partial class DisposeChecks
{
    public static async Task RunAsync(Report report)
    {
        await report.RunAsync("drain: synchronous segments", () => CheckGateAsync(report));
        await report.RunAsync("drain: client results", () => CheckClientResultsAsync(report));
        await report.RunAsync("drain: event-thread reentry", () => CheckEventThreadAsync(report));
        await report.RunAsync("drain: event-thread warning", () => CheckBlockedLoopAsync(report));
    }

    private static async Task CheckGateAsync(Report report)
    {
        var gate = new MpvWorkGate();
        var lease = gate.TryEnter();
        var closed = gate.CloseAsync();
        report.Check("drain: active segment prevents completion", !closed.IsCompleted);
        using (var late = gate.TryEnter()) report.Check("drain: late segment refused", !late.IsActive);
        lease.Dispose();
        await closed;
        report.Check("drain: release proves completion", closed.IsCompletedSuccessfully);
        report.Check("drain: closed gate has one result", ReferenceEquals(closed, gate.CloseAsync()));
    }

    private static async Task CheckClientResultsAsync(Report report)
    {
        var destroyed = 0;
        var log = new CheckLogger();
        var native = new MpvCoreHandle(() => Interlocked.Increment(ref destroyed));
        var client = new MpvClient(native, log);
        client.RunClientCallback(() => throw new InvalidOperationException("private request metadata"));
        report.Check("drain: callback exception safely logged", log.Errors == 1 && !log.Messages.Any(message => message.Contains("private request", StringComparison.Ordinal)));
        var calls = Enumerable.Range(0, 16).Select(_ => client.DisposeAsync().AsTask()).ToArray();
        await Task.WhenAll(calls);
        report.Check("drain: client calls share result", calls.All(task => ReferenceEquals(task, calls[0])));
        report.Equal("drain: client destroyed once", 1, destroyed);
        var ranLate = false;
        client.RunClientCallback(() => ranLate = true);
        report.Check("drain: client callback refused after close", !ranLate);
        report.Equal("drain: queued native read cannot access handle", MpvError.Uninitialized, native.GetProperty("volume", out double _));
        report.Equal("drain: queued native command cannot access handle", MpvError.Uninitialized, native.SendTrackCommand(1, ["audio-add", "unused"]));

        var failure = new InvalidOperationException("Injected destruction failure.");
        var failedClient = new MpvClient(new MpvCoreHandle(() => throw failure));
        var first = failedClient.DisposeAsync().AsTask();
        report.Check("drain: initial failure retained", ReferenceEquals(failure, await CaptureAsync(first)));
        var again = failedClient.DisposeAsync().AsTask();
        report.Check("drain: repeated failure shares task", ReferenceEquals(first, again));
        report.Check("drain: repeated failure stays failed", ReferenceEquals(failure, await CaptureAsync(again)));
    }

    private static async Task CheckEventThreadAsync(Report report)
    {
        var destroyed = 0;
        var client = new MpvClient(new MpvCoreHandle(() => Interlocked.Increment(ref destroyed)));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = Task.Run(async () =>
        {
            await start.Task;
            client.RunClientCallback(() => requested.TrySetResult(client.DisposeAsync().AsTask()));
        });
        SetLoop(client, loop);
        start.SetResult();
        var close = await requested.Task;
        await close;
        report.Check("drain: event-thread disposal exits without self-join", loop.IsCompletedSuccessfully && destroyed == 1);
    }

    private static async Task CheckBlockedLoopAsync(Report report)
    {
        var destroyed = 0;
        var log = new CheckLogger();
        var client = new MpvClient(new MpvCoreHandle(() => Interlocked.Increment(ref destroyed)), log);
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetLoop(client, exit.Task);
        var close = client.DisposeAsync().AsTask();
        await log.Warning.Task.WaitAsync(TimeSpan.FromSeconds(8));
        report.Check("drain: timeout warns but cannot destroy", !close.IsCompleted && destroyed == 0);
        exit.SetResult();
        await close;
        report.Equal("drain: true event exit allows destruction", 1, destroyed);
    }

    private static async Task<Exception?> CaptureAsync(Task task)
    {
        try { await task; return null; }
        catch (Exception ex) { return ex; }
    }

    private static void SetLoop(MpvClient client, Task loop)
    {
        typeof(MpvClient).GetField("_eventLoopTask", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, loop);
        typeof(MpvClient).GetField("_eventCts", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, new CancellationTokenSource());
    }

    private sealed class CheckLogger : ILogger
    {
        public readonly List<string> Messages = [];
        public readonly TaskCompletionSource Warning = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Errors;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add(formatter(state, exception));
            if (logLevel == LogLevel.Error) Interlocked.Increment(ref Errors);
            if (logLevel == LogLevel.Warning) Warning.TrySetResult();
        }
    }
}
