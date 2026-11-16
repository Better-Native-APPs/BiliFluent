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
    private readonly Func<Task> _closeLive;
    private readonly Func<Task> _closeEmbedded;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<Request> _requests = [];
    private Request? _latest;
    private Request? _admittingLive;
    private bool _stopping;
    private bool _releaseUnconfirmed;
    private Task _navigationLiveClose = Task.CompletedTask;
    private Task? _shutdown;
    private Task? _shutdownWorker;

    internal LiveRouteCoordinator(Action checkAccess, Func<Task> closeLive, Func<Task> closeEmbedded)
    {
        _checkAccess = checkAccess;
        _closeLive = closeLive;
        _closeEmbedded = closeEmbedded;
    }

    internal bool HasWork => _requests.Count != 0 || _releaseUnconfirmed
        || !_navigationLiveClose.IsCompleted || _shutdown is { IsCompleted: false };
    internal bool IsBlocked => _stopping || _releaseUnconfirmed;

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
        catch
        {
            _releaseUnconfirmed = true;
            SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<LiveRouteCoordinator>>(),
                logger => logger.LogError("Live navigation close is incomplete; ownership is retained and replacement is blocked."));
        }
    }

    internal Task OpenLiveAsync(Func<CancellationToken, Task> open, CancellationToken token)
        => Queue(open, isLive: true, token);

    internal Task OpenVideoAsync(Func<CancellationToken, Task> open, CancellationToken token)
        => Queue(open, isLive: false, token);

    private Task Queue(Func<CancellationToken, Task> open, bool isLive, CancellationToken token)
    {
        _checkAccess();
        token.ThrowIfCancellationRequested();
        if (IsBlocked) throw Failure();
        // One transition plus one replacement; repeated clicks cannot build an unbounded queue.
        if (_requests.Count >= 2) throw new InvalidOperationException("播放切换尚未完成，请稍后再试。");
        var previous = _latest;
        var request = new Request(token);
        _latest = request;
        _requests.Add(request); // Publish before cancellation callbacks or owner methods can reenter.
        request.Cancellation = previous is not null && !previous.Done.Task.IsCompleted
            ? Invoke(() => previous.Stop.CancelAsync()) : Task.CompletedTask;
        // Start cancellation immediately, then join both real owner close and any navigation drain.
        request.LiveClose = Task.WhenAll(Invoke(_closeLive), _navigationLiveClose);
        request.EmbeddedClose = Invoke(_closeEmbedded); // Cancels/drains the actual pending VOD owner.
        request.Worker = RunAsync(request, previous?.Done.Task, open, isLive);
        // T1-06: Request.Done is published before route callbacks; return the identical owner completion without a synchronous UI wait.
        #pragma warning disable VSTHRD003
        return request.Done.Task;
        #pragma warning restore VSTHRD003
    }

    private async Task RunAsync(Request request, Task? previous, Func<CancellationToken, Task> open, bool isLive)
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
                await Task.WhenAll(request.LiveClose, request.EmbeddedClose);
                #pragma warning restore VSTHRD003
            }
            catch { failed = true; _releaseUnconfirmed = true; }
            // Previous source/open failure does not prove release failure. Its real close above does.
            if (previous is not null) try {
                // T1-08: This is the captured predecessor request, not this runner; its independently owned completion must drain before replacement.
                #pragma warning disable VSTHRD003
                await previous;
                #pragma warning restore VSTHRD003
            } catch { }
            if (failed || _releaseUnconfirmed) throw Failure();
            CheckRequest(request);
            if (isLive)
            {
                _admittingLive = request;
                try { await open(request.Stop.Token); }
                finally { if (ReferenceEquals(_admittingLive, request)) _admittingLive = null; }
            }
            else
            {
                using var lease = await AcquireAsync(request.Stop.Token);
                CheckRequest(request);
                await open(request.Stop.Token);
            }
            CheckRequest(request);
            request.Done.TrySetResult();
        }
        catch (OperationCanceledException) when (request.Stop.IsCancellationRequested)
        {
            request.Done.TrySetCanceled(request.Stop.Token);
        }
        catch
        {
            if (request.Stop.IsCancellationRequested && !_releaseUnconfirmed)
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

    /// <summary>Called only by the real launcher during its published Live transition.</summary>
    internal async Task<IDisposable> EnterLiveAsync(CancellationToken token)
    {
        _checkAccess();
        var request = _admittingLive ?? throw Failure();
        CheckRequest(request);
        var lease = await AcquireAsync(token); // Wait itself is cancellable even while another route holds it.
        try
        {
            CheckRequest(request);
            // A real owner API, not a snapshot or a no-op admission token.
            await _closeEmbedded();
            CheckRequest(request);
            token.ThrowIfCancellationRequested();
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private async Task<IDisposable> AcquireAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        return new Lease(_gate);
    }

    private void CheckRequest(Request request)
    {
        _checkAccess();
        request.Stop.Token.ThrowIfCancellationRequested();
        if (IsBlocked || !ReferenceEquals(_latest, request)) throw Failure();
    }

    internal Task ShutdownAsync()
    {
        _checkAccess();
        if (_shutdown is not null) return _shutdown;
        _stopping = true;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _shutdown = done.Task;
        var requests = _requests.ToArray();
        var cancellations = requests.Select(request => Invoke(() => request.Stop.CancelAsync())).ToArray();
        var liveClose = Task.WhenAll(Invoke(_closeLive), _navigationLiveClose);
        var embeddedClose = Invoke(_closeEmbedded);
        _shutdownWorker = ShutdownCoreAsync(done, requests, cancellations, liveClose, embeddedClose);
        return done.Task;
    }

    private async Task ShutdownCoreAsync(TaskCompletionSource done, Request[] requests, Task[] cancellations,
        Task liveClose, Task embeddedClose)
    {
        await Task.Yield();
        var failed = _releaseUnconfirmed;
        try { await Task.WhenAll(cancellations); }
        catch { WriteCancellationFailure(); }
        try
        {
            Task liveCloseTask = liveClose;
            Task embeddedCloseTask = embeddedClose;
            await Task.WhenAll(liveCloseTask, embeddedCloseTask);
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
        try { await Task.WhenAll(Invoke(_closeLive), Invoke(_closeEmbedded)); }
        catch { failed = true; }
        if (failed || _releaseUnconfirmed)
        {
            _releaseUnconfirmed = true;
            done.TrySetException(Failure());
        }
        else done.TrySetResult();
    }

    internal static Task Invoke(Func<Task> action)
    {
        try { return action(); }
        catch { return Task.FromException(Failure()); }
    }

    private void WriteCancellationFailure()
        => SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<LiveRouteCoordinator>>(),
            logger => logger.LogWarning("Live route cancellation callback failed; resource drain continues."));

    private static InvalidOperationException Failure()
        => new("播放切换或关闭未完成，原播放所有者仍被保留。");

    private sealed class Request(CancellationToken token)
    {
        internal readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Cancellation = Task.CompletedTask;
        internal Task LiveClose = Task.CompletedTask;
        internal Task EmbeddedClose = Task.CompletedTask;
        internal Task? Worker;
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
        if (failure is not null) throw Failure();
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
                registry._owners.Remove(key);
                permit.Dispose(); // Only confirmed close and completed app bookkeeping release admission.
                done.TrySetResult();
            }
            catch
            {
                registry._releaseFailed = true;
                try { registry._reportFailure(); } catch { /* Feedback cannot release an owner. */ }
                done.TrySetException(Failure()); // Cached failure: a later close no-op cannot clear ownership.
            }
        }
    }
}
