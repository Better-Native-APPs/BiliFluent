#nullable enable

using BiliCopilot.UI.Toolkits;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Live.Application;

/// <summary>
/// UI-confined shared-active route owner. Independent players never enter this coordinator.
/// Cancellation precedes all gate waits; only an actual completed close permits handoff.
/// </summary>
internal sealed class LiveRouteCoordinator
{
    private readonly Action _checkAccess;
    private readonly Func<Task> _closeEmbedded;
    private readonly SharedPlaybackOwnerRegistry _owners;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<Request> _requests = [];
    private Request? _latest;
    private bool _stopping;
    private bool _releaseUnconfirmed;
    private Task _navigationLiveClose = Task.CompletedTask;
    private Task? _shutdown;
    private Task? _shutdownWorker;

    internal LiveRouteCoordinator(Action checkAccess, Func<Task> closeEmbedded, Action reportFailure)
    {
        _checkAccess = checkAccess;
        _closeEmbedded = closeEmbedded;
        _owners = new SharedPlaybackOwnerRegistry(checkAccess, reportFailure);
    }

    internal bool HasWork => _owners.HasWork || _requests.Count != 0 || _releaseUnconfirmed
        || !_navigationLiveClose.IsCompletedSuccessfully || _shutdown is { IsCompleted: false };
    internal bool HasLiveWork => _owners.LiveOwners.Any() || _requests.Any(request => request.IsLive)
        || !_navigationLiveClose.IsCompletedSuccessfully;
    internal bool IsBlocked => _stopping || _releaseUnconfirmed || _owners.IsBlocked;
    internal IEnumerable<object> LiveOwners => _owners.LiveOwners;
    internal bool IsLiveOwner(object owner) => _owners.IsLiveOwner(owner);
    internal Task CloseOwnerAsync(object owner) => _owners.CloseAsync(owner);
    internal bool RequestOwnerRelease(object owner) => _owners.RequestRelease(owner);

    internal Task RegisterNavigationLiveClose(Task close)
    {
        _checkAccess();
        // The caller already issued this owner close; returning it holds no route lease.
        #pragma warning disable VSTHRD003
        if (ReferenceEquals(_navigationLiveClose, close)) return close;
        #pragma warning restore VSTHRD003
        // Never replace an unfinished or failed release with a later successful/no-op close.
        _navigationLiveClose = _navigationLiveClose.IsCompletedSuccessfully
            ? close : Task.WhenAll(_navigationLiveClose, close);
        _ = ObserveNavigationLiveCloseAsync(_navigationLiveClose);
        return _navigationLiveClose;
    }

    internal Task WaitForNavigationLiveCloseAsync()
    {
        _checkAccess();
        // Return the registered actual close, not a wrapper or a lease held by its producer.
        #pragma warning disable VSTHRD003
        return _navigationLiveClose;
        #pragma warning restore VSTHRD003
    }

    private async Task ObserveNavigationLiveCloseAsync(Task close)
    {
        try
        {
            // This observer never owns a route lease; the real close cannot depend on it.
            #pragma warning disable VSTHRD003
            await close;
            #pragma warning restore VSTHRD003
        }
        catch (Exception error)
        {
            _releaseUnconfirmed = true;
            SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<LiveRouteCoordinator>>(),
                logger =>
                {
                    if (logger.IsEnabled(LogLevel.Error))
                    {
                        var failure = LiveCloseDiagnostics.GetFailure(error);
                        logger.LogError("Live navigation close is incomplete; ownership is retained and replacement is blocked. step={Step:l} error={Error:l}",
                            failure.Step, failure.Error);
                    }
                });
        }
    }

    internal Task OpenLiveAsync(Func<Admission, CancellationToken, Task> open, CancellationToken token)
        => Queue(open, isLive: true, token);

    internal Task OpenVideoAsync(Func<Admission, CancellationToken, Task> open, CancellationToken token)
        => Queue(open, isLive: false, token);

    private Task Queue(Func<Admission, CancellationToken, Task> open, bool isLive, CancellationToken token)
    {
        _checkAccess();
        token.ThrowIfCancellationRequested();
        if (IsBlocked) throw Failure();
        var previous = _latest;
        var navigation = _navigationLiveClose; // Capture before a close/cancel callback can register this request's Back.
        var request = new Request(token, isLive);
        _latest = request;
        _requests.Add(request); // Publish before cancellation callbacks or owner methods can reenter.
        request.Cancellation = previous is not null && !previous.Done.Task.IsCompleted
            ? Invoke(() => previous.Stop.CancelAsync()) : Task.CompletedTask;
        // Start cancellation immediately, then join both real owner close and any navigation drain.
        request.OwnerClose = Task.WhenAll(Invoke(() => LiveCloseDiagnostics.Invoke("replace", _owners.CloseAllAsync)), navigation);
        request.EmbeddedClose = Invoke(_closeEmbedded); // Cancels/drains the actual pending VOD owner.
        request.Worker = RunAsync(request, previous, open);
        // T1-06: Request.Done is published before route callbacks; return the identical owner completion without a synchronous UI wait.
        #pragma warning disable VSTHRD003
        return request.Done.Task;
        #pragma warning restore VSTHRD003
    }

    private async Task RunAsync(Request request, Request? previous, Func<Admission, CancellationToken, Task> open)
    {
        await Task.Yield(); // All task slots/ownership above are observable before work resumes.
        try
        {
            var failed = false;
            try {
                // Cancellation callback faults finish cancellation; they do not establish owner release.
                #pragma warning disable VSTHRD003
                await request.Cancellation;
                #pragma warning restore VSTHRD003
            } catch { WriteCancellationFailure(); }
            try {
                // T1-07: Both real closes start before these joins; close paths do not reacquire the held route lease.
                #pragma warning disable VSTHRD003
                await Task.WhenAll(request.OwnerClose, request.EmbeddedClose);
                #pragma warning restore VSTHRD003
            }
            catch { failed = true; _releaseUnconfirmed = true; }
            // Previous source/open failure does not prove release failure. Its real close above does.
            if (previous is not null) try {
                // T1-08: This is the captured predecessor request, not this runner; its independently owned completion must drain before replacement.
                #pragma warning disable VSTHRD003
                await previous.Done.Task;
                #pragma warning restore VSTHRD003
            } catch { }
            if (previous?.Worker is not null)
            {
#pragma warning disable VSTHRD003 // The captured predecessor worker has no dependency on this successor.
                await previous.Worker;
#pragma warning restore VSTHRD003
            }
            if (failed || _releaseUnconfirmed) throw Failure();
            CheckRequest(request);
            // Both media routes take exactly one permit here. Registration transfers it to the
            // composite owner; success, cancellation and failed initialization cannot release it early.
            using var admission = await AcquireAsync(request);
            CheckRequest(request);
            await open(admission, request.Stop.Token);
            CheckRequest(request);
            request.Done.TrySetResult();
        }
        catch
        {
            // This is outside the player's owned initialization/gate. Never close from work that
            // the same Player.CloseAsync must drain, and never retarget cleanup to a successor.
            if (request.Owner is { } owner)
            {
                try { await _owners.CloseAsync(owner); }
                catch { _releaseUnconfirmed = true; }
            }
            if ((request.Stop.IsCancellationRequested || !ReferenceEquals(_latest, request))
                && !_releaseUnconfirmed && !_owners.IsBlocked)
                request.Done.TrySetCanceled(request.Stop.Token);
            else request.Done.TrySetException(Failure());
        }
        finally
        {
            _requests.Remove(request);
            if (ReferenceEquals(_latest, request)) _latest = null;
            request.Stop.Dispose(); // Dispose does not cancel a successfully opened owner's lifetime.
        }
    }

    /// <summary>Invalidate the captured queued open before navigation can invoke owner callbacks.</summary>
    internal Func<Task> CapturePendingOpeningClose(out bool isLive)
    {
        _checkAccess();
        var request = _latest;
        isLive = request?.IsLive == true;
        _latest = null;
        return request is null ? () => Task.CompletedTask : () => ClosePendingOpeningAsync(request);
    }

    private async Task ClosePendingOpeningAsync(Request request)
    {
        var cancellation = request.Done.Task.IsCompleted ? Task.CompletedTask : Invoke(() => request.Stop.CancelAsync());
        var close = request.Owner is { } owner ? _owners.CloseAsync(owner) : Task.CompletedTask;
        await Task.Yield();
#pragma warning disable VSTHRD003 // Cancel/close have already started; only this captured opening is joined, never a successor.
        try { await cancellation; } catch { WriteCancellationFailure(); }
        try { await request.Done.Task; } catch { /* The actual owner close below decides release. */ }
        if (request.Worker is not null) await request.Worker;
        await close;
#pragma warning restore VSTHRD003
        if (request.Owner is { } lateOwner) await _owners.CloseAsync(lateOwner);
    }

    private async Task<Admission> AcquireAsync(Request request)
    {
        await _gate.WaitAsync(request.Stop.Token);
        var permit = new Lease(_gate);
        return new Admission(permit, (owner, close, released) =>
        {
            CheckRequest(request);
            if (request.Owner is not null) throw Failure();
            _owners.Retain(owner, request.IsLive, permit, close, released);
            request.Owner = owner;
        });
    }

    private void CheckRequest(Request request)
    {
        _checkAccess();
        request.Stop.Token.ThrowIfCancellationRequested();
        if (IsBlocked) throw Failure();
        if (!ReferenceEquals(_latest, request)) throw new OperationCanceledException("Playback opening was superseded.");
    }

    internal Task ShutdownAsync()
    {
        _checkAccess();
        if (_shutdown is not null) return _shutdown;
        _stopping = true;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _shutdown = done.Task;
        var requests = _requests.ToArray();
        var navigation = _navigationLiveClose;
        var cancellations = requests.Select(request => Invoke(() => request.Stop.CancelAsync())).ToArray();
        var ownerClose = Task.WhenAll(Invoke(() => LiveCloseDiagnostics.Invoke("window", _owners.CloseAllAsync)), navigation);
        var embeddedClose = Invoke(_closeEmbedded);
        _shutdownWorker = ShutdownCoreAsync(done, requests, cancellations, ownerClose, embeddedClose);
        return done.Task;
    }

    private async Task ShutdownCoreAsync(TaskCompletionSource done, Request[] requests, Task[] cancellations,
        Task ownerClose, Task embeddedClose)
    {
        await Task.Yield();
        var failed = _releaseUnconfirmed;
        try { await Task.WhenAll(cancellations); }
        catch { WriteCancellationFailure(); }
        try
        {
            Task ownerCloseTask = ownerClose;
            Task embeddedCloseTask = embeddedClose;
            await Task.WhenAll(ownerCloseTask, embeddedCloseTask);
        }
        catch { failed = true; }
        foreach (var request in requests)
        {
            try {
                // T1-09: Shutdown cancels/closes before joining each prepublished Request.Done; failed requests still have real worker ownership.
                #pragma warning disable VSTHRD003
                await request.Done.Task;
                #pragma warning restore VSTHRD003
            } catch { }
            if (request.Worker is not null) try {
                // T1-10: Join the actual retained route worker after Done; a result or cancellation alone cannot release its owner.
                #pragma warning disable VSTHRD003
                await request.Worker;
                #pragma warning restore VSTHRD003
            } catch { /* Completed worker failure is not proof of unreleased playback. Close actual owners below. */ }
        }
        // The old task may have acquired an owner just before cancellation. Drain that actual owner too.
        // Earlier failure cannot skip this independent drain, nor is it cleared by a later no-op.
        try { await Task.WhenAll(Invoke(_owners.CloseAllAsync), Invoke(_closeEmbedded)); }
        catch { failed = true; }
        if (failed || _releaseUnconfirmed || _owners.IsBlocked || _owners.HasWork)
        {
            _releaseUnconfirmed = true;
            done.TrySetException(Failure());
        }
        else done.TrySetResult();
    }

    internal static Task Invoke(Func<Task> action)
    {
        try { return action(); }
        catch (Exception error) { return Task.FromException(LiveCloseDiagnostics.Wrap(Failure().Message, error)); }
    }

    private void WriteCancellationFailure()
        => SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<LiveRouteCoordinator>>(),
            logger => logger.LogWarning("Live route cancellation callback failed; resource drain continues."));

    private static InvalidOperationException Failure()
        => new("播放切换或关闭未完成，原播放所有者仍被保留。");

    private sealed class Request(CancellationToken token, bool isLive)
    {
        internal readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool IsLive => isLive;
        internal object? Owner;
        internal Task Cancellation = Task.CompletedTask;
        internal Task OwnerClose = Task.CompletedTask;
        internal Task EmbeddedClose = Task.CompletedTask;
        internal Task? Worker;
    }

    internal sealed class Admission(IDisposable permit, Action<object, Func<Task>, Action> retain) : IDisposable
    {
        private bool _retained;
        private bool _disposed;

        internal void Retain(object owner, Func<Task> close, Action released)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_retained) throw Failure();
            retain(owner, close, released);
            _retained = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_retained) permit.Dispose(); // No native owner was created; otherwise the registry owns this permit.
        }
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        // D1: Lease borrows the shared semaphore and owns one permit; Interlocked.Exchange plus Release runs once, so Dispose would destroy shared admission.
        #pragma warning disable CA2213
        private SemaphoreSlim? _gate = gate;
        #pragma warning restore CA2213
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}

/// <summary>Retains a composite player/room owner and its permit until its real close succeeds.</summary>
internal sealed class SharedPlaybackOwnerRegistry(Action checkAccess, Action reportFailure)
{
    private readonly Dictionary<object, Owner> _owners = new(ReferenceEqualityComparer.Instance);
    private readonly Action _reportFailure = reportFailure;
    private bool _releaseFailed;

    internal bool HasWork => _owners.Count != 0 || _releaseFailed;
    internal bool IsBlocked => _releaseFailed;
    internal IEnumerable<object> LiveOwners => _owners.Values.Where(owner => owner.IsLive).Select(owner => owner.Key);
    internal bool IsLiveOwner(object key) => _owners.TryGetValue(key, out var owner) && owner.IsLive;

    internal void Retain(object key, bool isLive, IDisposable permit, Func<Task> close, Action released)
    {
        checkAccess();
        if (IsBlocked || _owners.ContainsKey(key)) throw Failure();
        _owners.Add(key, new Owner(this, key, isLive, permit, close, released));
    }

    internal Task CloseAsync(object key)
    {
        checkAccess();
        return _owners.TryGetValue(key, out var owner) ? owner.CloseAsync() : Task.CompletedTask;
    }

    internal bool RequestRelease(object key)
    {
        checkAccess();
        if (!_owners.TryGetValue(key, out var owner)) return false;
        // Player.ReleasePlayer is called inside the real close. This observer never joins it
        // from the player's owned-work set, so final registry release cannot await itself.
        _ = ObserveReleaseAsync(owner.CloseAsync());
        return true;
    }

    internal Task CloseAllAsync()
    {
        checkAccess();
        var owners = _owners.Values.ToArray();
        var closes = owners.Select(owner => owner.CloseAsync()).ToArray();
        return DrainAsync(owners, closes);
    }

    private static async Task ObserveReleaseAsync(Task close)
    {
#pragma warning disable VSTHRD003 // Observes an independently published owner close without owning player work or a route permit.
        try { await close; }
#pragma warning restore VSTHRD003
        catch { /* The registry retains the failed owner and reports through its close worker. */ }
    }

    private static async Task DrainAsync(Owner[] owners, Task[] closes)
    {
        Exception? failure = null;
        try { await Task.WhenAll(closes); }
        catch (Exception ex) { failure = ex; }
        foreach (var owner in owners)
        {
#pragma warning disable VSTHRD003 // Every worker was started before this independent shutdown/replacement join.
            if (owner.Worker is not null) await owner.Worker;
#pragma warning restore VSTHRD003
        }
        if (failure is not null) throw LiveCloseDiagnostics.Wrap(Failure().Message, failure);
    }

    private static InvalidOperationException Failure()
        => new("Player close is incomplete. Shared ownership is retained.");

    private sealed class Owner(SharedPlaybackOwnerRegistry registry, object key, bool isLive,
        IDisposable permit, Func<Task> close, Action released)
    {
        private Task? _close;
        internal object Key => key;
        internal bool IsLive => isLive;
        internal Task? Worker { get; private set; }

        internal Task CloseAsync()
        {
            if (_close is not null) return _close;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _close = done.Task; // Publish before the player close or a release callback can reenter.
            var actual = LiveRouteCoordinator.Invoke(close);
            Worker = CompleteCloseAsync(done, actual);
            return done.Task;
        }

        private async Task CompleteCloseAsync(TaskCompletionSource done, Task actual)
        {
            await Task.Yield();
            try
            {
#pragma warning disable VSTHRD003 // Real native/room/player close owns its own drain; it never waits for this registry continuation.
                await actual;
#pragma warning restore VSTHRD003
                released();
                permit.Dispose(); // Only confirmed close and completed app bookkeeping release admission.
                registry._owners.Remove(key);
                done.TrySetResult();
            }
            catch (Exception error)
            {
                registry._releaseFailed = true;
                try { registry._reportFailure(); } catch { /* Feedback cannot release an owner. */ }
                done.TrySetException(LiveCloseDiagnostics.Wrap(Failure().Message, error)); // Cached failure: a later close no-op cannot clear ownership.
            }
        }
    }
}
