#nullable enable

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using BiliCopilot.UI.Live.Application;
using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Models.Media;
using Richasy.BiliKernel.Models.User;

namespace BiliCopilot.UI.Live.Playback;

/// <summary>
/// UI-dispatcher admission and serialized media operations, delegating room/chat ownership.
/// A session never migrates accounts or rooms. Refresh replaces only its own room generation.
/// Public commands other than CloseAsync must be called on the supplied dispatcher.
/// </summary>
public sealed class LiveSession : ILiveSession
{
    private readonly ILivePlaybackBackend _backend;
    private readonly Func<LivePlaybackRequest, CancellationToken, Task<LiveSourceResult>> _resolve;
    // CompleteCloseAsync waits the room's actual close before confirming either owner's release.
    #pragma warning disable CA2213
    private readonly LiveRoomSession _room;
    #pragma warning restore CA2213
    private readonly Func<LiveAccountStamp> _account;
    private readonly DispatcherQueue _dispatcher;
    // D5: CompleteCloseAsync drains cancellation/media/room close/owner disposal/account unregister before disposing this shutdown CTS.
    #pragma warning disable CA2213
    private readonly CancellationTokenSource _shutdown = new();
    #pragma warning restore CA2213
    private readonly object _closeSync = new();
    private readonly object _inboxSync = new();
    private (Scope Scope, LiveBackendStatus Status)? _backendStatus;
    private bool _inboxPosted;
    private Task _tail = Task.CompletedTask;
    private Task? _refresh;
    private Task? _close;
    private Task _cancelShutdown = Task.CompletedTask;
    // D6: Replacement retains the prior scope; StopScopeCoreAsync/terminal close disposes it after actual work, and Scope.Dispose releases its CTS.
    #pragma warning disable CA2213
    private Scope? _scope;
    #pragma warning restore CA2213
    private LiveInformation? _live;
    private LiveAccountStamp? _stamp;
    private CancellationTokenRegistration _accountRegistration;
    private long _generation;
    private PlayerFormatInformation? _preferredQuality;
    private bool _controlPending;
    private bool _releaseBlocked;

    public LiveSession(ILivePlaybackBackend backend, LiveMediaSourceService sources,
        ILiveChatClient chat, Func<LiveAccountStamp> account, DispatcherQueue dispatcher)
        : this(backend, BindSources(sources), chat, account, dispatcher) { }

    // Narrow fake-resolver seam; production binds the live-only source service.
    internal LiveSession(ILivePlaybackBackend backend,
        Func<LivePlaybackRequest, CancellationToken, Task<LiveSourceResult>> resolve,
        ILiveChatClient chat, Func<LiveAccountStamp> account, DispatcherQueue dispatcher)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _room = new LiveRoomSession(chat, account, () => _dispatcher.HasThreadAccess,
            action => _dispatcher.TryEnqueue(() => action()), RecordRoomFailure);
        _room.PropertyChanged += OnRoomPropertyChanged;
        _room.MessageReceived += OnRoomMessageReceived;
        SelectQualityCommand = new AsyncRelayCommand<PlayerFormatInformation>(SelectQualityFromCommandAsync,
            quality => CanChangeQuality && quality is not null && Qualities.Contains(quality));
    }

    private static Func<LivePlaybackRequest, CancellationToken, Task<LiveSourceResult>> BindSources(LiveMediaSourceService sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return sources.ResolveAsync;
    }

    public LiveBinding? Binding => _room.Binding;
    public string Title => _room.Title;
    public string AnchorName => _room.AnchorName;
    public Uri? AnchorAvatar => _room.AnchorAvatar;
    public UserProfile? Anchor => _room.Anchor;
    public bool? IsFollowing => _room.IsFollowing;
    public long? AudienceCount => _room.AudienceCount;
    public string AudienceLabel => _room.AudienceLabel;
    public IReadOnlyList<PlayerFormatInformation> Qualities { get; private set; } = Array.Empty<PlayerFormatInformation>();
    public PlayerFormatInformation? SelectedQuality { get; private set; }
    public bool CanChangeQuality => !IsClosing && !IsBusy && !_controlPending && !_releaseBlocked
        && Binding is not null && _scope?.Token.IsCancellationRequested == false && Qualities.Count > 1;
    public IAsyncRelayCommand<PlayerFormatInformation> SelectQualityCommand { get; }
    public LivePlaybackState PlaybackState { get; private set; }
    public LiveChatState ChatState => _room.ChatState;
    public string? PlaybackError { get; private set; }
    public string? ChatError => _room.ChatError;
    public double Volume { get; private set; } = 1;
    public bool IsBusy { get; private set; }
    public bool CanSend => !IsClosing && !IsBusy && _room.CanSend;
    public ReadOnlyObservableCollection<LiveChatMessage> Messages => _room.Messages;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<LiveChatMessage>? MessageReceived;
    public bool IsClosing => Volatile.Read(ref _close) is not null;
    public bool ReleaseConfirmed { get; private set; }

    public Task OpenAsync(LiveInformation live, CancellationToken cancellationToken)
    {
        CheckAdmission(cancellationToken);
        ArgumentNullException.ThrowIfNull(live);
        if (_live is not null) throw new InvalidOperationException("A Live session can open only one room. Use Refresh for fresh media.");
        _stamp = _account();
        _stamp.Value.Lifetime.ThrowIfCancellationRequested();
        _live = live;
        // Cancellation invalidates admission even before the dispatcher can deliver Closing.
        _accountRegistration = _stamp.Value.Lifetime.Register(() => _ = Observe(CloseAsync()));
        return BeginOpen(cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        CheckAdmission(cancellationToken);
        if (_live is null) throw new InvalidOperationException("Live has no room to refresh.");
        if (_refresh is { IsCompleted: false })
            // T4-01: Return the retained actual refresh operation; no replacement is queued and no synchronous UI wait is introduced.
            #pragma warning disable VSTHRD003
            return _refresh; // One pending replacement, no refresh queue.
            #pragma warning restore VSTHRD003
        return BeginOpen(cancellationToken, isRefresh: true);
    }

    public Task SelectQualityAsync(PlayerFormatInformation quality, CancellationToken cancellationToken)
    {
        CheckAdmission(cancellationToken);
        ArgumentNullException.ThrowIfNull(quality);
        if (!CanChangeQuality) return Task.CompletedTask;
        if (!Qualities.Contains(quality))
            throw new ArgumentException("Live quality was not advertised by this room.", nameof(quality));
        if (Equals(SelectedQuality, quality)) return Task.CompletedTask;
        return BeginOpen(cancellationToken, isRefresh: true, quality: quality);
    }

    private async Task SelectQualityFromCommandAsync(PlayerFormatInformation? quality)
    {
        if (quality is null || !CanChangeQuality || !Qualities.Contains(quality)) return;
        try { await SelectQualityAsync(quality, CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { } // BeginOpen already publishes the retryable playback error.
    }

    private Task BeginOpen(CancellationToken token, bool isRefresh = false, PlayerFormatInformation? quality = null)
    {
        RequireSameAccount();
        var requestedQuality = quality ?? _preferredQuality;
        var previous = _scope;
        previous?.Invalidate();
        var scope = new Scope(++_generation, _stamp!.Value, token, _shutdown.Token);
        try { scope.Room = _room.PrepareRoom(_live!, _stamp.Value, scope.Token); }
        catch { scope.Dispose(); throw; }
        _scope = scope;
        IsBusy = true;
        PlaybackState = LivePlaybackState.Opening;
        PlaybackError = null;
        var operation = Enqueue(async () =>
        {
            // This operation sits AFTER the actual previous resolver/open/control task, not a
            // cancelled waiter. StopScope also joins the independent connect and manual send.
            var failureStage = FailureStage.LivePreviousStop;
            try
            {
                if (previous is not null) await StopScopeAsync(previous);
                EnsureCurrent(scope);
                failureStage = FailureStage.LiveResolve;
                var request = new LivePlaybackRequest(_live!, requestedQuality?.Quality, requestedQuality?.Eotf);
                var result = await _resolve(request, scope.Token);
                EnsureCurrent(scope); // SDK parsing can complete after cancellation.
                failureStage = FailureStage.LiveValidateSource;
                var room = CanonicalRoom(result);
                if (scope.PreviousRoom is not null && room != scope.PreviousRoom)
                    throw new InvalidOperationException("Live room identity changed during refresh.");
                failureStage = FailureStage.LivePublishBinding;
                Subscribe(scope);
                _room.BindRoom(scope.Room, room, result.LivePage.Information);
                failureStage = FailureStage.LiveSetVolume;
                await _backend.SetVolumeAsync(Volume, scope.Token);
                EnsureCurrent(scope);
                failureStage = FailureStage.LiveBackendOpen;
                await _backend.OpenAsync(result.Media, scope.Token);
                EnsureCurrent(scope);
                // Global descriptions must be checked against this room's matching SDR/PQ/HLG lines.
                Qualities = Array.AsReadOnly(result.Qualities.Where(format => result.Media.Live.Lines.Any(line => line.Eotf == format.Eotf
                    && (line.Quality == format.Quality || line.AcceptQualities.Contains(format.Quality))))
                    .Distinct().ToArray());
                SelectedQuality = Qualities.FirstOrDefault(format => Equals(format, result.SelectedQuality));
                _preferredQuality = SelectedQuality;
                failureStage = FailureStage.LivePublishPlaying;
                PlaybackState = LivePlaybackState.Playing;
                _ = _room.ConnectAsync(scope.Room); // The room owns the actual task until StopScope/Close.
            }
            catch (OperationCanceledException ex)
            {
                SafeFailureContext.TryWrite(failureStage, ex, () => this.Get<ILogger<LiveSession>>());
                if (OwnsPublication(scope)) { PlaybackState = LivePlaybackState.Idle; _room.MarkOpenCancelled(scope.Room); }
                await CleanupFailedOpenAsync(scope);
                throw new OperationCanceledException("Live open was cancelled.");
            }
            catch (Exception ex)
            {
                SafeFailureContext.TryWrite(failureStage, ex, () => this.Get<ILogger<LiveSession>>());
                if (OwnsPublication(scope)) { PlaybackState = LivePlaybackState.Failed; PlaybackError = "Live playback is unavailable. Refresh to resolve a new stream."; }
                await CleanupFailedOpenAsync(scope);
                throw new InvalidOperationException("Live open failed.");
            }
            finally
            {
                if (OwnsPublication(scope)) { IsBusy = false; _room.ClearStoppedBinding(scope.Room); Notify(); }
            }
        }, previous?.Binding?.RoomId);
        if (isRefresh) _refresh = operation;
        // Publish the real media tail (and refresh coalescer) before any UI observer can reenter.
        ClearInbox();
        _room.PublishRoomReset(scope.Room);
        return operation;
    }

    private static long CanonicalRoom(LiveSourceResult result)
    {
        var media = result.Media;
        if (!TryRoom(result.LivePage?.Information.Identifier.Id, out var room)
            || !TryRoom(media.Live.Id, out var streamRoom) || streamRoom != room
            || !TryRoom(media.Request.Live.Identifier.Id, out var requestRoom) || requestRoom != room)
            throw new InvalidOperationException("Live canonical room or source scope is invalid.");
        return room;
    }

    private static bool TryRoom(string? text, out long room)
        => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out room) && room > 0;

    public Task PlayAsync(CancellationToken cancellationToken)
    {
        CheckAdmission(cancellationToken);
        RequireSameAccount();
        if (PlaybackState == LivePlaybackState.Playing) return Task.CompletedTask;
        // Resume at the live edge with fresh resolution, including Paused. The live backend
        // deliberately exposes no replay/seek command that could race EOF.
        return RefreshAsync(cancellationToken);
    }

    public Task PauseAsync(CancellationToken cancellationToken)
        => ControlAsync(async (scope, token) =>
        {
            if (PlaybackState is not (LivePlaybackState.Playing or LivePlaybackState.Opening)) return;
            await _backend.PauseAsync(token);
            EnsureCurrent(scope);
            PlaybackState = LivePlaybackState.Paused;
        }, cancellationToken);

    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(volume) || volume < 0 || volume > 1)
            throw new ArgumentOutOfRangeException(nameof(volume), "Live volume must be between zero and one.");
        return ControlAsync(async (scope, token) =>
        {
            await _backend.SetVolumeAsync(volume, token);
            EnsureCurrent(scope);
            Volume = volume;
        }, cancellationToken);
    }

    private Task ControlAsync(Func<Scope, CancellationToken, Task> command, CancellationToken token)
    {
        CheckAdmission(token);
        RequireSameAccount();
        var scope = _scope ?? throw new InvalidOperationException("Live has no open scope.");
        if (IsBusy || _controlPending) return Task.CompletedTask; // No unbounded slider/transport queue.
        _controlPending = true;
        return Enqueue(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(scope.Token, token);
            try { EnsureCurrent(scope); await command(scope, linked.Token); EnsureCurrent(scope); }
            catch (OperationCanceledException) { throw new OperationCanceledException("Live command was cancelled."); }
            catch
            {
                if (IsCurrent(scope)) PlaybackError = "Live playback command failed.";
                throw new InvalidOperationException("Live playback command failed.");
            }
            finally { _controlPending = false; if (IsCurrent(scope)) Notify(); }
        });
    }

    public Task<LiveSendResult> SendManuallyAsync(LiveBinding binding, string text, CancellationToken cancellationToken)
    {
        CheckUi();
        if (cancellationToken.IsCancellationRequested || IsClosing || !CanSend || _scope is not { } scope
            || !MatchesAccount() || scope.Binding != binding || Binding != binding || string.IsNullOrWhiteSpace(text))
            return Task.FromResult(new LiveSendResult(binding, LiveSendOutcome.NotSent, null, "Message was not sent."));
        return _room.SendManuallyAsync(binding, text, cancellationToken);
    }

    private async Task CleanupFailedOpenAsync(Scope scope)
    {
        // A failure may follow allocation. Keep the scope owned; a later refresh can only start
        // after this real cleanup. Any release uncertainty makes replacement permanently fail closed.
        try { await StopScopeAsync(scope); }
        catch { _releaseBlocked = true; throw new InvalidOperationException("Live cleanup is unconfirmed; replacement is blocked."); }
    }

    private Task StopScopeAsync(Scope scope) => scope.Stop ??= StopScopeCoreAsync(scope);

    private async Task StopScopeCoreAsync(Scope scope)
    {
        if (scope.Stopped) return;
        scope.Invalidate();
        Unsubscribe(scope);
        var failed = false;
        try {
            // T4-03: Scope invalidation starts cancellation before this join; callbacks request/observe close without blocking on this consumer.
            #pragma warning disable VSTHRD003
            await scope.Cancellation;
            #pragma warning restore VSTHRD003
        } catch { RecordFailure(LiveFailureCategory.CancellationCallback); }
        // The room joins actual connection/send tasks and disconnect before the backend is stopped.
        try { await _room.StopAsync(scope.Room); } catch { failed = true; }
        try { await _backend.StopAsync(CancellationToken.None); } catch { failed = true; RecordFailure(LiveFailureCategory.BackendRelease); }
        scope.Dispose();
        if (failed)
        {
            _releaseBlocked = true;
            throw new InvalidOperationException("Live cleanup is unconfirmed; replacement is blocked.");
        }
        scope.Stopped = true;
    }

    public Task CloseAsync()
    {
        lock (_closeSync)
        {
            if (_close is not null) return _close;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _close = completion.Task; // Admission closed before cancellation callbacks or awaits.
            _cancelShutdown = _shutdown.CancelAsync();
            if (!_dispatcher.TryEnqueue(() => _ = CompleteCloseAsync(completion)))
                completion.TrySetException(new InvalidOperationException("Live dispatcher is unavailable; owned resources are retained."));
            return _close;
        }
    }

    private async Task CompleteCloseAsync(TaskCompletionSource completion)
    {
        try
        {
            CheckUi();
            IsBusy = true;
            PlaybackState = LivePlaybackState.Closing;
            ClearInbox();
            _room.RequestClose();
            _scope?.Invalidate();
            try {
                // T4-06: Shutdown cancellation starts before close joins it; account callbacks never synchronously wait for this close consumer.
                #pragma warning disable VSTHRD003
                await _cancelShutdown;
                #pragma warning restore VSTHRD003
            } catch { RecordFailure(LiveFailureCategory.CancellationCallback); }
            // T4-07: The retained observer tail drains actual serial media work; producers do not await the close consumer.
            #pragma warning disable VSTHRD003
            await _tail; // Observer tail drains every actual media task, including late SDK results.
            #pragma warning restore VSTHRD003
            if (_scope is { } scope)
            {
                Unsubscribe(scope);
                try {
                    // T4-08: Terminal close joins already-started scope cancellation before owner disposal; callbacks do not block on close.
                    #pragma warning disable VSTHRD003
                    await scope.Cancellation;
                    #pragma warning restore VSTHRD003
                } catch { RecordFailure(LiveFailureCategory.CancellationCallback); }
            }
            var chatReleased = false;
            var backendReleased = false;
            try { await _room.CloseAsync(); chatReleased = _room.ReleaseConfirmed; } catch { RecordFailure(LiveFailureCategory.ChatRelease); }
            try { await _backend.DisposeAsync(); backendReleased = true; } catch { RecordFailure(LiveFailureCategory.BackendRelease); }
            // Its callback requests/observes CloseAsync; it never waits synchronously for Close.
            // Drain it even after an owner fails, and before disposing tokens or confirming release.
            try { await _accountRegistration.DisposeAsync(); }
            catch { RecordFailure(LiveFailureCategory.RegistrationRelease); }
            // Completed callback faults are not running work or evidence of native ownership.
            // Both actual disposals must succeed. Cached close is never a disposal retry.
            if (!chatReleased || !backendReleased)
                throw new InvalidOperationException("Live release is unconfirmed; the window and owners must remain retained.");
            try { _scope?.Dispose(); } catch { RecordFailure(LiveFailureCategory.RegistrationRelease); }
            try { _shutdown.Dispose(); } catch { RecordFailure(LiveFailureCategory.RegistrationRelease); }
            IsBusy = false;
            PlaybackState = LivePlaybackState.Closed;
            ReleaseConfirmed = true;
            Notify();
            completion.TrySetResult();
        }
        catch
        {
            PlaybackError = "Live close is incomplete. Replacement is blocked.";
            Notify();
            completion.TrySetException(new InvalidOperationException("Live close is incomplete. Owned resources are retained."));
        }
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private Task Enqueue(Func<Task> action, long? previousRoom = null)
    {
        if (previousRoom is not null && _scope is { } scope) scope.PreviousRoom = previousRoom;
        var predecessor = _tail;
        var actual = RunAsync();
        _tail = Observe(actual);
        return actual;
        async Task RunAsync()
        {
            await Task.Yield(); // Publish ownership/tail before any reentrant callbacks.
            // T4-11: The serial runner captured the prior tail before publishing itself; this predecessor is not its own observer.
            #pragma warning disable VSTHRD003
            await predecessor;
            #pragma warning restore VSTHRD003
            await action();
        }
    }

    private bool OwnsPublication(Scope scope) => !IsClosing && ReferenceEquals(_scope, scope)
        && scope.Generation == _generation && !scope.Account.Lifetime.IsCancellationRequested;
    private bool IsCurrent(Scope scope) => OwnsPublication(scope) && !scope.Invalidated && !scope.Token.IsCancellationRequested;
    private void EnsureCurrent(Scope scope)
    {
        CheckUi();
        scope.Token.ThrowIfCancellationRequested();
        if (!IsCurrent(scope)) throw new OperationCanceledException("Live scope was replaced.");
        RequireSameAccount();
        if (_releaseBlocked) throw new InvalidOperationException("Live replacement is blocked by unconfirmed cleanup.");
    }
    private bool MatchesAccount()
    {
        if (_stamp is not { } captured || captured.Lifetime.IsCancellationRequested) return false;
        var current = _account();
        return current.Generation == captured.Generation && current.AccountId == captured.AccountId
            && current.Lifetime == captured.Lifetime && !current.Lifetime.IsCancellationRequested;
    }
    private void RequireSameAccount()
    {
        if (MatchesAccount()) return;
        _ = Observe(CloseAsync());
        throw new OperationCanceledException("Live account scope ended.");
    }
    private void CheckUi()
    {
        if (!_dispatcher.HasThreadAccess) throw new InvalidOperationException("Live command requires its UI dispatcher.");
    }
    private void CheckAdmission(CancellationToken token)
    {
        CheckUi();
        token.ThrowIfCancellationRequested();
        if (IsClosing || _releaseBlocked) throw new InvalidOperationException("Live admission is closed.");
    }

    private void Subscribe(Scope scope)
    {
        scope.BackendHandler = (_, status) =>
        {
            lock (_inboxSync) { _backendStatus = (scope, status); PostInboxLocked(); }
        };
        _backend.StatusChanged += scope.BackendHandler;
    }
    private void Unsubscribe(Scope scope)
    {
        // Observer detachment cannot skip independent owner drains/disposals.
        try { _backend.StatusChanged -= scope.BackendHandler; } catch { RecordFailure(LiveFailureCategory.StatusSubscriber); }
    }
    private void PostInboxLocked()
    {
        if (_inboxPosted || IsClosing) return;
        _inboxPosted = true;
        if (!_dispatcher.TryEnqueue(DrainInbox)) { _inboxPosted = false; _backendStatus = null; }
    }
    private void ClearInbox()
    {
        lock (_inboxSync) { _backendStatus = null; }
    }
    private void DrainInbox()
    {
        (Scope Scope, LiveBackendStatus Status)? backend;
        lock (_inboxSync)
        {
            backend = _backendStatus;
            _backendStatus = null; _inboxPosted = false;
        }
        if (IsClosing || !MatchesAccount()) return;
        if (backend is { } b && IsCurrent(b.Scope))
        {
            PlaybackState = b.Status.State switch
            {
                LiveBackendState.Playing => LivePlaybackState.Playing,
                LiveBackendState.Paused => LivePlaybackState.Paused,
                LiveBackendState.Ended => LivePlaybackState.Ended,
                LiveBackendState.Failed => LivePlaybackState.Failed,
                _ => LivePlaybackState.Opening,
            };
            if (b.Status.State == LiveBackendState.Failed) PlaybackError = "Live playback failed. Refresh to resolve a new stream.";
            if (double.IsFinite(b.Status.Volume)) Volume = Math.Clamp(b.Status.Volume, 0, 1);
        }
        Notify();
    }
    private void OnRoomPropertyChanged(object? sender, PropertyChangedEventArgs args) => Notify();
    private void OnRoomMessageReceived(object? sender, LiveChatMessage message)
    {
        if (_scope is { } scope && IsCurrent(scope) && Binding == message.Binding && MatchesAccount())
            PublishMessage(scope, message);
    }
    private void PublishMessage(Scope scope, LiveChatMessage message)
    {
        if (MessageReceived is not { } handlers) return;
        foreach (EventHandler<LiveChatMessage> handler in handlers.GetInvocationList())
        {
            if (!IsCurrent(scope) || Binding != message.Binding) return;
            try { handler(this, message); } catch { RecordFailure(LiveFailureCategory.MessageSubscriber); }
        }
    }
    private void Notify()
    {
        CheckUi();
        try { SelectQualityCommand.NotifyCanExecuteChanged(); } catch { RecordFailure(LiveFailureCategory.PropertySubscriber); }
        if (PropertyChanged is not { } handlers) return;
        foreach (PropertyChangedEventHandler handler in handlers.GetInvocationList())
            try { handler(this, new PropertyChangedEventArgs(string.Empty)); } catch { RecordFailure(LiveFailureCategory.PropertySubscriber); }
    }
    private async Task Observe(Task task)
    {
        try {
            // T4-12: This observer joins already-issued owned work, not the completion it produces; errors remain observed asynchronously.
            #pragma warning disable VSTHRD003
            await task;
            #pragma warning restore VSTHRD003
        }
        catch (OperationCanceledException) { return; }
        catch { RecordFailure(LiveFailureCategory.OwnedTaskFault); }
    }

    private void RecordFailure(LiveFailureCategory category)
        => SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<LiveSession>>(),
             logger => logger.LogWarning("Live session operation failed: {Category}", category));

    private void RecordRoomFailure(string category)
        => SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<LiveSession>>(),
            logger => logger.LogWarning("Live room operation failed: {Category}", category));

    private sealed class Scope : IDisposable
    {
        private readonly CancellationTokenSource _lifetime;
        private bool _disposed;
        public Scope(long generation, LiveAccountStamp account, CancellationToken request, CancellationToken shutdown)
        {
            Generation = generation; Account = account;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(request, account.Lifetime, shutdown);
            Token = _lifetime.Token;
        }
        public long Generation { get; }
        public LiveAccountStamp Account { get; }
        public CancellationToken Token { get; }
        public LiveRoomScope Room { get; set; } = null!;
        public LiveBinding? Binding => Room.Binding;
        public long? PreviousRoom { get; set; }
        public bool Invalidated { get; private set; }
        public bool Stopped { get; set; }
        public Task Cancellation { get; private set; } = Task.CompletedTask;
        public Task? Stop { get; set; }
        public EventHandler<LiveBackendStatus>? BackendHandler { get; set; }
        public void Invalidate()
        {
            if (Invalidated) return;
            Invalidated = true;
            if (!_disposed) Cancellation = _lifetime.CancelAsync();
        }
        public void Dispose() { if (!_disposed) { _disposed = true; _lifetime.Dispose(); } }
    }
}
