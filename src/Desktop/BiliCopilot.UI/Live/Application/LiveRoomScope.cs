#nullable enable

namespace BiliCopilot.UI.Live.Application;

/// <summary>
/// An opaque room generation, created and released only by its room session.
/// The token may cancel before its connect/send/release tasks actually finish.
/// </summary>
// The session owns the lifetime; exposing IDisposable would let callers release it before its tasks drain.
#pragma warning disable CA1001
public sealed class LiveRoomScope
#pragma warning restore CA1001
{
    private readonly CancellationTokenSource _lifetime;
    private bool _disposed;

    internal LiveRoomScope(LiveRoomSession owner, Guid sessionId, long generation,
        LiveAccountStamp account, CancellationToken request, CancellationToken shutdown)
    {
        Owner = owner;
        SessionId = sessionId;
        Generation = generation;
        Account = account;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(request, account.Lifetime, shutdown);
        Token = _lifetime.Token;
    }

    public Guid SessionId { get; }
    public long Generation { get; }
    public LiveAccountStamp Account { get; }
    public CancellationToken Token { get; }
    public LiveBinding? Binding { get; internal set; }

    internal LiveRoomSession Owner { get; }
    internal bool Invalidated { get; private set; }
    internal bool Stopped { get; set; }
    internal bool Sending { get; set; }
    internal Task Cancellation { get; private set; } = Task.CompletedTask;
    internal Task? Connect { get; set; }
    internal Task Send { get; set; } = Task.CompletedTask;
    internal Task? Stop { get; set; }
    internal EventHandler<LiveChatStatus>? ChatHandler { get; set; }
    internal EventHandler<LiveChatMessage>? MessageHandler { get; set; }
    internal EventHandler<LiveAudienceUpdate>? AudienceHandler { get; set; }

    internal void Invalidate()
    {
        if (Invalidated) return;
        Invalidated = true;
        if (!_disposed) Cancellation = _lifetime.CancelAsync();
    }

    internal void DisposeLifetime()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Dispose();
    }
}
