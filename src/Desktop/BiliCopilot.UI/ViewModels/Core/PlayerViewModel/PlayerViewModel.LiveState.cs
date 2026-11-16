// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>The live media stages; file-loaded is deliberately not the first-frame barrier.</summary>
public enum LiveMediaLoadState { Idle, Resolving, CommandSubmitted, FileLoaded, Ready, Paused, Ended, Failed, Closed }

// Pure decisions, not a second backend or an event pump. Each attempt belongs to one fresh client.
internal sealed class LivePlaybackAttempt : IDisposable
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _cancellation;
    private bool _started;
    private bool _submitted;
    private bool _loaded;
    private bool _frame;
    private bool _invalid;
    private bool _retry;
    private LiveMediaLoadState _state = LiveMediaLoadState.Resolving;

    internal LivePlaybackAttempt(CancellationToken token) => _cancellation = token.Register(Invalidate);
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks",
        Justification = "This per-client event completion never queues behind the media gate awaiting it; continuations run asynchronously.")]
    internal Task<bool> Ready => _ready.Task;
    internal LiveMediaLoadState State { get { lock (_sync) return _state; } }
    internal bool CommandSubmitted { get { lock (_sync) return _submitted; } }
    internal bool FileLoaded { get { lock (_sync) return _loaded; } }
    internal bool HasFrame { get { lock (_sync) return _frame; } }
    internal bool CanRetry { get { lock (_sync) return _retry && !_invalid && !_frame; } }

    internal void MarkStarted() { lock (_sync) { if (!_invalid) _started = true; } }

    internal void MarkSubmitted()
    {
        lock (_sync)
        {
            if (_invalid || _state is LiveMediaLoadState.Failed or LiveMediaLoadState.Ended) return;
            _submitted = true;
            if (!_loaded && !_frame) _state = LiveMediaLoadState.CommandSubmitted;
        }
    }

    internal void MarkLoaded()
    {
        lock (_sync)
        {
            if (_invalid || !_started || _state is LiveMediaLoadState.Failed or LiveMediaLoadState.Ended) return;
            _loaded = true;
            if (!_frame) _state = LiveMediaLoadState.FileLoaded;
        }
    }

    internal void MarkFrame()
    {
        lock (_sync)
        {
            if (_invalid || !_loaded || _state is LiveMediaLoadState.Failed or LiveMediaLoadState.Ended) return;
            _frame = true;
            _state = LiveMediaLoadState.Ready;
            _ready.TrySetResult(true);
        }
    }

    internal void Fail(bool endedBeforeFrame)
    {
        lock (_sync)
        {
            if (_invalid) return;
            _retry = endedBeforeFrame && !_frame;
            _state = LiveMediaLoadState.Failed;
            _ready.TrySetResult(false);
        }
    }

    internal void End()
    {
        lock (_sync)
        {
            if (_invalid || _state == LiveMediaLoadState.Failed) return;
            _retry = !_frame;
            _state = _frame ? LiveMediaLoadState.Ended : LiveMediaLoadState.Failed;
            _ready.TrySetResult(false);
        }
    }

    internal void Invalidate()
    {
        lock (_sync)
        {
            _invalid = true;
            _retry = false;
            _state = LiveMediaLoadState.Closed;
            _ready.TrySetCanceled();
        }
    }

    public void Dispose() { Invalidate(); _cancellation.Dispose(); }
}

// Publish the retained real refresh before executing anything that may notify/reenter the owner.
internal sealed class LiveRefreshGate
{
    private Task? _pending;
    internal Task RunAsync(Func<Task> operation)
    {
        if (_pending is { IsCompleted: false }) return _pending;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = completion.Task;
        _ = CompleteAsync();
        return _pending;
        async Task CompleteAsync()
        {
            await Task.Yield();
            try { await operation(); completion.SetResult(); }
            catch (OperationCanceledException) { completion.SetCanceled(); }
            catch (Exception ex) { completion.SetException(ex); }
        }
    }
}

// A failed real release is terminal. Repeated callers cannot turn a native Dispose no-op into proof.
internal sealed class LiveReleaseBarrier
{
    private Task? _release;
    private Func<Task>[]? _owners;
    internal bool ReleaseConfirmed { get; private set; }
    internal bool IsBlocked => _release is { IsFaulted: true };
    internal int RetainedOwnerCount => _owners?.Length ?? 0;
    internal Task ReleaseAsync(params Func<Task>[] owners)
    {
        if (_release is not null) return _release;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _owners = owners;
        _release = completion.Task;
        _ = CompleteAsync();
        return _release;
        async Task CompleteAsync()
        {
            await Task.Yield();
            var failed = false;
            foreach (var release in owners)
                try { await release(); } catch (Exception) { failed = true; }
            if (failed)
                completion.SetException(new InvalidOperationException("Live media release is incomplete. Owned resources are retained."));
            else
            {
                ReleaseConfirmed = true;
                _owners = null;
                completion.SetResult();
            }
        }
    }
}
