using BiliCopilot.UI.Forms;
using BiliCopilot.UI.Hosting;
using BiliCopilot.UI.Live.Application;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class AppViewModel
{
    private readonly SemaphoreSlim _embeddedGate = new(1, 1);
    private CancellationTokenSource? _embeddedOpenCancellation;
    // Non-owning association: a successor open must not be cancelled while closing its predecessor.
    private CancellationTokenSource? _embeddedPlayerOpeningCancellation;
    private long _embeddedOpenVersion;
    private long _sharedNavigationVersion;
    public PlayerViewModel? EmbeddedPlayer { get; private set; }
    public bool HasEmbeddedWork => EmbeddedPlayer is not null || _embeddedOpenCancellation is not null;
    internal bool HasIndependentWorkRequiringMainWindow
        => Players.Any(player => !player.IsEmbedded && !player.HasUsableIndependentHost);

    private Task OpenMainPlayerAsync(MediaSnapshot snapshot)
    {
        CheckPlaybackAccess();
        if (IsPlaybackStopping) return Task.CompletedTask;
        var bridge = LiveApplication;
        // UI-confined capture precedes ApplicationPlayback.RunAsync's first yield/await.
        LiveLaunchAccount? account = snapshot.Type == BiliMediaType.Live ? bridge.CaptureLaunchAccount() : null;
        var navigationVersion = _sharedNavigationVersion;
        Task OpenAsync()
        {
            return OwnApplicationPlaybackAsync(token =>
            {
                token.ThrowIfCancellationRequested();
                if (navigationVersion != _sharedNavigationVersion)
                    return Task.CompletedTask; // Navigation won before a route/native owner existed.
                return account is { } liveAccount
                    ? bridge.OpenLiveAsync((admission, routeToken) => OpenMainPlayerCoreAsync(snapshot, routeToken, admission, liveAccount), liveAccount, token)
                    : bridge.OpenVideoAsync((admission, routeToken) => OpenMainPlayerCoreAsync(snapshot, routeToken, admission), token);
            });
        }

        Task? navigation = null;
        QueueMainPlayerNavigation(snapshot, OpenAsync, ref navigation);
        return navigation ?? OpenAsync();
    }

    partial void QueueMainPlayerNavigation(MediaSnapshot snapshot, Func<Task> open, ref Task? navigation);
    partial void OnMainPlayerCreated(PlayerViewModel player, MediaSnapshot snapshot);
    partial void OnMainPlayerHostAttached(PlayerViewModel player);
    partial void OnMainPlayerClosing(PlayerViewModel? player);
    partial void OnMainPlayerReleased(PlayerViewModel player);
    partial void OnMainPlaybackNavigationChanged();

    private async Task OpenMainPlayerCoreAsync(MediaSnapshot snapshot, CancellationToken admissionToken,
        LiveRouteCoordinator.Admission admission, LiveLaunchAccount? account = null)
    {
        var isLive = snapshot.Type == BiliMediaType.Live;
        admissionToken.ThrowIfCancellationRequested();
        var version = ++_embeddedOpenVersion;
        var previousRequest = _embeddedOpenCancellation;
        var previousPlayer = previousRequest is not null && ReferenceEquals(_embeddedPlayerOpeningCancellation, previousRequest)
            ? EmbeddedPlayer : null;
        var request = CancellationTokenSource.CreateLinkedTokenSource(admissionToken);
        _embeddedOpenCancellation = request;
        var entered = false;
        var closedWhileOpening = false;
        try
        {
            // A callback-reentrant Player close must wait until this exact cancellation scope settles.
            var cancellation = previousPlayer?.BeginSynchronousCancellationScope();
            try
            {
                // Complete callbacks synchronously; failures belong to this transition's cleanup scope.
#pragma warning disable VSTHRD103
                try { previousRequest?.Cancel(); }
#pragma warning restore VSTHRD103
                catch (Exception ex)
                {
                    previousPlayer?.RetainOpeningCancellationFailure(ex);
                    throw;
                }
            }
            finally
            {
                if (cancellation is not null) previousPlayer!.CompleteSynchronousCancellationScope(cancellation);
            }
            await _embeddedGate.WaitAsync(request.Token);
            entered = true;
            request.Token.ThrowIfCancellationRequested();
            if (EmbeddedPlayer is { } previous) await LiveApplication.ClosePlayerAsync(previous);
            request.Token.ThrowIfCancellationRequested();
            if (version != _embeddedOpenVersion || IsPlaybackStopping) return;

            if (snapshot.Type is not (BiliMediaType.Video or BiliMediaType.Pgc or BiliMediaType.Live))
                throw new NotSupportedException("主窗口 mpv 播放仅支持视频、剧集和直播。");
            if (isLive && account is null) throw new InvalidOperationException("Live launch account was not captured.");
            if (account is { } captured) LiveApplication.CheckLaunchAccount(captured);

            var main = Windows.OfType<MainWindow>().FirstOrDefault()
                ?? throw new InvalidOperationException("主窗口尚未创建。");
            var vm = this.Get<PlayerViewModel>();
            vm.MarkEmbedded();
            LiveApplication.Retain(vm, admission); // Composite player/room + permit, before host or native creation.
            vm.DecodeOverride = null;
            var mode = GetNewInitialPresentation();
            EmbeddedPlayer = vm;
            _embeddedPlayerOpeningCancellation = request;
            try
            {
                OnMainPlayerCreated(vm, snapshot);
                void CheckOpening()
                {
                    request.Token.ThrowIfCancellationRequested();
                    if (account is { } launchAccount) LiveApplication.CheckLaunchAccount(launchAccount);
                    if (version != _embeddedOpenVersion || IsPlaybackStopping || vm.IsPlaybackClosing)
                        throw new OperationCanceledException("Playback opening was superseded or closed.");
                }

                Players.Add(vm);
                vm.IsConnecting = true;
                CheckOpening();
                main.Activate();
                CheckOpening();
                var slot = main.ShowEmbeddedPlayer(vm);
                CheckOpening();
                var host = new MainPlayerHost(main, slot);
                await vm.AttachHostAsync(host, request.Token);
                CheckOpening();
                OnMainPlayerHostAttached(vm);
                await vm.SetPresentationAsync(mode);
                CheckOpening();
                if (account is { } liveAccount)
                {
                    await vm.InitializeLiveAsync(snapshot, liveAccount.Account, request.Token);
                    CheckOpening();
                    // Embedded InitializeAsync preserves its VOD error surface and may swallow a
                    // load error. Only the live video-output barrier establishes live launch success.
                    if (!vm.HasLiveFrame) throw new InvalidOperationException("Live playback could not start.");
                }
                else
                {
                    await vm.InitializeAsync(snapshot, request.Token);
                    CheckOpening();
                }
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested || vm.IsPlaybackClosing)
            {
                closedWhileOpening = vm.IsPlaybackClosing;
                // Leave the app opening gate before a published close's delayed cancellation.
                // The route/registry still joins the real close and retains its permit throughout.
                if (!vm.HasPublishedClose) await LiveApplication.ClosePlayerAsync(vm);
                if (isLive) throw;
            }
            catch (Exception ex)
            {
                SafeFailureContext.TryWrite(FailureStage.EmbeddedOpen, ex, () => _logger);
                if (isLive)
                {
                    ReportLiveFailure();
                    await LiveApplication.ClosePlayerAsync(vm);
                    throw new InvalidOperationException("Live playback could not start.");
                }
                // Keep the failed session + return affordance visible; no hidden fallback engine.
                vm.ErrorMessage = ex.Message;
                vm.IsConnecting = false;
                _logger.LogError(ex, "Embedded player initialization failed.");
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested || closedWhileOpening)
        {
            if (isLive) throw;
        }
        catch (Exception ex)
        {
            if (isLive)
            {
                SafeFailureContext.TryWrite(FailureStage.EmbeddedOpen, ex, () => _logger);
                ReportLiveFailure();
                throw new InvalidOperationException("Live playback transition is incomplete.");
            }
            _logger.LogError(ex, "Main player transition failed; ownership retained.");
            ShowTipCommand.Execute((ex.Message, InfoType.Error));
        }
        finally
        {
            if (entered) _embeddedGate.Release();
            if (ReferenceEquals(_embeddedOpenCancellation, request)) _embeddedOpenCancellation = null;
            if (ReferenceEquals(_embeddedPlayerOpeningCancellation, request)) _embeddedPlayerOpeningCancellation = null;
            OnMainPlaybackNavigationChanged();
            // InitializeAsync links this request for the lifetime of the active media.
            // Dispose releases registrations without cancelling successfully loaded playback.
            request.Dispose();
        }
    }

    private static PlayerPresentationMode GetNewInitialPresentation()
        => SettingsToolkit.ReadLocalSetting(SettingNames.PlayerPresenterMode, PlayerPresenterMode.Default) switch
        {
            PlayerPresenterMode.FullScreen => PlayerPresentationMode.FullScreen,
            PlayerPresenterMode.CompactOverlay => PlayerPresentationMode.CompactOverlay,
            _ => PlayerPresentationMode.Normal,
        };

    private static void CancelEmbeddedOpening(CancellationTokenSource? request)
    {
        // Synchronous callbacks are observed by the transition/close owner, not an unowned exit.
#pragma warning disable VSTHRD103
        try { request?.Cancel(); }
#pragma warning restore VSTHRD103
        catch (ObjectDisposedException)
        {
            // OpenMainPlayerCoreAsync disposes this source only after leaving its opening gate.
            // A delayed close may capture that already-finished request; nothing remains to cancel.
        }
    }

    internal Action? CaptureEmbeddedOpeningCancellation(PlayerViewModel player)
    {
        if (!ReferenceEquals(EmbeddedPlayer, player)) return null;
        var request = _embeddedPlayerOpeningCancellation;
        if (request is null) return null;
        if (ReferenceEquals(_embeddedOpenCancellation, request)) ++_embeddedOpenVersion;
        return () => CancelEmbeddedOpening(request);
    }

    public Task CloseEmbeddedPlayerAsync()
    {
        CheckPlaybackAccess();
        ++_sharedNavigationVersion; // Also invalidates an entry still in ApplicationPlayback's first yield.
        ++_embeddedOpenVersion;
        var request = _embeddedOpenCancellation;
        var current = EmbeddedPlayer;
        var pendingLive = false;
        var pending = _liveApplication?.CapturePendingOpeningClose(out pendingLive) ?? (() => Task.CompletedTask);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (pendingLive || current is not null && IsSharedLivePlayer(current))
            _ = LiveApplication.RegisterNavigationLiveClose(done.Task); // Captured VM/open; publish before close/cancel callbacks.
        var close = LiveRouteCoordinator.Invoke(() => LiveCloseDiagnostics.Invoke("navigation", () => BeginEmbeddedCloseAsync(current, request)));
        var pendingClose = LiveRouteCoordinator.Invoke(pending);
        _ = CompleteEmbeddedNavigationCloseAsync(done, close, pendingClose);
        return done.Task;
    }

    internal Task CloseEmbeddedPlayerForRouteAsync()
    {
        CheckPlaybackAccess();
        ++_embeddedOpenVersion;
        // Replacement already owns its request cancellation. Closing a predecessor must not
        // invalidate the successor or acquire a second application permit.
        return LiveCloseDiagnostics.Invoke("replace", () => BeginEmbeddedCloseAsync(EmbeddedPlayer, _embeddedOpenCancellation));
    }

    private static async Task CompleteEmbeddedNavigationCloseAsync(TaskCompletionSource done, Task close, Task pendingClose)
    {
        try
        {
#pragma warning disable VSTHRD003 // Both captured close producers are started before this lease-free navigation join.
            await Task.WhenAll(close, pendingClose);
#pragma warning restore VSTHRD003
            done.TrySetResult();
        }
        catch (Exception ex) { done.TrySetException(ex); }
    }

    private Task BeginEmbeddedCloseAsync(PlayerViewModel? current, CancellationTokenSource? request)
    {
        OnMainPlayerClosing(current);
        void CancelOpening() => CancelEmbeddedOpening(request);
        Exception? cancellationError = null;
        // An already-published close has observed its original opening CTS. A later request
        // belongs to its own transition, not to a retroactive failure of that older close.
        if (current is null || current.HasPublishedClose)
        {
            try { CancelOpening(); }
            catch (Exception ex) { cancellationError = ex; }
        }
        // Publish the real Player close and stop admission before invoking reentrant callbacks.
        _ = current?.CloseAsync(CancelOpening);
        var close = current is null ? null : LiveApplication.ClosePlayerAsync(current);
        return CloseEmbeddedCoreAsync(current, close, cancellationError);
    }

    private async Task CloseEmbeddedCoreAsync(PlayerViewModel? target, Task? close, Exception? cancellationError)
    {
        Exception? closeError = null;
        // No embedded gate is owned here; the published Player close drains its actual owned work.
#pragma warning disable VSTHRD003
        try { if (close is not null) await close; }
#pragma warning restore VSTHRD003
        catch (Exception ex) { closeError = ex; }
        await _embeddedGate.WaitAsync();
        try
        {
            // A failed first close still must wait for the opening transition to leave its gate.
            if (closeError is not null)
            {
                if (cancellationError is not null)
                {
                    if (target is not null && IsSharedLivePlayer(target))
                        SafeFailureContext.TryWrite(FailureStage.EmbeddedOpen, cancellationError, () => _logger);
                    else _logger.LogWarning(cancellationError, "Opening cancellation failed while awaiting the primary player close failure.");
                }
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(closeError).Throw();
            }
            if (cancellationError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cancellationError).Throw();
            // The gate is a drain barrier, not permission to retarget an old Back to a successor.
            if (target is not null) await LiveApplication.ClosePlayerAsync(target);
        }
        finally { _embeddedGate.Release(); }
    }

    internal void ReleasePlayer(PlayerViewModel vm)
    {
        // Player calls this before its CloseAsync completion is set. The registry observes that
        // exact real task, then performs app bookkeeping; failed closes retain both VM and permit.
        if (_liveApplication?.RequestPlayerRelease(vm) == true) return;
        CompletePlayerRelease(vm); // Independent VOD retains its existing release behavior.
    }

    internal bool IsSharedLivePlayer(PlayerViewModel vm)
        => _liveApplication?.IsLiveOwner(vm) == true || vm.IsLiveMedia;

    internal void CompletePlayerRelease(PlayerViewModel vm)
    {
        if (ReferenceEquals(EmbeddedPlayer, vm))
        {
            OnMainPlayerReleased(vm);
            Windows.OfType<MainWindow>().FirstOrDefault()?.ClearEmbeddedPlayer(vm);
            EmbeddedPlayer = null;
            OnMainPlaybackNavigationChanged();
        }
        Players.Remove(vm);
        NotifyPlaybackActivityChanged();
    }

    /// <summary>Explicit new independent session, never a fullscreen transfer.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    public Task OpenIndependentPlayerAsync(MediaSnapshot snapshot)
        => snapshot.Type == BiliMediaType.Live ? OpenMainPlayerAsync(snapshot)
            : OwnApplicationPlaybackAsync(token => OpenIndependentPlayerCoreAsync(snapshot, token));

    private async Task OpenIndependentPlayerCoreAsync(MediaSnapshot snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var vm = this.Get<PlayerViewModel>();
        Players.Add(vm);
        try
        {
            // PlayerWindow construction is managed-only. Assign ownership before native readiness.
            await vm.AttachHostAsync(new PlayerWindow(), token);
            token.ThrowIfCancellationRequested();
            await vm.InitializeAsync(snapshot, token);
        }
        catch (Exception ex)
        {
            // This await is outside InitializeAsync/OwnWork/the media gate, so close cannot self-join.
            // Keep main reachable even when native release fails and the registered owner remains.
            Windows.OfType<MainWindow>().FirstOrDefault()?.Activate();
            try { await vm.CloseAsync(); }
            catch (Exception cleanupError)
            {
                _logger.LogError(cleanupError, "Independent player cleanup failed; ownership retained.");
                Windows.OfType<MainWindow>().FirstOrDefault()?.Activate();
                ShowTipCommand.Execute((cleanupError.Message, InfoType.Error));
                return;
            }
            if (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Independent player initialization failed; host released.");
                ShowTipCommand.Execute((ex.Message, InfoType.Error));
            }
        }
    }
}
