// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Services.AppLinks;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Xaml.Automation.Peers;
using Richasy.WinUIKernel.Share.Base;
using Windows.System;

namespace BiliCopilot.UI.Controls.Settings;

/// <summary>
/// Shows which program Windows sends bilibili links to, and opens the Windows page where that is changed.
/// </summary>
public sealed partial class AppLinkSettingControl : SettingsPageControlBase
{
    // Holds a line of height before the first answer, so the cards below do not shift when it arrives.
    private const string EmptyLine = "\u200B";

    // The last answer outlives the page, so a later visit starts from it instead of an empty line.
    private static AppLinkDefaultStatus? _lastStatus;

    private readonly WindowsAppLinkAssociationSource _source = new();
    private Window? _window;
    private bool _windowWasInactive;
    private bool _opening;
    private int _refreshVersion;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppLinkSettingControl"/> class.
    /// </summary>
    public AppLinkSettingControl()
    {
        InitializeComponent();
        StatusText.Text = _lastStatus is { } known ? Describe(known) : EmptyLine;

        // The base class stops listening after its first unload; this card has to refresh on every visit.
        Loaded += OnCardLoaded;
        Unloaded += OnCardUnloaded;
    }

    private static string Describe(AppLinkDefaultStatus status)
        => status.State switch
        {
            AppLinkDefaultState.ThisApp => ResourceToolkit.GetLocalizedString(StringNames.AppLinkDefaultThisApp),
            AppLinkDefaultState.OtherApp when status.OtherAppName is { } name
                => ResourceToolkit.GetLocalizedString(StringNames.AppLinkDefaultOtherApp).Replace("{0}", name, StringComparison.Ordinal),
            AppLinkDefaultState.OtherApp => ResourceToolkit.GetLocalizedString(StringNames.AppLinkDefaultOtherAppUnnamed),
            AppLinkDefaultState.RegisteredOnly => ResourceToolkit.GetLocalizedString(StringNames.AppLinkDefaultRegisteredOnly),
            _ => ResourceToolkit.GetLocalizedString(StringNames.AppLinkDefaultUnknown),
        };

    private void OnCardLoaded(object sender, RoutedEventArgs e)
    {
        DetachWindow();
        _window = FindHostWindow();
        if (_window is not null) _window.Activated += OnWindowActivated;
        _ = RefreshStatusAsync();
    }

    private void OnCardUnloaded(object sender, RoutedEventArgs e)
    {
        DetachWindow();
        _refreshVersion++; // A query still running belongs to a card that is no longer shown.
    }

    // The user changes the default in Windows Settings, so the answer is re-read when they come back.
    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _windowWasInactive = true;
            return;
        }

        if (!_windowWasInactive) return;
        _windowWasInactive = false;
        _ = RefreshStatusAsync();
    }

    private Window? FindHostWindow()
    {
        try
        {
            var app = this.Get<AppViewModel>();
            var root = XamlRoot;
            return app.Windows.Find(window => root is not null && Equals(window.Content?.XamlRoot, root)) ?? app.ActivatedWindow;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void DetachWindow()
    {
        if (_window is not null) _window.Activated -= OnWindowActivated;
        _window = null;
        _windowWasInactive = false;
    }

    private async Task RefreshStatusAsync()
    {
        var version = ++_refreshVersion;
        var source = _source;
        var status = await Task.Run(() => AppLinkDefaultApp.QueryAsync(source));
        if (version != _refreshVersion || !IsLoaded) return;

        var changed = _lastStatus is { } previous && previous != status;
        _lastStatus = status;
        var text = Describe(status);
        if (StatusText.Text == text) return;
        StatusText.Text = text;

        // A screen reader hears only a real change, such as after returning from Windows Settings.
        if (changed) FrameworkElementAutomationPeer.FromElement(StatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private async void OnOpenDefaultAppsButtonClick(object sender, RoutedEventArgs e)
    {
        if (_opening) return;
        _opening = true;
        try
        {
            var addresses = AppLinkDefaultApp.BuildSettingsUris(AppLinkDefaultApp.TryGetOwnAppUserModelId(_source), Environment.OSVersion.Version);
            var opened = await AppLinkDefaultApp.OpenSettingsAsync(addresses, static address => Launcher.LaunchUriAsync(address).AsTask());
            if (opened is null)
            {
                this.Get<AppViewModel>().ShowTipCommand.Execute((ResourceToolkit.GetLocalizedString(StringNames.AppLinkDefaultOpenSettingsFailed), InfoType.Error));
            }
        }
        finally
        {
            _opening = false;
        }
    }
}
