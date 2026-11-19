// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Services.AppLinks;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.Windows.AppLifecycle;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.WinUIKernel.Share.Base;
using System.ComponentModel;
using System.Globalization;

namespace BiliCopilot.UI;

public partial class App
{
    private readonly AppLinkActivationGate _appLinks = new();
    private AccountViewModel? _appLinkAccount;
    private IBiliTokenResolver? _appLinkTokens;
    private AppViewModel? _appLinkApp;
    private object? _appLinkRoot;
    private Func<bool>? _appLinkRootIsCurrent;
    private int _appLinkDispatchQueued;

    partial void InitializeAppLinkRouting()
    {
        // Unlike notification intents, public navigation may wait on a login page in this process.
        _appLinkTokens = this.Get<IBiliTokenResolver>();
        _appLinkAccount = this.Get<AccountViewModel>();
        _appLinkApp = this.Get<AppViewModel>();
        _appLinkAccount.AccountChanged += OnAppLinkAccountChanged;
        _appLinkApp.PropertyChanged += OnAppLinkAppChanged;
    }

    partial void CaptureAppLinkActivation(AppActivationArguments arguments, bool initial, ref bool handled)
    {
        if (arguments.Kind != ExtendedActivationKind.Protocol) return;
        handled = true;
        try
        {
            var address = (arguments.Data as IProtocolActivatedEventArgs)?.Uri?.OriginalString;
            var capture = _appLinks.Capture(address, initial);
            if (capture.SupersededKind is { } previous) LogAppLink(previous, "dropped-superseded");
            LogAppLink(capture.Kind, capture.Result);
            if (capture.Result == "pending") QueueAppLinkDispatch();
        }
        catch (Exception)
        {
            _appLinks.DropPending();
            LogAppLink("invalid", "failed-capture");
        }
    }

    private void RefreshAppLinkReadiness()
    {
        try
        {
            var token = _appLinkTokens?.GetToken();
            var signedIn = token is { UserId: > 0 } && _appLinkAccount is { } account
                && account.AccountId == token.UserId.ToString(CultureInfo.InvariantCulture)
                && !account.AccountCancellationToken.IsCancellationRequested;
            _appLinks.UpdateReadiness(signedIn, _appLinkApp?.IsInitialLoading != false,
                _appLinkApp?.IsClosed == true, _appLinkRootIsCurrent?.Invoke() == true);
        }
        catch (Exception)
        {
            _appLinks.UpdateReadiness(false, true, false, false);
            LogAppLink("invalid", "failed-context");
        }
    }

    private void QueueAppLinkDispatch()
    {
        if (Interlocked.CompareExchange(ref _appLinkDispatchQueued, 1, 0) != 0) return;
        try
        {
            if (_dispatcherQueue.TryEnqueue(DispatchPendingAppLink)) return;
            LogAppLink("invalid", "failed-dispatch");
        }
        catch (Exception) { LogAppLink("invalid", "failed-dispatch"); }
        Volatile.Write(ref _appLinkDispatchQueued, 0);
    }

    private async void DispatchPendingAppLink()
    {
        try
        {
            // Use the tray's existing show/activate path, including a not-yet-ready main window.
            if (_appLinks.HasPending) ForegroundAppLinkWindow();
            while (true)
            {
                RefreshAppLinkReadiness();
                if (_appLinks.TakePending() is not { } request) break;
                if (!_appLinks.TryConsume(request))
                {
                    LogAppLink(request.Resolution.Kind, "dropped-superseded");
                    continue;
                }
                ForegroundAppLinkWindow();
                if (request.Resolution.Destination is not { } destination)
                {
                    await _appLinkApp!.ShowTipCommand.ExecuteAsync((ResourceToolkit.GetLocalizedString(StringNames.AppLinkUnsupported), InfoType.Warning));
                    LogAppLink("unsupported", "notified");
                }
                else
                {
                    await TextLinkNavigator.OpenAsync(destination, isStillShown: () => _appLinks.IsCurrent(request), waitForPlayback: false);
                    LogAppLink(request.Resolution.Kind, _appLinks.IsCurrent(request) ? "dispatched" : "dropped-superseded");
                }
            }
        }
        catch (Exception)
        {
            LogAppLink("invalid", "failed-navigation");
        }
        finally
        {
            Volatile.Write(ref _appLinkDispatchQueued, 0);
            if (_appLinks.CanDispatchPending) QueueAppLinkDispatch();
        }
    }

    private void OnAppLinkAccountChanged(object? sender, EventArgs args) => QueueAppLinkDispatch();

    private static void ForegroundAppLinkWindow()
    {
        try { GetMainWindow()?.Activate(); }
        catch (Exception) { LogAppLink("invalid", "failed-foreground"); }
    }

    private void OnAppLinkAppChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppViewModel.IsInitialLoading) or nameof(AppViewModel.IsClosed)) QueueAppLinkDispatch();
    }

    partial void BeginAppLinkRoot(object owner, Func<bool> isCurrent)
    {
        _appLinkRoot = owner;
        _appLinkRootIsCurrent = isCurrent;
        _appLinks.BeginRoot(owner);
    }

    partial void CompleteAppLinkNavigation(object owner)
    {
        _appLinks.CompleteNavigation(owner);
        QueueAppLinkDispatch();
    }

    partial void RetireAppLinkRoot(object owner)
    {
        _appLinks.RetireRoot(owner);
        if (!ReferenceEquals(_appLinkRoot, owner)) return;
        _appLinkRoot = null;
        _appLinkRootIsCurrent = null;
    }

    internal static void DiscardPendingAppLinkForRestart()
    {
        if (!Current._appLinks.DropPending()) return;
        try { Serilog.Log.Information("AppLink | result=dropped-restart"); }
        catch { /* Diagnostics cannot change the restart contract. */ }
    }

    private static void LogAppLink(string kind, string result)
    {
        try { Serilog.Log.Information("AppLink | kind={Kind:l} result={Result:l}", kind, result); }
        catch { /* No activation payload or exception details may enter diagnostics. */ }
    }
}
