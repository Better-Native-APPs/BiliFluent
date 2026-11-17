#nullable enable

using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using BiliCopilot.UI.Live.Application;
using BiliCopilot.UI.Toolkits;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Bili.Authorization;

namespace BiliCopilot.UI.Live;

internal sealed class LiveChatClient : ILiveChatClient, ILiveAudienceSource, ILiveChatDiagnosticSink
{
    private readonly object _gate = new();
    private readonly LiveHttpApi _http;
    private readonly IBiliCookiesResolver _cookies;
    private readonly IBiliTokenResolver _tokens;
    private readonly Func<LiveAccountStamp> _account;
    private readonly ILiveChatDispatcher _dispatcher;
    private readonly Func<ILiveChatSocket> _socketFactory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Queue<LiveChatMessage> _messages = new();
    private LiveChatStatus? _pendingStatus;
    private LiveAudienceUpdate? _pendingAudience;
    private bool _drainScheduled;
    private Owner? _current;
    private SendAttempt? _send;
    private Task? _disconnectTask;
    private Task? _disconnectWorker;
    private TaskCompletionSource<bool>? _disposeCompletion;
    private Task? _disposeWorker;
    private bool _poisoned;

    internal LiveChatClient(HttpMessageHandler handler, IBiliCookiesResolver cookies, IBiliTokenResolver tokens,
        Func<LiveAccountStamp> account, DispatcherQueue dispatcher)
        : this(handler, cookies, tokens, account, CreateDispatcher(handler, dispatcher),
            () => new LiveChatSocket(), (delay, token) => Task.Delay(delay, token))
    {
    }

    // Only for same-assembly composition/fixtures. Production keeps the frozen constructor.
    internal LiveChatClient(HttpMessageHandler handler, IBiliCookiesResolver cookies, IBiliTokenResolver tokens,
        Func<LiveAccountStamp> account, ILiveChatDispatcher dispatcher, Func<ILiveChatSocket> socketFactory,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        try
        {
            _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
            _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
            _account = account ?? throw new ArgumentNullException(nameof(account));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _socketFactory = socketFactory ?? throw new ArgumentNullException(nameof(socketFactory));
            _delay = delay ?? throw new ArgumentNullException(nameof(delay));
            _http = new LiveHttpApi(handler);
        }
        catch { handler?.Dispose(); throw; }
    }

    private static ILiveChatDispatcher CreateDispatcher(HttpMessageHandler handler, DispatcherQueue dispatcher)
    {
        try { return new LiveChatDispatcher(dispatcher); }
        catch { handler?.Dispose(); throw; }
    }

    internal static HttpMessageHandler CreateSecureHandler() => LiveHttpApi.CreateSecureHandler();

    public event EventHandler<LiveChatMessage>? MessageReceived;
    public event EventHandler<LiveChatStatus>? StatusChanged;
    public event EventHandler<LiveAudienceUpdate>? AudienceChanged;

    public Task ConnectAsync(LiveBinding binding, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposeCompletion is not null || _poisoned || _disconnectTask is not null || _current is not null)
                return Task.FromException(new LiveChatFailure("Live chat must finish closing before opening again.", false));
            if (binding.SessionId == Guid.Empty || binding.RoomId <= 0 || binding.RoomGeneration < 0 || binding.AccountGeneration < 0)
                return Task.FromException(LiveProtocol.Invalid());
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            var owner = new Owner(binding, cancellationToken);
            _current = owner; // Ready/Done are published before any factory can reenter.
            owner.Worker = RunOwnerAsync(owner);
            // T2-01: Owner.Ready is published before the connection factory; authentication is produced by receiver/deadline, not this caller.
            #pragma warning disable VSTHRD003
            return owner.Ready.Task;
            #pragma warning restore VSTHRD003
        }
    }

    public Task DisconnectAsync()
    {
        lock (_gate)
        {
            if (_disconnectTask is not null)
                return _disconnectTask;
            var owner = _current;
            var send = _send;
            if (owner is null && send is null)
                return Task.CompletedTask;
            var completion = Completion<bool>();
            _disconnectTask = completion.Task;
            if (owner is not null)
            {
                owner.Accepting = false; // Invalidate admission/callbacks before first await.
                owner.IsConnected = false;
                Cancel(owner.Stop);
            }
            _messages.Clear();
            _pendingStatus = null;
            _pendingAudience = null;
            _disconnectWorker = DrainAsync(owner, send, completion);
            return completion.Task;
        }
    }

    public Task<LiveSendResult> SendManuallyAsync(LiveBinding binding, string text, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var owner = _current;
            if (owner is null || !CanUse(owner) || !owner.IsConnected || owner.Binding != binding
                || _send is not null || cancellationToken.IsCancellationRequested
                || string.IsNullOrWhiteSpace(text) || text.Length > 1000)
                return Task.FromResult(LiveHttpApi.NotSent(binding));
            var attempt = new SendAttempt(owner, binding, cancellationToken);
            _send = attempt; // Single-flight result/owner published BEFORE SendAsync factory.
            attempt.Worker = RunSendAsync(attempt, text);
            // T2-02: SendAttempt.Done is published before the HTTP factory; return that identical single-flight owner completion.
            #pragma warning disable VSTHRD003
            return attempt.Done.Task;
            #pragma warning restore VSTHRD003
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeCompletion is null)
            {
                _disposeCompletion = Completion<bool>(); // Permanently close new admission.
                _disposeWorker = DisposeCoreAsync(_disposeCompletion);
            }
            return new ValueTask(_disposeCompletion.Task);
        }
    }

    private async Task RunOwnerAsync(Owner owner)
    {
        var retries = 0;
        LiveChatFailure? terminalFailure = null;
        try
        {
            while (true)
            {
                owner.Stop.Token.ThrowIfCancellationRequested();
                var startedAt = Stopwatch.GetTimestamp();
                owner.ConnectionAttempts++;
                owner.CloseReason = LiveFailureCategory.AccountResolver.ToString();
                owner.LastStableDuration = TimeSpan.Zero;
                LiveChatFailure failure;
                try
                {
                    QueueStatus(owner, LiveChatState.Connecting, null);
                    var credentials = await LiveChatDispatch.InvokeAsync(_dispatcher, () =>
                    {
                        lock (_gate)
                        {
                            RequireCurrent(owner);
                            var captured = LiveCredentials.Capture(owner.Binding, _account, _cookies, _tokens);
                            if (owner.Stamp.HasValue && owner.Stamp.Value != captured.Stamp)
                                throw new LiveChatFailure("Live chat account changed.", false, true);
                            owner.Stamp = captured.Stamp;
                            return captured;
                        }
                    }, owner.Stop.Token).ConfigureAwait(false);
                    if (!owner.AccountLinked)
                    {
                        owner.AccountCancellation = credentials.Stamp.Lifetime.Register(() => Cancel(owner.Stop));
                        owner.AccountLinked = true;
                    }
                    await RunSocketAsync(owner, credentials).ConfigureAwait(false);
                    failure = new LiveChatFailure("Live chat connection closed.", false);
                }
                catch (Exception exception) { failure = Classify(exception); }
                lock (_gate) owner.IsConnected = false;
                ReportStatistics(owner.Statistics, final: true);
                ReportClosed(owner.Stop.IsCancellationRequested ? LiveChatState.Closed.ToString() : owner.CloseReason,
                    Stopwatch.GetElapsedTime(startedAt));
                if (owner.Stop.IsCancellationRequested)
                    break;
                ReportFailure(failure);
                if (owner.ReleaseFailed || !failure.Transient)
                {
                    QueueStatus(owner, failure.Closed ? LiveChatState.Closed : failure.Unavailable ? LiveChatState.Unavailable : LiveChatState.Failed,
                        failure.Closed ? null : failure.Message);
                    terminalFailure = failure;
                    return;
                }
                if (owner.LastStableDuration >= TimeSpan.FromSeconds(60))
                    retries = 0;
                if (retries >= 6)
                {
                    var exhausted = new LiveChatFailure("Live chat retry limit was reached.", false);
                    QueueStatus(owner, LiveChatState.Failed, exhausted.Message);
                    terminalFailure = exhausted;
                    return;
                }
                retries++;
                QueueStatus(owner, LiveChatState.Retrying, "Live chat is reconnecting.");
                await _delay(RetryDelay(retries), owner.Stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            if (!owner.Stop.IsCancellationRequested)
            {
                var failure = Classify(exception);
                ReportFailure(failure);
                QueueStatus(owner, failure.Unavailable ? LiveChatState.Unavailable : LiveChatState.Failed, failure.Message);
                terminalFailure = failure;
            }
        }
        finally
        {
            // The callback only requests cancellation. Join its actual unregister before Done,
            // so DrainAsync cannot dispose Stop while that callback is still using it.
            try { await CloseStepAsync("chat-account-unregister", () => owner.AccountCancellation.DisposeAsync().AsTask()).ConfigureAwait(false); }
            catch (Exception error)
            {
                RecordFailure(LiveFailureCategory.RegistrationRelease);
                owner.ReleaseFailed = true;
                lock (_gate) _poisoned = true;
                terminalFailure = LiveCloseDiagnostics.Forward(new LiveChatFailure("Live chat cleanup is incomplete.", false), error);
                // Keep the registration on the retained owner; still publish actual worker completion.
            }
            lock (_gate) owner.IsConnected = false;
            if (terminalFailure is not null) owner.Ready.TrySetException(terminalFailure);
            owner.Ready.TrySetCanceled(new CancellationToken(true));
            owner.Done.TrySetResult(true);
        }
    }

    private async Task RunSocketAsync(Owner owner, LiveCredentials credentials)
    {
        owner.LastStableDuration = TimeSpan.Zero;
        owner.CloseReason = LiveFailureCategory.Discovery.ToString();
        var discovery = await _http.DiscoverAsync(owner.Binding, credentials, owner.Stop.Token).ConfigureAwait(false);
        ReportPreparation(credentials.UserId != 0, discovery.Preparation, owner.ConnectionAttempts);
        owner.CloseReason = LiveFailureCategory.AccountResolver.ToString();
        await LiveChatDispatch.InvokeAsync(_dispatcher, () =>
        {
            lock (_gate) { RequireCurrentAccount(owner); return true; }
        }, owner.Stop.Token).ConfigureAwait(false);
        owner.CloseReason = LiveFailureCategory.SocketConnect.ToString();
        var attempt = new SocketAttempt(_socketFactory, owner.Stop.Token, owner.Statistics);
        owner.Attempt = attempt; // Retain every allocated socket, including failed connections.
        try
        {
            using (var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(attempt.Stop.Token))
            {
                connectDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                attempt.Connector = attempt.Socket.ConnectAsync(discovery.Endpoint, connectDeadline.Token);
                await attempt.Connector.ConfigureAwait(false);
            }
            QueueStatus(owner, LiveChatState.Authenticating, null);
            attempt.CloseReason = LiveFailureCategory.Authentication.ToString();
            using (var authDeadline = CancellationTokenSource.CreateLinkedTokenSource(attempt.Stop.Token))
            {
                authDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                await using var cancelAuth = authDeadline.Token.Register(() => attempt.Auth.TrySetCanceled(authDeadline.Token)).ConfigureAwait(false);
                var authentication = LiveProtocol.Authentication(credentials.UserId, owner.Binding.RoomId, discovery.Key, discovery.Buvid);
                try
                {
                    attempt.AuthSentAt = Stopwatch.GetTimestamp();
                    attempt.AuthWriter = WriteAsync(attempt, authentication, authDeadline.Token);
                    await attempt.AuthWriter.ConfigureAwait(false);
                }
                finally { Array.Clear(authentication, 0, authentication.Length); }
                attempt.Receiver = ReceiveAsync(owner, attempt);
                // T2-03: The socket receiver/deadline independently completes Auth; the authentication producer does not await this socket consumer.
                #pragma warning disable VSTHRD003
                await attempt.Auth.Task.ConfigureAwait(false); // Only an explicit op-8 code:0 can complete it.
                #pragma warning restore VSTHRD003
            }
            attempt.CloseReason = LiveFailureCategory.SocketReceive.ToString();
            if (attempt.Receiver.IsCompleted)
                await attempt.Receiver.ConfigureAwait(false);
            await LiveChatDispatch.InvokeAsync(_dispatcher, () =>
            {
                lock (_gate)
                {
                    RequireCurrentAccount(owner);
                    owner.IsConnected = true;
                    attempt.ConnectedAt = Stopwatch.GetTimestamp();
                    QueueStatus(owner, LiveChatState.Connected, null);
                    owner.Ready.TrySetResult(true);
                    return true;
                }
            }, owner.Stop.Token).ConfigureAwait(false);
            attempt.Heartbeat = HeartbeatAsync(attempt);
            try { await attempt.Receiver.ConfigureAwait(false); }
            catch when (attempt.HeartbeatFailure is not null) { throw attempt.HeartbeatFailure!; }
        }
        finally
        {
            lock (_gate) owner.IsConnected = false;
            if (attempt.ConnectedAt.HasValue)
                owner.LastStableDuration = Stopwatch.GetElapsedTime(attempt.ConnectedAt.Value);
            Cancel(attempt.Stop);
            try
            {
                if (attempt.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var closeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    attempt.Closer = CloseOutputAsync(attempt, closeDeadline.Token);
                    await ObserveAsync(attempt.Closer, "chat-close-output", LiveFailureCategory.CloseHandshake).ConfigureAwait(false);
                }
            }
            catch (Exception) { RecordFailure(LiveFailureCategory.CloseHandshake); }
            try { attempt.Socket.Abort(); }
            catch (Exception) { RecordFailure(LiveFailureCategory.SocketAbort); }
            await ObserveAsync(attempt.Connector, "chat-connect").ConfigureAwait(false);
            await ObserveAsync(attempt.AuthWriter, "chat-auth-send").ConfigureAwait(false);
            await ObserveAsync(attempt.Receiver, "chat-receive").ConfigureAwait(false);
            await ObserveAsync(attempt.Heartbeat, "chat-heartbeat").ConfigureAwait(false);
            try
            {
                await CloseStepAsync("chat-socket-dispose", () =>
                {
                    attempt.Socket.Dispose();
                    owner.Attempt = null;
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                attempt.CloseReason = LiveFailureCategory.ChatRelease.ToString();
                owner.ReleaseFailed = true;
                lock (_gate) _poisoned = true;
                // Keep owner.Attempt/socket strongly owned and reject all replacement.
                // A failed release must override an earlier transport failure: replacement is unsafe.
                #pragma warning disable CA2219
                throw LiveCloseDiagnostics.Forward(new LiveChatFailure("Live chat transport could not be released.", false), error);
                #pragma warning restore CA2219
            }
            finally
            {
                owner.CloseReason = owner.ReleaseFailed ? LiveFailureCategory.ChatRelease.ToString()
                    : attempt.HeartbeatFailure is not null ? LiveFailureCategory.Heartbeat.ToString()
                    : attempt.ServerCloseReason ?? attempt.CloseReason;
                attempt.Writes.Dispose();
                attempt.Stop.Dispose();
            }
        }
    }

    private async Task ReceiveAsync(Owner owner, SocketAttempt attempt)
    {
        var fragment = new byte[16384];
        var fragments = 0;
        using var assembled = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await attempt.Socket.ReceiveAsync(new ArraySegment<byte>(fragment), attempt.Stop.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    attempt.ServerCloseReason = result.CloseStatus is { } closeStatus && Enum.IsDefined(closeStatus)
                        ? closeStatus.ToString() : LiveChatState.Closed.ToString();
                    // Only known server-restart/internal/try-later closes are transient.
                    var code = (int?)result.CloseStatus;
                    throw new LiveChatFailure("Live chat connection closed.", code is 1011 or 1012 or 1013,
                        closed: code is 1000 or 1001);
                }
                if (++fragments > 4096 || result.MessageType != WebSocketMessageType.Binary || result.Count < 0 || result.Count > fragment.Length
                    || result.Count > LiveProtocol.MaxMessageBytes - assembled.Length)
                    throw LiveProtocol.Invalid();
                await assembled.WriteAsync(fragment.AsMemory(0, result.Count)).ConfigureAwait(false);
                if (!result.EndOfMessage)
                    continue;
                Interlocked.Increment(ref attempt.Statistics.WsMessages);
                LiveProtocol.Decode(assembled.ToArray(), (operation, body) =>
                {
                    Interlocked.Increment(ref attempt.Statistics.Packets);
                    if (operation == 5)
                    {
                        Interlocked.Increment(ref attempt.Statistics.Op5);
                        var command = LiveChatDiagnosticFacts.CommandName(body, out var danmaku);
                        if (danmaku) Interlocked.Increment(ref attempt.Statistics.Danmaku);
                        else Interlocked.Increment(ref attempt.Statistics.Other);
                        attempt.Statistics.RecordCommand(command);
                    }
                    if (operation == 8)
                    {
                        ReportAuthentication(LiveChatDiagnosticFacts.AuthenticationCode(body),
                            Stopwatch.GetElapsedTime(attempt.AuthSentAt));
                        LiveProtocol.RequireAuthenticationSuccess(body);
                        attempt.Auth.TrySetResult(true);
                    }
                    else if (operation == 3)
                    {
                        Interlocked.Increment(ref attempt.Statistics.HeartbeatReply);
                        if (body.Length < 4)
                            throw LiveProtocol.Invalid();
                        _ = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body.Span);
                        // Popularity is control data, not a display message.
                    }
                    else if (operation == 5 && attempt.Auth.Task.IsCompletedSuccessfully)
                    {
                        var audience = LiveProtocol.ReadAudience(owner.Binding, body);
                        if (audience is not null)
                        {
                            QueueAudience(owner, audience);
                            return;
                        }
                        var message = LiveProtocol.ReadMessage(owner.Binding, body);
                        if (message is not null)
                        {
                            QueueMessage(owner, message);
                        }
                        else Interlocked.Increment(ref attempt.Statistics.ParseNull);
                    }
                });
                assembled.SetLength(0);
                fragments = 0;
            }
        }
        catch (Exception exception)
        {
            ReportOwnedTaskFault("chat-receive", exception);
            var failure = Classify(exception);
            attempt.Auth.TrySetException(failure);
            throw failure;
        }
    }

    private async Task HeartbeatAsync(SocketAttempt attempt)
    {
        try
        {
            while (true)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(attempt.Stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                await WriteAsync(attempt, LiveProtocol.Encode(2, ReadOnlySpan<byte>.Empty), deadline.Token).ConfigureAwait(false);
                Interlocked.Increment(ref attempt.Statistics.HeartbeatSent);
                await _delay(TimeSpan.FromSeconds(30), attempt.Stop.Token).ConfigureAwait(false);
                ReportStatistics(attempt.Statistics, final: false);
            }
        }
        catch (Exception exception)
        {
            ReportOwnedTaskFault("chat-heartbeat", exception);
            if (!attempt.Stop.IsCancellationRequested)
            {
                attempt.CloseReason = LiveFailureCategory.Heartbeat.ToString();
                attempt.HeartbeatFailure = Classify(exception);
            }
            Cancel(attempt.Stop);
            try { attempt.Socket.Abort(); }
            catch (Exception) { RecordFailure(LiveFailureCategory.SocketAbort); }
        }
    }

    private static async Task WriteAsync(SocketAttempt attempt, byte[] packet, CancellationToken token)
    {
        await attempt.Writes.WaitAsync(token).ConfigureAwait(false);
        try { await attempt.Socket.SendAsync(new ArraySegment<byte>(packet), token).ConfigureAwait(false); }
        finally { attempt.Writes.Release(); }
    }

    private static async Task CloseOutputAsync(SocketAttempt attempt, CancellationToken token)
    {
        await attempt.Writes.WaitAsync(token).ConfigureAwait(false);
        try { await attempt.Socket.CloseOutputAsync(token).ConfigureAwait(false); }
        finally { attempt.Writes.Release(); }
    }

    private async Task RunSendAsync(SendAttempt attempt, string text)
    {
        LiveSendResult result;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token, attempt.Owner.Stop.Token);
        try
        {
            var responseTask = await LiveChatDispatch.InvokeAsync(_dispatcher, () =>
            {
                lock (_gate)
                {
                    RequireCurrentAccount(attempt.Owner);
                    var credentials = LiveCredentials.Capture(attempt.Binding, _account, _cookies, _tokens);
                    if (!credentials.CanSend || credentials.Stamp != attempt.Owner.Stamp)
                        return Task.FromResult(LiveHttpApi.NotSent(attempt.Binding));
                    var request = LiveHttpApi.CreateSendRequest(attempt.Binding, text, credentials);
                    attempt.Response = _http.SendOnceAsync(request, attempt.Binding, cancellation.Token, () =>
                    {
                        if (!CanUse(attempt.Owner) || !attempt.Owner.IsConnected || _send != attempt
                            || attempt.Binding != attempt.Owner.Binding || cancellation.IsCancellationRequested
                            || !CurrentAccountMatches(attempt.Owner))
                            return false;
                        attempt.Dispatched = true;
                        return true;
                    });
                    return attempt.Response;
                }
            }, cancellation.Token).ConfigureAwait(false);
            result = await responseTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // If the UI action began, its response task is still owned even if handoff failed.
            if (attempt.Response is not null)
            {
                try {
                    // T2-04: Error recovery rejoins the same retained HTTP response attempt, never a replacement or its own observer task.
                    #pragma warning disable VSTHRD003
                    result = await attempt.Response.ConfigureAwait(false);
                    #pragma warning restore VSTHRD003
                }
                catch (Exception) { result = attempt.Dispatched ? LiveHttpApi.Unknown(attempt.Binding) : LiveHttpApi.NotSent(attempt.Binding); }
            }
            else
                result = attempt.Dispatched ? LiveHttpApi.Unknown(attempt.Binding) : LiveHttpApi.NotSent(attempt.Binding);
        }
        lock (_gate)
        {
            if (_send == attempt)
                _send = null;
            // No message insertion/status event: this result belongs only to its caller/binding.
            attempt.Done.TrySetResult(result);
        }
    }

    private async Task DrainAsync(Owner? owner, SendAttempt? send, TaskCompletionSource<bool> completion)
    {
        try
        {
            if (owner is not null)
            {
                // T2-05: Disconnect starts cancellation before joining prepublished owner.Done; owner production does not wait for disconnect.
                #pragma warning disable VSTHRD003
                await owner.Done.Task.ConfigureAwait(false);
                #pragma warning restore VSTHRD003
                // T2-06: Join the actual connection worker after Done; cancellation and published completion alone do not release physical ownership.
                #pragma warning disable VSTHRD003
                await owner.Worker.ConfigureAwait(false);
                #pragma warning restore VSTHRD003
            }
            if (send is not null)
            {
                // T2-07: Disconnect joins the retained send completion; the independently issued HTTP attempt does not await this drain.
                #pragma warning disable VSTHRD003
                await send.Done.Task.ConfigureAwait(false);
                #pragma warning restore VSTHRD003
                // T2-08: The physical send worker must finish after Done before release; advisory cancellation is not completion.
                #pragma warning disable VSTHRD003
                await send.Worker.ConfigureAwait(false);
                #pragma warning restore VSTHRD003
            }
            lock (_gate)
            {
                var startedAt = Stopwatch.GetTimestamp();
                if (owner?.ReleaseFailed == true)
                {
                    var error = new LiveChatFailure("Live chat transport could not be released.", false);
                    LiveCloseDiagnostics.Record(error, "chat-transport-release-confirmed");
                    ReportCloseStep("chat-transport-release-confirmed", error, Stopwatch.GetElapsedTime(startedAt));
                    throw error;
                }
                ReportCloseStep("chat-transport-release-confirmed", null, Stopwatch.GetElapsedTime(startedAt));
                owner?.Stop.Dispose();
                if (_current == owner)
                    _current = null;
                completion.TrySetResult(true);
                _disconnectTask = null;
            }
        }
        catch (Exception error)
        {
            lock (_gate) _poisoned = true;
            completion.TrySetException(LiveCloseDiagnostics.Forward(new LiveChatFailure("Live chat cleanup is incomplete.", false), error));
        }
    }

    private async Task DisposeCoreAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            await CloseStepAsync("chat-disconnect", DisconnectAsync).ConfigureAwait(false);
            await CloseStepAsync("chat-http-handler-dispose", () => { _http.Dispose(); return Task.CompletedTask; }).ConfigureAwait(false);
            completion.TrySetResult(true);
        }
        catch (Exception error)
        {
            lock (_gate) _poisoned = true;
            completion.TrySetException(LiveCloseDiagnostics.Forward(new LiveChatFailure("Live chat cleanup is incomplete.", false), error));
        }
    }

    private void QueueStatus(Owner owner, LiveChatState state, string? safeError)
    {
        lock (_gate)
        {
            if (!CanPublish(owner))
                return;
            _pendingStatus = new LiveChatStatus(owner.Binding, state, safeError);
            ScheduleDrain();
        }
    }

    private void QueueMessage(Owner owner, LiveChatMessage message)
    {
        lock (_gate)
        {
            if (!CanUse(owner) || !owner.Dedupe.Admit(message))
                return;
            if (_messages.Count >= 256)
            {
                Interlocked.Increment(ref owner.Statistics.QueueDrop);
                return;
            }
            _messages.Enqueue(message);
            Interlocked.Increment(ref owner.Statistics.QueueIn);
            ScheduleDrain();
        }
    }

    private void QueueAudience(Owner owner, LiveAudienceUpdate audience)
    {
        lock (_gate)
        {
            if (!CanUse(owner)) return;
            if (_pendingAudience?.Metric == LiveAudienceMetric.Watched && audience.Metric != LiveAudienceMetric.Watched) return;
            _pendingAudience = audience;
            ScheduleDrain();
        }
    }

    private void ScheduleDrain()
    {
        if (_drainScheduled)
            return;
        _drainScheduled = true;
        var queued = false;
        try { queued = _dispatcher.TryEnqueue(DrainEvents); }
        catch (Exception) { RecordFailure(LiveFailureCategory.Dispatcher); }
        if (!queued)
        {
            _drainScheduled = false;
            _messages.Clear();
            _pendingStatus = null;
            _pendingAudience = null;
        }
    }

    private void DrainEvents()
    {
        lock (_gate)
        {
            var owner = _current;
            if (owner is null || !CanPublish(owner) || !CurrentAccountMatches(owner))
            {
                _messages.Clear();
                _pendingStatus = null;
                _pendingAudience = null;
            }
            else
            {
                var status = _pendingStatus;
                _pendingStatus = null;
                if (status is not null && status.Binding == owner.Binding)
                {
                    try { StatusChanged?.Invoke(this, status); }
                    catch (Exception) { RecordFailure(LiveFailureCategory.StatusSubscriber); }
                }
                var audience = _pendingAudience;
                _pendingAudience = null;
                if (audience is not null && audience.Binding == owner.Binding && CanUse(owner) && CurrentAccountMatches(owner))
                {
                    try { AudienceChanged?.Invoke(this, audience); }
                    catch (Exception) { RecordFailure(LiveFailureCategory.StatusSubscriber); }
                }
                for (var count = 0; count < 64 && _messages.Count > 0; count++)
                {
                    var message = _messages.Dequeue();
                    if (!CanUse(owner) || !CurrentAccountMatches(owner) || message.Binding != owner.Binding)
                        continue;
                    try
                    {
                        if (MessageReceived is { } handlers)
                        {
                            Interlocked.Increment(ref owner.Statistics.Dispatched);
                            handlers(this, message);
                        }
                    }
                    catch (Exception) { RecordFailure(LiveFailureCategory.MessageSubscriber); }
                }
            }
            _drainScheduled = false;
            if (_messages.Count > 0 || _pendingStatus is not null || _pendingAudience is not null)
                ScheduleDrain();
        }
    }

    // _gate is held by callers. Account resolver is used only from dispatcher callbacks.
    private bool CanUse(Owner owner) => ReferenceEquals(_current, owner) && owner.Accepting
        && !owner.Stop.IsCancellationRequested && _disposeCompletion is null && !_poisoned;

    private bool CanPublish(Owner owner) => ReferenceEquals(_current, owner) && owner.Accepting
        && !owner.Stop.IsCancellationRequested && _disposeCompletion is null;

    private bool CurrentAccountMatches(Owner owner)
    {
        try
        {
            var stamp = _account();
            return owner.Stamp.HasValue && stamp == owner.Stamp.Value && LiveCredentials.Matches(stamp, owner.Binding);
        }
        catch (Exception) { RecordFailure(LiveFailureCategory.AccountResolver); return false; }
    }

    private void RequireCurrent(Owner owner)
    {
        if (!CanUse(owner))
            throw new LiveChatFailure("Live chat binding is no longer current.", false, true);
    }

    private void RequireCurrentAccount(Owner owner)
    {
        RequireCurrent(owner);
        if (!CurrentAccountMatches(owner))
            throw new LiveChatFailure("Live chat account changed.", false, true);
    }

    internal static TimeSpan RetryDelay(int retry) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Clamp(retry - 1, 0, 5)) * (0.75 + (Random.Shared.NextDouble() * 0.5))));

    private static LiveChatFailure Classify(Exception exception) => exception switch
    {
        LiveChatFailure known => known,
        OperationCanceledException => new LiveChatFailure("Live chat operation timed out or was canceled.", true),
        WebSocketException => new LiveChatFailure("Live chat connection was interrupted.", true),
        HttpRequestException => new LiveChatFailure("Live chat discovery was interrupted.", true),
        IOException => new LiveChatFailure("Live chat transport was interrupted.", true),
        _ => new LiveChatFailure("Live chat is unavailable.", false),
    };

    private void Cancel(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (AggregateException) { RecordFailure(LiveFailureCategory.CancellationCallback); }
        catch (ObjectDisposedException) { RecordFailure(LiveFailureCategory.CancellationAfterRelease); }
    }

    private async Task ObserveAsync(Task task, string name, LiveFailureCategory category = LiveFailureCategory.OwnedTaskFault)
    {
        try {
            // T2-09: This observer consumes already-issued owned work; it cannot be the producer of the completion it observes.
            #pragma warning disable VSTHRD003
            await task.ConfigureAwait(false);
            #pragma warning restore VSTHRD003
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { RecordFailure(category); ReportOwnedTaskFault(name, error); }
    }

    private void ReportOwnedTaskFault(string name, Exception error)
        => SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<LiveChatClient>>(), logger =>
        {
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning("Live | task-fault name={Name:l} error={Error:l} inner={Inner:l}",
                    name, error.GetType().Name, error.InnerException?.GetType().Name ?? "none");
        });

    void ILiveChatDiagnosticSink.ReportOwnedTaskFault(string name, Exception error) => ReportOwnedTaskFault(name, error);

    private async Task CloseStepAsync(string name, Func<Task> operation)
    {
        var startedAt = Stopwatch.GetTimestamp();
        Exception? failure = null;
        try { await operation().ConfigureAwait(false); }
        catch (Exception error) { failure = error; LiveCloseDiagnostics.Record(error, name); throw; }
        finally { ReportCloseStep(name, failure, Stopwatch.GetElapsedTime(startedAt)); }
    }

    void ILiveChatDiagnosticSink.ReportCloseStep(string name, Exception? error, TimeSpan elapsed) => ReportCloseStep(name, error, elapsed);

    private void ReportCloseStep(string name, Exception? error, TimeSpan elapsed)
        => SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<LiveChatClient>>(), logger =>
        {
            var level = error is null ? LogLevel.Information : LogLevel.Warning;
            if (logger.IsEnabled(level))
                logger.Log(level, "Live | close-step name={Name:l} result={Result:l} error={Error:l} inner={Inner:l} ms={Milliseconds}",
                    name, error is null ? "ok" : "failed", error?.GetType().Name ?? "none",
                    error?.InnerException?.GetType().Name ?? "none", (long)elapsed.TotalMilliseconds);
        });

    private void RecordFailure(LiveFailureCategory category)
        => SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<LiveChatClient>>(),
            logger => logger.LogWarning("Live chat operation failed: {Category}", category));

    private void ReportFailure(LiveChatFailure failure)
        => SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<LiveChatClient>>(),
            logger => logger.LogWarning("Live chat failed: {Reason}", failure.Message));

    private void ReportPreparation(bool uidSet, LiveChatPreparation preparation, int attempt)
        => SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<LiveChatClient>>(), logger =>
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(
                    "LiveChat | prepare uid-set={UidSet:l} sessdata={Sessdata:l} bili-jct={BiliJct:l} buvid={Buvid:l} nav-code={NavCode:l} nav-login={NavLogin:l} nav-mid-match={NavMidMatch:l} hosts={Hosts} attempt={Attempt}",
                    LiveChatDiagnosticFacts.Boolean(uidSet), LiveChatDiagnosticFacts.Boolean(preparation.Sessdata),
                    LiveChatDiagnosticFacts.Boolean(preparation.BiliJct), LiveChatDiagnosticFacts.Boolean(preparation.Buvid),
                    LiveChatDiagnosticFacts.Code(preparation.NavCode), LiveChatDiagnosticFacts.Boolean(preparation.NavLogin),
                    LiveChatDiagnosticFacts.Boolean(preparation.NavMidMatch), preparation.Hosts, attempt);
        });

    private void ReportAuthentication(int? code, TimeSpan elapsed)
        => SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<LiveChatClient>>(), logger =>
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("LiveChat | auth code={Code:l} ms={Milliseconds}",
                    LiveChatDiagnosticFacts.Code(code), (long)elapsed.TotalMilliseconds);
        });

    private void ReportStatistics(LiveChatStatistics statistics, bool final)
    {
        if (statistics.TakeSnapshot(final) is not { } snapshot) return;
        SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<LiveChatClient>>(), logger =>
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(
                    "LiveChat | stats secs={Seconds:l} ws-messages={WsMessages} packets={Packets} op5={Op5} danmaku={Danmaku} other={Other} parse-null={ParseNull} queue-in={QueueIn} queue-drop={QueueDrop} dispatched={Dispatched} heartbeat-sent={HeartbeatSent} heartbeat-reply={HeartbeatReply} top-cmds={TopCommands:l}",
                    snapshot.Seconds.ToString("F1", CultureInfo.InvariantCulture), snapshot.WsMessages, snapshot.Packets,
                    snapshot.Op5, snapshot.Danmaku, snapshot.Other, snapshot.ParseNull, snapshot.QueueIn, snapshot.QueueDrop,
                    snapshot.Dispatched, snapshot.HeartbeatSent, snapshot.HeartbeatReply, snapshot.TopCommands);
        });
    }

    private void ReportClosed(string reason, TimeSpan elapsed)
        => SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<LiveChatClient>>(), logger =>
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("LiveChat | closed reason={Reason:l} lasted-secs={Seconds:l}",
                    reason, elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture));
        });

    void ILiveChatDiagnosticSink.ReportRoomStatistics(long inboxIn, long inboxDrop, long published, long dispatchFail)
        => SafeFailureContext.TryWriteFacts(() => this.Get<ILogger<LiveChatClient>>(), logger =>
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("LiveChat | room inbox-in={InboxIn} inbox-drop={InboxDrop} published={Published} dispatch-fail={DispatchFail}",
                    inboxIn, inboxDrop, published, dispatchFail);
        });

    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Owner
    {
        internal Owner(LiveBinding binding, CancellationToken token)
        {
            Binding = binding;
            Stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        }
        internal readonly LiveBinding Binding;
        internal readonly CancellationTokenSource Stop;
        internal readonly TaskCompletionSource<bool> Ready = Completion<bool>();
        internal readonly TaskCompletionSource<bool> Done = Completion<bool>();
        internal readonly LiveEventDeduplicator Dedupe = new();
        internal readonly LiveChatStatistics Statistics = new();
        internal Task Worker = Task.CompletedTask;
        internal LiveAccountStamp? Stamp;
        internal CancellationTokenRegistration AccountCancellation;
        internal bool AccountLinked;
        internal bool Accepting = true;
        internal bool IsConnected;
        internal bool ReleaseFailed;
        internal TimeSpan LastStableDuration;
        internal SocketAttempt? Attempt;
        internal int ConnectionAttempts;
        internal string CloseReason = LiveFailureCategory.AccountResolver.ToString();
    }

    private sealed class SocketAttempt
    {
        internal SocketAttempt(Func<ILiveChatSocket> factory, CancellationToken token, LiveChatStatistics statistics)
        {
            Statistics = statistics;
            Stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            try { Socket = factory() ?? throw LiveProtocol.Invalid(); }
            catch { Stop.Dispose(); Writes.Dispose(); throw; }
        }
        internal readonly ILiveChatSocket Socket;
        internal readonly CancellationTokenSource Stop;
        internal readonly SemaphoreSlim Writes = new(1, 1);
        internal readonly TaskCompletionSource<bool> Auth = Completion<bool>();
        internal readonly LiveChatStatistics Statistics;
        internal Task Connector = Task.CompletedTask;
        internal Task AuthWriter = Task.CompletedTask;
        internal Task Receiver = Task.CompletedTask;
        internal Task Heartbeat = Task.CompletedTask;
        internal Task Closer = Task.CompletedTask;
        internal LiveChatFailure? HeartbeatFailure;
        internal long? ConnectedAt;
        internal long AuthSentAt;
        internal string CloseReason = LiveFailureCategory.SocketConnect.ToString();
        internal string? ServerCloseReason;
    }

    private sealed class SendAttempt
    {
        internal SendAttempt(Owner owner, LiveBinding binding, CancellationToken token)
        {
            Owner = owner;
            Binding = binding;
            Token = token;
        }
        internal readonly Owner Owner;
        internal readonly LiveBinding Binding;
        internal readonly CancellationToken Token;
        internal readonly TaskCompletionSource<LiveSendResult> Done = Completion<LiveSendResult>();
        internal Task Worker = Task.CompletedTask;
        internal Task<LiveSendResult>? Response;
        internal bool Dispatched;
    }
}
