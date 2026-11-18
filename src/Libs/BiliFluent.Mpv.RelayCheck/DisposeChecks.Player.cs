// Copyright (c) Bili Copilot. All rights reserved.

using System.Reflection;
using Microsoft.Extensions.Logging;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Core.Enums;
using Richasy.MpvKernel.Core.Models;
using Richasy.MpvKernel.Player;
using Richasy.MpvKernel.Player.Models;

namespace BiliFluent.Mpv.RelayCheck;

internal static partial class DisposeChecks
{
    private static async Task RunPlayerChecksAsync(Report report)
    {
        await report.RunAsync("drain: player queue and results", () => CheckPlayerQueueAsync(report));
        await report.RunAsync("drain: player callback reentry", () => CheckPlayerReentryAsync(report));
        await report.RunAsync("drain: player event-thread reentry", () => CheckPlayerEventThreadAsync(report));
        await report.RunAsync("drain: detached remote awaits", () => CheckDetachedAwaitsAsync(report));
        await report.RunAsync("drain: player failure and exceptions", () => CheckPlayerFailureAsync(report));
        await report.RunAsync("drain: normal source UI context", () => CheckNormalUiContextAsync(report));
    }

    private static async Task CheckPlayerQueueAsync(Report report)
    {
        var context = new ManualContext();
        var client = new MpvClient(new MpvCoreHandle(() => { }));
        var source = new SourceResolver();
        var player = CreatePlayer(context, client, source);
        SetPlayer(player, nameof(player.PlaybackState), MpvPlayerState.Playing);
        foreach (var (name, value) in new (string, object)[]
        {
            (nameof(player.Position), 15d), (nameof(player.Duration), 100d), (nameof(player.Volume), 70d),
            (nameof(player.PlaybackRate), 1.5d), (nameof(player.IsLoading), true), (nameof(player.IsFullScreen), true),
            (nameof(player.IsCompactOverlay), true), (nameof(player.IsPlaybackInitialized), true), (nameof(player.CacheSpeed), 11L),
        }) SetPlayer(player, name, value);
        context.PumpAll();
        var notifications = 0;
        player.PropertyChanged += (_, _) => notifications++;
        player.TrackUpdated += (_, _) => notifications++;
        player.RelayUpstreamFailed += (_, _) => notifications++;
        player.SeamlessSwitchProgress += (_, _) => notifications++;
        player.SeamlessRelayUpstreamFailed += (_, _) => notifications++;
        Notify(player, MpvClientEventId.PositionChanged, 99d);
        Notify(player, MpvClientEventId.DurationChanged, 200d);
        Notify(player, MpvClientEventId.StateChanged, MpvPlayerState.Seeking);
        player.RaisePropertyChanged(nameof(player.Title));
        InvokePlayer(player, "PostCounted", new SendOrPostCallback(_ => notifications++));
        var queued = context.Count;
        var closes = new Task[16];
        await Task.WhenAll(Enumerable.Range(0, closes.Length).Select(index => Task.Run(() => { closes[index] = player.DisposeAsync().AsTask(); })));
        report.Check("drain: parallel player calls share one task", closes.All(task => ReferenceEquals(task, closes[0])));
        await context.WaitForCountAsync(queued + 1).WaitAsync(TimeSpan.FromSeconds(30));
        context.PumpNewest();
        await Task.WhenAll(closes);
        report.Check("drain: terminal state is complete at return", player.PlaybackState == MpvPlayerState.Idle && player.Position == 0
            && player.Duration == 0 && player.Volume == 0 && player.PlaybackRate == 0 && !player.IsLoading
            && !player.IsFullScreen && !player.IsCompactOverlay && !player.IsPlaying && !player.IsBuffering && player.IsStopped);
        report.Check("drain: unrelated final properties are preserved", player.Title == "retained title" && player.IsPlaybackInitialized && player.CacheSpeed == 11);
        report.Check("drain: shutdown did not require old UI queue to run", context.Count == queued && queued >= 5);
        var atReturn = notifications;
        context.PumpAll();
        report.Equal("drain: stale UI posts have no effects", atReturn, notifications);
        var versionField = typeof(MpvPlayer).GetField("_positionUpdateVersion", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var version = versionField.GetValue(player);
        Notify(player, MpvClientEventId.PositionChanged, 400d);
        Notify(player, MpvClientEventId.TrackCountChanged, null);
        foreach (var handler in new[] { "OnFileLoaded", "OnFileLoading", "OnFileEnd", "OnClientShutdown", "OnStatusTimerElapsedAsync", "OnHistoryTimerElapsedAsync", "OnSeamlessFileLoading" })
            InvokePlayer(player, handler, null, null);
        player.RaisePropertyChanged(nameof(player.Title));
        await player.RefreshStatusAsync();
        await player.InitializeAsync(alsoPlay: false);
        report.Check("drain: replay refused after close", !await player.ReplayAsync());
        context.PumpAll();
        report.Equal("drain: late handlers cannot notify", atReturn, notifications);
        report.Check("drain: late handler cannot change even version", Equals(version, versionField.GetValue(player)));
        report.Equal("drain: closed source work is not accepted", 0, source.Calls);
        report.Check("drain: repeated player result remains shared", ReferenceEquals(closes[0], player.DisposeAsync().AsTask()));
    }

    private static async Task CheckPlayerReentryAsync(Report report)
    {
        var context = new ManualContext();
        var player = CreatePlayer(context, new MpvClient(new MpvCoreHandle(() => { })));
        context.PumpAll();
        var requested = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName != nameof(player.PlaybackState) || player.PlaybackState != MpvPlayerState.End) return;
            var close = player.DisposeAsync().AsTask();
            requested.TrySetResult(close);
            await close;
            returned.TrySetResult();
        };
        InvokePlayer(player, "OnFileEnd", null, EventArgs.Empty);
        context.PumpNewest();
        var disposal = await requested.Task;
        await PumpUntilAsync(context, disposal);
        await PumpUntilAsync(context, returned.Task);
        report.Check("drain: player callback can await close without waiting on itself", returned.Task.IsCompletedSuccessfully && player.IsStopped);
        context.PumpAll();
    }

    private static async Task CheckPlayerEventThreadAsync(Report report)
    {
        var context = new ManualContext();
        var client = new MpvClient(new MpvCoreHandle(() => { }));
        var player = CreatePlayer(context, client);
        var requested = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ReachFileEnd += async (_, _) =>
        {
            var close = player.DisposeAsync().AsTask();
            requested.TrySetResult(close);
            await close;
            returned.TrySetResult();
        };
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = Task.Run(async () =>
        {
            await start.Task;
            client.RunClientCallback(() =>
            {
                var ended = (EventHandler?)typeof(MpvClient).GetField(nameof(client.ReachFileEnd), BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client);
                ended?.Invoke(client, EventArgs.Empty);
            });
        });
        SetLoop(client, loop);
        start.SetResult();
        var disposal = await requested.Task;
        await PumpUntilAsync(context, disposal);
        await returned.Task;
        report.Check("drain: player disposal from event thread ends normally", loop.IsCompletedSuccessfully && client.IsDisposed && player.IsStopped);
        context.PumpAll();
    }

    private static async Task CheckDetachedAwaitsAsync(Report report)
    {
        var context = new ManualContext();
        var history = new HistoryResolver();
        var log = new CheckLogger();
        var player = CreatePlayer(context, new MpvClient(new MpvCoreHandle(() => { })), history: history, logger: log);
        SetPlayer(player, nameof(player.Position), 15d);
        SetPlayer(player, nameof(player.Duration), 100d);
        SetPlayer(player, nameof(player.PlaybackState), MpvPlayerState.Playing);
        context.PumpAll();
        var notifications = 0;
        player.PropertyChanged += (_, _) => notifications++;
        InvokePlayer(player, "OnHistoryTimerElapsedAsync", null, null);
        report.Equal("drain: history request began", 1, history.NormalCalls);
        var close = player.DisposeAsync().AsTask();
        await PumpUntilAsync(context, close);
        report.Check("drain: pending periodic network is not joined", !history.Normal.Task.IsCompleted);
        report.Check("drain: original exiting history is kept once", history.ExitCalls == 1 && history.ExitPosition == 15
            && history.ExitDuration == 100 && history.ExitState == MpvPlayerState.Playing);
        var atReturn = notifications;
        history.Normal.SetException(new InvalidOperationException("private request metadata"));
        await log.Error.Task;
        context.PumpAll();
        report.Check("drain: late history exception is contained and redacted", log.Errors == 1 && !log.Messages.Any(message => message.Contains("private request", StringComparison.Ordinal)));
        report.Equal("drain: late history has no owner side effects", atReturn, notifications);

        for (var mode = 0; mode < 3; mode++)
        {
            var delayedContext = new ManualContext();
            var source = new SourceResolver { Pending = mode == 1 ? null : new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var delayedHistory = new HistoryResolver { Start = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var delayedPlayer = CreatePlayer(delayedContext, new MpvClient(new MpvCoreHandle(() => { })), source, mode == 1 ? delayedHistory : null);
            var changes = 0;
            delayedPlayer.PropertyChanged += (_, _) => Interlocked.Increment(ref changes);
            Task operation = mode == 2 ? delayedPlayer.ReplayAsync() : delayedPlayer.InitializeAsync(alsoPlay: false);
            await PumpUntilAsync(delayedContext, delayedPlayer.DisposeAsync().AsTask());
            var saved = Volatile.Read(ref changes);
            if (source.Pending is { } pending) pending.SetResult(new("unused", title: "late title"));
            else delayedHistory.Start!.SetResult(5);
            await operation;
            delayedContext.PumpAll();
            report.Check($"drain: remote await {mode} cannot resurrect player", Volatile.Read(ref changes) == saved && delayedPlayer.Title == "retained title"
                && delayedPlayer.Position == 0 && !delayedPlayer.IsInternalLoading);
            if (operation is Task<bool> replay) report.Check("drain: late replay reports no load", !await replay);
        }
    }

    private static async Task CheckNormalUiContextAsync(Report report)
    {
        var context = new ManualContext();
        var source = new SourceResolver { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var player = CreatePlayer(context, new MpvClient(new MpvCoreHandle(() => { })), source);
        var notifiedOnUi = false;
        player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(player.Title)) notifiedOnUi = ReferenceEquals(SynchronizationContext.Current, context);
        };
        Task? initialize = null;
        context.Run(() => initialize = player.InitializeAsync(alsoPlay: false));
        source.Pending.SetResult(new("unused", title: "normal title"));
        await PumpUntilAsync(context, initialize!);
        report.Check("drain: normal initialization keeps UI-affine notifications", notifiedOnUi && player.Title == "normal title");
        await PumpUntilAsync(context, player.DisposeAsync().AsTask());
        context.PumpAll();
    }

    private static async Task CheckPlayerFailureAsync(Report report)
    {
        var context = new ManualContext();
        var log = new CheckLogger();
        var failure = new InvalidOperationException("Injected native disposal failure.");
        var player = CreatePlayer(context, new MpvClient(new MpvCoreHandle(() => throw failure)), logger: log);
        SetPlayer(player, nameof(player.Duration), 20d);
        SetPlayer(player, nameof(player.Volume), 70d);
        context.PumpAll();
        Notify(player, MpvClientEventId.VolumeChanged, "not a number");
        context.PumpNewest();
        report.Equal("drain: queued callback exception is contained", 1, log.Errors);
        player.PropertyChanging += (_, _) => throw new InvalidOperationException("private property metadata");
        player.PropertyChanged += (_, _) => throw new InvalidOperationException("private property metadata");
        var close = player.DisposeAsync().AsTask();
        var caught = await CaptureAsync(PumpUntilAsync(context, close));
        report.Check("drain: player preserves native failure", ReferenceEquals(failure, caught));
        report.Check("drain: player failure cannot become later success", ReferenceEquals(close, player.DisposeAsync().AsTask())
            && ReferenceEquals(failure, await CaptureAsync(player.DisposeAsync().AsTask())));
        report.Check("drain: throwing observers do not prevent terminal assignments", player.Duration == 0 && player.Volume == 0 && player.IsStopped);
        report.Check("drain: observer exceptions logged without metadata", log.Errors >= 5 && !log.Messages.Any(message => message.Contains("private property", StringComparison.Ordinal)));
        context.PumpAll();
    }

    private sealed class HistoryResolver : IMpvMediaHistoryResolver
    {
        public readonly TaskCompletionSource Normal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<double>? Start;
        public int NormalCalls;
        public int ExitCalls;
        public double ExitPosition;
        public double ExitDuration;
        public MpvPlayerState ExitState;

        public Task<double> GetStartPositionAsync() => Start?.Task ?? Task.FromResult(0d);

        public Task SaveHistoryAsync(double position, double duration, MpvPlayerState state, bool isExiting = false)
        {
            if (!isExiting)
            {
                NormalCalls++;
                return Normal.Task;
            }

            ExitCalls++;
            ExitPosition = position;
            ExitDuration = duration;
            ExitState = state;
            return Task.CompletedTask;
        }
    }

    private static MpvPlayer CreatePlayer(ManualContext context, MpvClient client, IMpvMediaSourceResolver? source = null,
        IMpvMediaHistoryResolver? history = null, ILogger? logger = null)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try { return new(client, source ?? new SourceResolver(), history, logger: logger) { Title = "retained title" }; }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void InvokePlayer(MpvPlayer player, string name, params object?[] arguments)
        => typeof(MpvPlayer).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(player, arguments);

    private static void SetPlayer(MpvPlayer player, string name, object value)
        => typeof(MpvPlayer).GetProperty(name)!.SetValue(player, value);

    private static void Notify(MpvPlayer player, MpvClientEventId id, object? data)
        => InvokePlayer(player, "OnDataNotify", null, new MpvClientNotifyEventArgs(id, data));

    private static async Task PumpUntilAsync(ManualContext context, Task completion)
    {
        while (!completion.IsCompleted)
        {
            await Task.WhenAny(completion, context.WaitForPostAsync()).WaitAsync(TimeSpan.FromSeconds(30));
            if (!completion.IsCompleted) context.PumpNewest();
        }

        await completion;
    }

    private sealed class ManualContext : SynchronizationContext
    {
        private readonly object _sync = new();
        private readonly List<(SendOrPostCallback Callback, object? State)> _posts = [];
        private readonly List<(int Count, TaskCompletionSource Completion)> _counts = [];
        private TaskCompletionSource _available = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Count { get { lock (_sync) return _posts.Count; } }

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_sync)
            {
                _posts.Add((d, state));
                _available.TrySetResult();
                for (var i = _counts.Count - 1; i >= 0; i--)
                {
                    if (_posts.Count < _counts[i].Count) continue;
                    _counts[i].Completion.TrySetResult();
                    _counts.RemoveAt(i);
                }
            }
        }

        public Task WaitForPostAsync() { lock (_sync) return _posts.Count == 0 ? _available.Task : Task.CompletedTask; }

        public Task WaitForCountAsync(int count)
        {
            lock (_sync)
            {
                if (_posts.Count >= count) return Task.CompletedTask;
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _counts.Add((count, completion));
                return completion.Task;
            }
        }

        public void PumpNewest()
        {
            (SendOrPostCallback Callback, object? State) post;
            lock (_sync)
            {
                if (_posts.Count == 0) return;
                post = _posts[^1];
                _posts.RemoveAt(_posts.Count - 1);
                if (_posts.Count == 0) _available = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            Run(() => post.Callback(post.State));
        }

        public void Run(Action action)
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(this);
            try { action(); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        public void PumpAll() { while (Count > 0) PumpNewest(); }
    }

    private sealed class SourceResolver : IMpvMediaSourceResolver
    {
        public TaskCompletionSource<MpvMediaSource>? Pending;
        public int Calls;

        public Task<MpvMediaSource> GetSourceAsync()
        {
            Calls++;
            return Pending?.Task ?? Task.FromResult(new MpvMediaSource("unused", title: "late title"));
        }

        public IMpvMediaSourceResolver Clone() => this;
    }
}
