#nullable enable

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Richasy.BiliKernel.Models.Media;
using Richasy.BiliKernel.Models.User;

namespace BiliCopilot.UI.Live.Application;

/// <summary>
/// Owns room binding, bounded chat publication and actual connect/send/release tasks.
/// Commands run on the injected dispatcher and preserve its synchronization context;
/// CloseAsync may be called from any thread. The playback owner must close all playback
/// on account invalidation, and join its media work before disposing this room session.
/// </summary>
public sealed class LiveRoomSession : ILiveRoomSession
{
    // CompleteCloseAsync releases the client only after all owned operations have really finished.
    #pragma warning disable CA2213
    private readonly ILiveChatClient _chat;
    private readonly CancellationTokenSource _shutdown = new();
    #pragma warning restore CA2213
    private readonly Func<LiveAccountStamp> _account;
    private readonly Func<bool> _hasThreadAccess;
    private readonly Func<Action, bool> _tryEnqueue;
    private readonly Action<string>? _reportFailure;
    private readonly Guid _id = Guid.NewGuid();
    private readonly ObservableCollection<LiveChatMessage> _messages = new();
    private readonly List<LiveRoomScope> _ownedScopes = new();
    private readonly object _closeSync = new();
    private readonly object _inboxSync = new();
    private readonly Queue<(LiveRoomScope Scope, LiveChatMessage Message)> _inbox = new();
    private (LiveRoomScope Scope, LiveChatStatus Status)? _chatStatus;
    private (LiveRoomScope Scope, LiveAudienceUpdate Update)? _audience;
    private LiveAudienceMetric? _audienceMetric;
    private bool _inboxPosted;
    private Task? _close;
    private Task _cancelShutdown = Task.CompletedTask;
    private LiveRoomScope? _scope;
    private LiveInformation? _live;
    private long _generation;
    private int _closeRequested;
    private bool _closePublished;
    private bool _releaseBlocked;
    private long _roomStatsAt = Stopwatch.GetTimestamp();
    private long _inboxIn;
    private long _inboxDrop;
    private long _published;
    private long _dispatchFail;

    /// <summary>Transfers chat ownership to a room session using the supplied dispatcher delegates.</summary>
    /// <param name="chat">The exclusively owned chat client, released by CloseAsync.</param>
    /// <param name="account">Returns the current account generation on the owning dispatcher.</param>
    /// <param name="hasThreadAccess">Reports access to the owning dispatcher.</param>
    /// <param name="tryEnqueue">Queues, never invokes inline, on the owning synchronization context.</param>
    /// <param name="reportFailure">Optional diagnostic sink receiving only category names, never room data or exceptions.</param>
    public LiveRoomSession(ILiveChatClient chat, Func<LiveAccountStamp> account,
        Func<bool> hasThreadAccess, Func<Action, bool> tryEnqueue, Action<string>? reportFailure = null)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _hasThreadAccess = hasThreadAccess ?? throw new ArgumentNullException(nameof(hasThreadAccess));
        _tryEnqueue = tryEnqueue ?? throw new ArgumentNullException(nameof(tryEnqueue));
        _reportFailure = reportFailure;
        Messages = new(_messages);
    }

    public LiveBinding? Binding { get; private set; }
    public LiveRoomScope? CurrentScope => _scope;
    public string Title { get; private set; } = string.Empty;
    public string AnchorName { get; private set; } = string.Empty;
    public Uri? AnchorAvatar { get; private set; }
    public UserProfile? Anchor { get; private set; }
    public bool? IsFollowing { get; private set; }
    public long? AudienceCount { get; private set; }
    public string AudienceLabel => _audienceMetric == LiveAudienceMetric.OnlineRank ? "高能在线" : "看过";
    public LiveChatState ChatState { get; private set; }
    public string? ChatError { get; private set; }
    public bool CanSend => !IsClosing && _scope is { Sending: false } scope && Binding is not null
        && IsCurrent(scope) && ChatState == LiveChatState.Connected && !string.IsNullOrWhiteSpace(scope.Account.AccountId);
    public bool IsClosing => Volatile.Read(ref _closeRequested) != 0;
    public bool ReleaseConfirmed { get; private set; }
    public ReadOnlyObservableCollection<LiveChatMessage> Messages { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<LiveChatMessage>? MessageReceived;

    /// <summary>
    /// Invalidates the old generation without notifying observers. Publish the owner's actual
    /// media tail/this scope before calling PublishRoomReset, so collection observers can reenter safely.
    /// </summary>
    public LiveRoomScope PrepareRoom(LiveInformation live, LiveAccountStamp account, CancellationToken cancellationToken)
    {
        CheckAdmission(cancellationToken);
        ArgumentNullException.ThrowIfNull(live);
        account.Lifetime.ThrowIfCancellationRequested();
        if (!MatchesAccount(account)) throw new OperationCanceledException("Live account scope ended.");
        _scope?.Invalidate();
        var scope = new LiveRoomScope(this, _id, ++_generation, account, cancellationToken, _shutdown.Token);
        _scope = scope;
        _ownedScopes.Add(scope);
        Binding = null;
        ChatState = LiveChatState.Idle;
        ChatError = null;
        AudienceCount = null;
        _audienceMetric = null;
        if (!ReferenceEquals(_live, live)) { _live = live; ApplyAnchor(live); }
        return scope;
    }

    public void PublishRoomReset(LiveRoomScope scope)
    {
        CheckUi();
        CheckOwner(scope);
        if (!OwnsPublication(scope)) return;
        try { _messages.Clear(); } catch { RecordFailure("CollectionSubscriber"); }
        ClearInbox();
        if (OwnsPublication(scope)) Notify();
    }

    /// <summary>Bind only after the previous scope's actual StopAsync has succeeded.</summary>
    public void BindRoom(LiveRoomScope scope, long roomId, LiveInformation information)
    {
        EnsureCurrent(scope);
        ArgumentNullException.ThrowIfNull(information);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roomId);
        if (scope.Binding is not null) throw new InvalidOperationException("Live room generation is already bound.");
        if (_ownedScopes.Any(previous => !ReferenceEquals(previous, scope) && !previous.Stopped))
            throw new InvalidOperationException("Live room replacement must await previous cleanup.");
        scope.Binding = new(_id, scope.Generation, roomId, scope.Account.Generation, scope.Account.AccountId);
        Binding = scope.Binding;
        ApplyAnchor(information);
        Subscribe(scope);
        Notify();
    }

    public Task ConnectAsync(LiveRoomScope scope)
    {
        EnsureCurrent(scope);
        if (scope.Binding is null) throw new InvalidOperationException("Live room has no binding to connect.");
        if (scope.Connect is not null) return scope.Connect;
        ChatState = LiveChatState.Connecting;
        scope.Connect = ConnectCoreAsync(scope);
        return scope.Connect;
    }

    private async Task ConnectCoreAsync(LiveRoomScope scope)
    {
        await Task.Yield(); // Publish the retained connect task before any facade or observer can reenter.
        try
        {
            EnsureCurrent(scope);
            await _chat.ConnectAsync(scope.Binding!.Value, scope.Token);
            EnsureCurrent(scope);
            ChatState = LiveChatState.Connected; // The facade returns only after positive op-8 acknowledgement.
            ChatError = null;
        }
        catch (OperationCanceledException) { if (IsCurrent(scope)) ChatState = LiveChatState.Closed; }
        catch
        {
            if (IsCurrent(scope)) { ChatState = LiveChatState.Unavailable; ChatError = "Live chat is unavailable."; }
        }
        finally { if (IsCurrent(scope)) Notify(); }
    }

    public Task<LiveSendResult> SendManuallyAsync(LiveBinding binding, string text, CancellationToken cancellationToken)
    {
        CheckUi();
        if (cancellationToken.IsCancellationRequested || !CanSend || _scope is not { } scope
            || scope.Binding != binding || Binding != binding || string.IsNullOrWhiteSpace(text))
            return Task.FromResult(new LiveSendResult(binding, LiveSendOutcome.NotSent, null, "Message was not sent."));
        scope.Sending = true;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(scope.Token, cancellationToken);
        Task<LiveSendResult> actual;
        try { actual = _chat.SendManuallyAsync(binding, text, linked.Token); }
        catch { actual = Task.FromResult(new LiveSendResult(binding, LiveSendOutcome.Unknown, null, "Message delivery is unknown. Do not resend automatically.")); }
        var completion = FinishSendAsync(scope, binding, actual, linked);
        scope.Send = Observe(completion, "room-send");
        Notify();
        return completion;
    }

    private async Task<LiveSendResult> FinishSendAsync(LiveRoomScope scope, LiveBinding binding,
        Task<LiveSendResult> actual, CancellationTokenSource linked)
    {
        try
        {
            // Join this actual attempt, not an advisory-cancelled waiter; retain a known late result for its caller.
            #pragma warning disable VSTHRD003
            var result = await actual;
            #pragma warning restore VSTHRD003
            return result.Binding == binding ? result : new(binding, LiveSendOutcome.Unknown, null, "Message delivery is unknown.");
        }
        catch { return new(binding, LiveSendOutcome.Unknown, null, "Message delivery is unknown. Do not resend automatically."); }
        finally
        {
            linked.Dispose();
            scope.Sending = false; // Never writes the new generation's send admission.
            if (IsCurrent(scope)) Notify();
        }
    }

    public Task StopAsync(LiveRoomScope scope)
    {
        CheckUi();
        CheckOwner(scope);
        // Terminal close owns every remaining drain; no later stop can retry disposal or mask its failure.
        if (Volatile.Read(ref _close) is { } close) return close;
        if (scope.Stop is not null) return scope.Stop;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.Stop = completion.Task;
        _ = CompleteStopAsync(scope, completion);
        return scope.Stop;
    }

    private async Task CompleteStopAsync(LiveRoomScope scope, TaskCompletionSource completion)
    {
        try
        {
            scope.Invalidate();
            Unsubscribe(scope);
            await DrainScopeAsync(scope);
            try { await CloseStepAsync("room-disconnect", () => _chat.DisconnectAsync()); }
            catch (Exception error)
            {
                _releaseBlocked = true;
                RecordFailure("ChatRelease");
                throw LiveCloseDiagnostics.Wrap("Live room cleanup is unconfirmed; replacement is blocked.", error);
            }
            scope.Stopped = true;
            scope.DisposeLifetime();
            _ownedScopes.Remove(scope);
            completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
        finally { ReportRoomStatistics(final: true); }
    }

    /// <summary>Closes admission/publication now; this is not release confirmation. CloseAsync performs the actual drain.</summary>
    public void RequestClose()
    {
        CheckUi();
        Volatile.Write(ref _closeRequested, 1);
        if (_closePublished) return;
        _closePublished = true;
        Binding = null;
        ChatState = LiveChatState.Closed;
        _scope?.Invalidate();
        try { _messages.Clear(); } catch { RecordFailure("CollectionSubscriber"); }
        ClearInbox();
        Notify();
    }

    public Task CloseAsync()
    {
        lock (_closeSync)
        {
            if (_close is not null) return _close;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _close = completion.Task;
            Volatile.Write(ref _closeRequested, 1);
            _cancelShutdown = _shutdown.CancelAsync();
            if (!_tryEnqueue(() => _ = CompleteCloseAsync(completion)))
            {
                _ = Observe(_cancelShutdown, "room-cancel-shutdown");
                completion.TrySetException(new InvalidOperationException("Live room dispatcher is unavailable; owned resources are retained."));
            }
            return _close;
        }
    }

    private async Task CompleteCloseAsync(TaskCompletionSource completion)
    {
        try
        {
            await CloseStepAsync("room-request-close", () => { RequestClose(); return Task.CompletedTask; });
            // Cancellation callbacks only request/observe close, never wait on this consumer.
            #pragma warning disable VSTHRD003
            await Observe(_cancelShutdown, "room-cancel-shutdown", closeStep: true);
            #pragma warning restore VSTHRD003
            var scopes = _ownedScopes.ToArray();
            foreach (var scope in scopes)
            {
                await CloseStepAsync("room-generation-invalidate", () => { scope.Invalidate(); return Task.CompletedTask; });
                await CloseStepAsync("room-generation-unsubscribe", () => { Unsubscribe(scope); return Task.CompletedTask; });
                await DrainScopeAsync(scope);
                // A retained StopAsync may still own an actual disconnect; its failure cannot be overwritten by a no-op.
                #pragma warning disable VSTHRD003
                if (scope.Stop is not null) await Observe(scope.Stop, "room-generation-stop", closeStep: true);
                #pragma warning restore VSTHRD003
            }
            try { await CloseStepAsync("room-chat-dispose", () => _chat.DisposeAsync().AsTask()); }
            catch (Exception error)
            {
                _releaseBlocked = true;
                RecordFailure("ChatRelease");
                throw LiveCloseDiagnostics.Wrap("Live room release is unconfirmed; its owner must remain retained.", error);
            }
            foreach (var scope in scopes)
                await CloseStepAsync("room-generation-lifetime", () => { scope.DisposeLifetime(); return Task.CompletedTask; });
            _ownedScopes.Clear();
            await CloseStepAsync("room-shutdown-lifetime", () => { _shutdown.Dispose(); return Task.CompletedTask; });
            ReleaseConfirmed = true;
            Notify();
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            Notify();
            completion.TrySetException(LiveCloseDiagnostics.Wrap("Live room close is incomplete. Owned resources are retained.", error));
        }
        finally { ReportRoomStatistics(final: true); }
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private async Task DrainScopeAsync(LiveRoomScope scope)
    {
        // These tasks were published by their producers before drain; cancellation is not their completion.
        #pragma warning disable VSTHRD003
        await Observe(scope.Cancellation, "room-generation-cancel", closeStep: true);
        if (scope.Connect is not null) await Observe(scope.Connect, "room-generation-connect", closeStep: true);
        await Observe(scope.Send, "room-generation-send", closeStep: true);
        #pragma warning restore VSTHRD003
    }

    // The legacy media adapter batches these changes with its existing open/finally publication.
    internal void MarkOpenCancelled(LiveRoomScope scope) { if (OwnsPublication(scope)) ChatState = LiveChatState.Closed; }
    internal void ClearStoppedBinding(LiveRoomScope scope) { if (OwnsPublication(scope) && scope.Invalidated) Binding = null; }

    private void ApplyAnchor(LiveInformation information)
    {
        Title = information.Identifier.Title ?? string.Empty;
        var user = information.User ?? _live?.User;
        Anchor = user;
        AnchorName = user?.Name ?? string.Empty;
        AnchorAvatar = user?.Avatar?.Uri;
        IsFollowing = information.Relation switch
        {
            UserRelationStatus.Following or UserRelationStatus.Friends or UserRelationStatus.SpeciallyFollowed => true,
            UserRelationStatus.Unfollow or UserRelationStatus.BeFollowed or UserRelationStatus.Blocked => false,
            _ => null,
        };
    }

    private bool OwnsPublication(LiveRoomScope scope) => !IsClosing && ReferenceEquals(_scope, scope)
        && scope.Generation == _generation && !scope.Account.Lifetime.IsCancellationRequested;
    private bool IsCurrent(LiveRoomScope scope) => OwnsPublication(scope) && !scope.Invalidated
        && !scope.Token.IsCancellationRequested && MatchesAccount(scope.Account);
    private bool MatchesAccount(LiveAccountStamp captured)
    {
        if (captured.Lifetime.IsCancellationRequested) return false;
        var current = _account();
        return current.Generation == captured.Generation && current.AccountId == captured.AccountId
            && current.Lifetime == captured.Lifetime && !current.Lifetime.IsCancellationRequested;
    }
    private void EnsureCurrent(LiveRoomScope scope)
    {
        CheckUi();
        CheckOwner(scope);
        scope.Token.ThrowIfCancellationRequested();
        if (!IsCurrent(scope)) throw new OperationCanceledException("Live room scope was replaced.");
        if (_releaseBlocked) throw new InvalidOperationException("Live room replacement is blocked by unconfirmed cleanup.");
    }
    private void CheckOwner(LiveRoomScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!ReferenceEquals(scope.Owner, this)) throw new ArgumentException("Live room scope belongs to another session.", nameof(scope));
    }
    private void CheckUi()
    {
        if (!_hasThreadAccess()) throw new InvalidOperationException("Live room command requires its dispatcher.");
    }
    private void CheckAdmission(CancellationToken token)
    {
        CheckUi();
        token.ThrowIfCancellationRequested();
        if (IsClosing || _releaseBlocked) throw new InvalidOperationException("Live room admission is closed.");
    }

    private void Subscribe(LiveRoomScope scope)
    {
        scope.ChatHandler = (_, status) =>
        {
            if (scope.Invalidated || scope.Token.IsCancellationRequested || IsClosing || scope.Binding != status.Binding) return;
            lock (_inboxSync) { _chatStatus = (scope, status); PostInboxLocked(); }
        };
        scope.MessageHandler = (_, message) =>
        {
            if (scope.Invalidated || scope.Token.IsCancellationRequested || IsClosing || scope.Binding != message.Binding) return;
            lock (_inboxSync)
            {
                if (_inbox.Count < 256)
                {
                    _inbox.Enqueue((scope, message));
                    Interlocked.Increment(ref _inboxIn);
                }
                else Interlocked.Increment(ref _inboxDrop);
                PostInboxLocked();
            }
        };
        _chat.StatusChanged += scope.ChatHandler;
        _chat.MessageReceived += scope.MessageHandler;
        if (_chat is ILiveAudienceSource audience)
        {
            scope.AudienceHandler = (_, update) =>
            {
                if (scope.Invalidated || scope.Token.IsCancellationRequested || IsClosing || scope.Binding != update.Binding) return;
                lock (_inboxSync)
                {
                    if (_audience?.Update.Metric == LiveAudienceMetric.Watched && update.Metric != LiveAudienceMetric.Watched) return;
                    _audience = (scope, update);
                    PostInboxLocked();
                }
            };
            audience.AudienceChanged += scope.AudienceHandler;
        }
    }
    private void Unsubscribe(LiveRoomScope scope)
    {
        // Detachment failures must not skip independent owner drains/disposal.
        try { _chat.StatusChanged -= scope.ChatHandler; } catch { RecordFailure("StatusSubscriber"); }
        try { _chat.MessageReceived -= scope.MessageHandler; } catch { RecordFailure("MessageSubscriber"); }
        try { if (_chat is ILiveAudienceSource audience) audience.AudienceChanged -= scope.AudienceHandler; }
        catch { RecordFailure("StatusSubscriber"); }
    }
    private void PostInboxLocked()
    {
        ReportRoomStatistics(final: false);
        if (_inboxPosted || IsClosing) return;
        _inboxPosted = true;
        bool queued;
        try { queued = _tryEnqueue(DrainInbox); }
        catch
        {
            Interlocked.Increment(ref _dispatchFail);
            throw;
        }
        if (!queued)
        {
            Interlocked.Increment(ref _dispatchFail);
            _inboxPosted = false; _inbox.Clear(); _chatStatus = null; _audience = null;
        }
    }
    private void ClearInbox()
    {
        lock (_inboxSync) { _inbox.Clear(); _chatStatus = null; _audience = null; }
    }
    private void DrainInbox()
    {
        (LiveRoomScope Scope, LiveChatMessage Message)[] messages;
        (LiveRoomScope Scope, LiveChatStatus Status)? chat;
        (LiveRoomScope Scope, LiveAudienceUpdate Update)? audience;
        lock (_inboxSync)
        {
            messages = _inbox.ToArray(); _inbox.Clear();
            chat = _chatStatus; audience = _audience;
            _chatStatus = null; _audience = null; _inboxPosted = false;
        }
        if (IsClosing) return;
        if (chat is { } c && IsCurrent(c.Scope) && Binding == c.Status.Binding)
        {
            ChatState = c.Status.State;
            ChatError = c.Status.SafeError is null ? null : "Live chat is unavailable.";
        }
        if (audience is { } a && IsCurrent(a.Scope) && Binding == a.Update.Binding
            && (_audienceMetric != LiveAudienceMetric.Watched || a.Update.Metric == LiveAudienceMetric.Watched))
        {
            AudienceCount = a.Update.Count;
            _audienceMetric = a.Update.Metric;
        }
        if (_scope is not { } current || !IsCurrent(current)) return;
        Notify();
        foreach (var item in messages)
        {
            if (!IsCurrent(item.Scope) || Binding != item.Message.Binding) continue;
            try { if (_messages.Count == 200) _messages.RemoveAt(0); } catch { RecordFailure("CollectionSubscriber"); }
            if (!IsCurrent(item.Scope) || Binding != item.Message.Binding) continue;
            try
            {
                _messages.Add(item.Message);
                Interlocked.Increment(ref _published);
            }
            catch { RecordFailure("CollectionSubscriber"); }
            if (IsCurrent(item.Scope) && Binding == item.Message.Binding) PublishMessage(item.Scope, item.Message);
        }
        ReportRoomStatistics(final: false);
    }
    private void PublishMessage(LiveRoomScope scope, LiveChatMessage message)
    {
        if (MessageReceived is not { } handlers) return;
        foreach (EventHandler<LiveChatMessage> handler in handlers.GetInvocationList())
        {
            if (!IsCurrent(scope) || Binding != message.Binding) return;
            try { handler(this, message); } catch { RecordFailure("MessageSubscriber"); }
        }
    }
    private void Notify()
    {
        CheckUi();
        if (PropertyChanged is not { } handlers) return;
        foreach (PropertyChangedEventHandler handler in handlers.GetInvocationList())
            try { handler(this, new PropertyChangedEventArgs(string.Empty)); } catch { RecordFailure("PropertySubscriber"); }
    }
    private async Task Observe(Task task, string name, bool closeStep = false)
    {
        var startedAt = Stopwatch.GetTimestamp();
        Exception? failure = null;
        try
        {
            // Join the independently issued operation, never its own completion or a cancelled waiter.
            #pragma warning disable VSTHRD003
            await task;
            #pragma warning restore VSTHRD003
        }
        catch (OperationCanceledException error) { failure = error; }
        catch (Exception error)
        {
            failure = error;
            RecordFailure("OwnedTaskFault");
            try { if (_chat is ILiveChatDiagnosticSink diagnostics) diagnostics.ReportOwnedTaskFault(name, error); }
            catch { }
        }
        finally { if (closeStep) ReportCloseStep(name, failure, Stopwatch.GetElapsedTime(startedAt)); }
    }

    private async Task CloseStepAsync(string name, Func<Task> operation)
    {
        var startedAt = Stopwatch.GetTimestamp();
        Exception? failure = null;
        try { await operation(); }
        catch (Exception error)
        {
            failure = error;
            LiveCloseDiagnostics.Record(error, name);
            throw;
        }
        finally { ReportCloseStep(name, failure, Stopwatch.GetElapsedTime(startedAt)); }
    }

    private void ReportCloseStep(string name, Exception? error, TimeSpan elapsed)
    {
        try { if (_chat is ILiveChatDiagnosticSink diagnostics) diagnostics.ReportCloseStep(name, error, elapsed); }
        catch { }
    }
    private void RecordFailure(string category)
    {
        try { _reportFailure?.Invoke(category); } catch { }
    }

    private void ReportRoomStatistics(bool final)
    {
        var previous = Volatile.Read(ref _roomStatsAt);
        var now = Stopwatch.GetTimestamp();
        if (!final && Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(30)) return;
        if (Interlocked.CompareExchange(ref _roomStatsAt, now, previous) != previous) return;
        var inboxIn = Interlocked.Exchange(ref _inboxIn, 0);
        var inboxDrop = Interlocked.Exchange(ref _inboxDrop, 0);
        var published = Interlocked.Exchange(ref _published, 0);
        var dispatchFail = Interlocked.Exchange(ref _dispatchFail, 0);
        if (inboxIn == 0 && inboxDrop == 0 && published == 0 && dispatchFail == 0) return;
        try
        {
            if (_chat is ILiveChatDiagnosticSink diagnostics)
                diagnostics.ReportRoomStatistics(inboxIn, inboxDrop, published, dispatchFail);
        }
        catch { } // Diagnostics must not change inbox admission or publication.
    }
}
