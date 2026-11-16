// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Pages;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media.Animation;
using WinUIEx;

namespace BiliCopilot.UI.Forms;

/// <summary>
/// 初始窗口.
/// </summary>
public sealed partial class StartupWindow : WindowEx
{
    private bool _isFirstActivated = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartupWindow"/> class.
    /// </summary>
    public StartupWindow()
    {
        InitializeComponent();
        IsMaximizable = false;
        IsMinimizable = false;
        IsResizable = false;
        AppWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay);

        Width = 760;
        Height = 500;

        Title = ResourceToolkit.GetLocalizedString(StringNames.AppName);

        this.CenterOnScreen();
        this.SetIcon("Assets/logo.ico");
        this.ExtendIntoTitleBar(TitleBar);
        this.Get<AppViewModel>().Windows.Add(this);

        RootFrame.Navigate(typeof(StartupPage), default, new SuppressNavigationTransitionInfo());

        Activated += OnActivated;
        Closed += OnClosed;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        this.Get<AppViewModel>().Windows.Remove(this);
        Activated -= OnActivated;
        Closed -= OnClosed;
        var startup = this.Get<StartupPageViewModel>();
        startup.ExitCommand.Execute(false);

        // A closing window does not always unload its page, so what was typed is cleared here as well.
        startup.ResetSignInForm();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_isFirstActivated)
        {
            _isFirstActivated = false;
            return;
        }

        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        // Waits for pending input, so the click that activated this window still reaches what was clicked.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, OnReactivated);
    }

    private void OnReactivated()
    {
        // A challenge or web sign-in window that is still open belongs in front of this one.
        if (CaptchaWindow.TryActivateCurrent())
        {
            return;
        }

        var webSignIn = this.Get<AppViewModel>().Windows.FirstOrDefault(p => p is WebSignInWindow);
        if (webSignIn is not null)
        {
            webSignIn.Activate();
            return;
        }

        // The QR code keeps its place while it is being polled; only a stopped one is replaced.
        this.Get<StartupPageViewModel>().ResumeQRCodeCommand.Execute(default);
    }
}
