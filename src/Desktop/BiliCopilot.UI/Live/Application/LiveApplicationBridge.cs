#nullable enable

using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Bili.Authorization;

namespace BiliCopilot.UI.Live.Application;

/// <summary>One shared player route and composite-owner registry, resolved after App construction.</summary>
internal sealed class LiveApplicationBridge
{
    private readonly AppViewModel _app;
    private readonly AccountViewModel _account;
    private readonly IBiliTokenResolver _tokens;
    private readonly DispatcherQueue _dispatcher;
    private readonly LiveRouteCoordinator _routes;
    private Task? _shutdown;

    public LiveApplicationBridge(AppViewModel app, IBiliTokenResolver tokens,
        AccountViewModel account, DispatcherQueue dispatcher)
    {
        _app = app;
        _account = account;
        _tokens = tokens;
        _dispatcher = dispatcher;
        _routes = new LiveRouteCoordinator(CheckAccess, app.CloseEmbeddedPlayerForRouteAsync, app.ReportLiveFailure);
    }

    internal bool HasWork => _routes.HasWork || _shutdown is { IsCompleted: false };
    internal bool HasLiveWork => _routes.HasLiveWork;

    internal bool HasActivePlayback
        => _routes.LiveOwners.OfType<PlayerViewModel>().Any(owner => owner.IsConnecting || owner.IsPlaybackClosing
            || owner.IsLiveStarting || owner.IsLiveReady && owner.LiveLoadState != LiveMediaLoadState.Paused);

    internal LiveAccountStamp CaptureAccount()
    {
        CheckAccess();
        return new(_account.AccountId, _account.AccountGeneration, _account.AccountCancellationToken);
    }

    internal LiveLaunchAccount CaptureLaunchAccount()
        => new(CaptureAccount(), _tokens.GetToken() is not null, _account.MyProfile?.IsVip == true);

    internal async Task OpenLiveAsync(Func<LiveRouteCoordinator.Admission, CancellationToken, Task> open,
        LiveLaunchAccount capturedAccount, CancellationToken token)
    {
        CheckAccess();
        CheckLaunchAccount(capturedAccount);
        if (_shutdown is not null) throw Failure();
        using var accountScope = CancellationTokenSource.CreateLinkedTokenSource(token, capturedAccount.Account.Lifetime);
        await _routes.OpenLiveAsync(async (admission, routeToken) =>
        {
            CheckLaunchAccount(capturedAccount);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(routeToken, capturedAccount.Account.Lifetime);
            await open(admission, linked.Token);
            CheckLaunchAccount(capturedAccount);
        }, accountScope.Token);
    }

    internal Task OpenVideoAsync(Func<LiveRouteCoordinator.Admission, CancellationToken, Task> open, CancellationToken token)
    {
        CheckAccess();
        if (_shutdown is not null) throw Failure();
        return _routes.OpenVideoAsync(open, token);
    }

    internal Task RegisterNavigationLiveClose(Task close)
    {
        CheckAccess();
        // Forward the already-issued owner close without acquiring a lease or changing completion.
        #pragma warning disable VSTHRD003
        return _routes.RegisterNavigationLiveClose(close);
        #pragma warning restore VSTHRD003
    }

    internal Task WaitForNavigationLiveCloseAsync()
    {
        CheckAccess();
        return _routes.WaitForNavigationLiveCloseAsync();
    }

    internal Func<Task> CapturePendingOpeningClose(out bool isLive)
    {
        CheckAccess();
        return _routes.CapturePendingOpeningClose(out isLive);
    }

    internal void Retain(PlayerViewModel player, LiveRouteCoordinator.Admission admission)
    {
        CheckAccess();
        // The VM owns its room, chat factory, partial host and native player. Retaining this
        // composite before AttachHostAsync also covers a room/client created later during open.
        admission.Retain(player, player.CloseAsync, () => _app.CompletePlayerRelease(player));
    }

    internal Task ClosePlayerAsync(PlayerViewModel player)
    {
        CheckAccess();
        return _routes.CloseOwnerAsync(player);
    }

    internal bool RequestPlayerRelease(PlayerViewModel player)
    {
        CheckAccess();
        return _routes.RequestOwnerRelease(player);
    }

    internal bool IsLiveOwner(PlayerViewModel player) => _routes.IsLiveOwner(player);

    internal Task ShutdownAsync()
    {
        CheckAccess();
        if (_shutdown is not null) return _shutdown;
        return _shutdown = _routes.ShutdownAsync();
    }

    private void CheckAccount(LiveAccountStamp stamp)
    {
        stamp.Lifetime.ThrowIfCancellationRequested();
        if (CaptureAccount() != stamp) throw new OperationCanceledException("账号已变化，未转移原直播会话。");
    }

    internal void CheckLaunchAccount(LiveLaunchAccount captured)
    {
        CheckAccount(captured.Account);
        // Do not silently adopt eligibility that changed after the launch snapshot was captured.
        if ((_tokens.GetToken() is not null) != captured.LoggedIn || (_account.MyProfile?.IsVip == true) != captured.Vip)
            throw new OperationCanceledException("账号播放资格已变化，未转移原直播请求。");
    }

    private void CheckAccess()
    {
        if (!_dispatcher.HasThreadAccess) throw new InvalidOperationException("直播播放操作需要应用界面线程。");
    }

    private static InvalidOperationException Failure() => new("直播关闭或播放器登记未完成，原所有者仍被保留。");
}

internal readonly record struct LiveLaunchAccount(LiveAccountStamp Account, bool LoggedIn, bool Vip);
