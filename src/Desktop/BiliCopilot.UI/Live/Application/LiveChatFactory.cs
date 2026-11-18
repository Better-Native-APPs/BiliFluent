#nullable enable

using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Bili.Authorization;

namespace BiliCopilot.UI.Live.Application;

/// <summary>
/// Retains handler allocation even if the real chat constructor throws while disposing it.
/// The forwarding handler adds no headers/redirects/cookies; retries only unfinished disposal phases.
/// </summary>
internal sealed class LiveChatFactory
{
    private OwnedHandler? _attempt;
    internal bool HasWork => _attempt is { ReleaseConfirmed: false };

    internal ILiveChatClient Create(IBiliCookiesResolver cookies, IBiliTokenResolver tokens,
        Func<LiveAccountStamp> account, DispatcherQueue dispatcher)
    {
        ForgetReleased();
        if (_attempt is not null) throw Failure();
        var handler = new OwnedHandler();
        _attempt = handler; // Managed owner precedes secure-handler allocation and constructor transfer.
        try
        {
            handler.InnerHandler = LiveChatClient.CreateSecureHandler();
            return new LiveChatClient(handler, cookies, tokens, account, dispatcher);
        }
        catch
        {
            // Chat's constructor normally disposes its transferred handler itself. Never do it twice.
            if (!handler.DisposeStarted)
            {
                try { handler.Dispose(); } catch { }
            }
            ForgetReleased();
            throw Failure();
        }
    }

    internal void ForgetReleased()
    {
        if (_attempt is { ReleaseConfirmed: true }) _attempt = null;
    }

    internal void RetryRelease()
    {
        if (_attempt is { ReleaseConfirmed: false } handler) handler.Dispose();
        ForgetReleased();
    }

    private static InvalidOperationException Failure() => new("直播聊天初始化或释放未完成，未启动替代连接。");

    internal sealed class OwnedHandler : HttpMessageHandler
    {
        private HttpMessageHandler? _inner;
        private HttpMessageInvoker? _invoker;
        private bool _innerReleased;
        private bool _invokerReleased;
        internal bool DisposeStarted { get; private set; }
        internal bool ReleaseConfirmed { get; private set; }

        internal HttpMessageHandler? InnerHandler
        {
            get => _inner;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                if (_inner is not null || DisposeStarted) throw Failure();
                _inner = value; // Retain allocation before constructing its forwarding invoker.
                _invoker = new HttpMessageInvoker(value, disposeHandler: false);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _invoker!.SendAsync(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (!disposing) { base.Dispose(false); return; }
            if (ReleaseConfirmed) return;
            DisposeStarted = true;
            try
            {
                if (!_invokerReleased) { _invoker?.Dispose(); _invokerReleased = true; }
                if (!_innerReleased) { _inner?.Dispose(); _innerReleased = true; }
                base.Dispose(true);
                ReleaseConfirmed = true;
            }
            catch (Exception error) { throw LiveCloseDiagnostics.Wrap(Failure().Message, error); }
        }
    }
}
