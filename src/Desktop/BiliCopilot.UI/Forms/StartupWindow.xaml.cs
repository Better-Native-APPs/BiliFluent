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
    // Sizes are in effective pixels and cover the whole window. The default gives the two-column sign-in page
    // room for a message that wraps below the form; the page scrolls when something still does not fit, and it
    // copes with anything down to the smallest size.
    private const double DefaultWidth = 760;
    private const double DefaultHeight = 560;
    private const double SmallestWidth = 360;
    private const double SmallestHeight = 300;

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

        // A compact overlay has no resize border, so the size chosen here is the one the page keeps.
        MinWidth = SmallestWidth;
        MinHeight = SmallestHeight;
        var (width, height) = GetInitialSize();
        Width = width;
        Height = height;

        Title = ResourceToolkit.GetLocalizedString(StringNames.AppName);

        this.CenterOnScreen();
        this.SetIcon("Assets/logo.ico");
        this.ExtendIntoTitleBar(TitleBar);
        this.Get<AppViewModel>().Windows.Add(this);

        RootFrame.Navigate(typeof(StartupPage), default, new SuppressNavigationTransitionInfo());

        Activated += OnActivated;
        Closed += OnClosed;
    }

    // The default size, or as much of it as the work area of this display has room for (small or heavily
    // scaled screens). The page switches to a single column when it gets less width than two columns need.
    private (double Width, double Height) GetInitialSize()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest)?.WorkArea;
        var scale = this.GetDpiForWindow() / 96d;
        if (area is not { } bounds || scale <= 0)
        {
            return (DefaultWidth, DefaultHeight);
        }

        return (
            Math.Clamp(bounds.Width / scale, SmallestWidth, DefaultWidth),
            Math.Clamp(bounds.Height / scale, SmallestHeight, DefaultHeight));
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
