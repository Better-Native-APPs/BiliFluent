// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Windowing;
using Richasy.BiliKernel.Bili.Authorization;
using WinUIEx;

namespace BiliCopilot.UI.Forms;

/// <summary>
/// 网页登录窗口.
/// </summary>
public sealed partial class WebSignInWindow : WindowEx
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private bool _isClosed;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebSignInWindow"/> class.
    /// </summary>
    public WebSignInWindow()
    {
        InitializeComponent();
        AppWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay);

        Width = 1200;
        Height = 632;

        Title = ResourceToolkit.GetLocalizedString(StringNames.AppName);

        this.CenterOnScreen();
        this.SetIcon("Assets/logo.ico");
        this.ExtendIntoTitleBar(TitleBar);
        this.Get<AppViewModel>().Windows.Add(this);

        Closed += OnClosed;
        Activated += OnActivated;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
        => this.Get<StartupPageViewModel>().ExitCommand.Execute(default);

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        this.Get<AppViewModel>().Windows.Remove(this);
        Activated -= OnActivated;
        Closed -= OnClosed;
        MainView.Loaded -= OnLoadedAsync;
        MainView.NavigationCompleted -= OnNavigationCompletedAsync;
        MainView.Close();
        try
        {
            await _cancellationTokenSource.CancelAsync();
        }
        finally
        {
            _cancellationTokenSource.Dispose();
        }

        var localToken = this.Get<IBiliTokenResolver>().GetToken();
        if (localToken != null)
        {
            this.Get<AppViewModel>().RestartCommand.Execute(default);
        }
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            await MainView.EnsureCoreWebView2Async();
            if (!_isClosed)
            {
                MainView.CoreWebView2.Navigate("https://passport.bilibili.com/login");
            }
        }
        catch (Exception) when (_isClosed)
        {
            // Closing the WebView can interrupt initialization.
        }
    }

    private async void OnNavigationCompletedAsync(WebView2 sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_isClosed)
        {
            return;
        }

        var token = _cancellationTokenSource.Token;
        try
        {
            if (sender.CoreWebView2.Source.StartsWith("https://passport.bilibili.com"))
            {
                await sender.CoreWebView2.ExecuteScriptAsync("document.querySelector('.international-footer').remove();document.querySelector('.top-header').remove()");
            }
            else if (sender.CoreWebView2.Source.StartsWith("https://www.bilibili.com"))
            {
                var cookies = await sender.CoreWebView2.CookieManager.GetCookiesAsync(sender.Source.AbsoluteUri);
                if (_isClosed)
                {
                    return;
                }

                var cookieDict = cookies.Select(p => (p.Name, p.Value)).ToDictionary();
                if (cookieDict.Count > 0)
                {
                    await this.Get<IAuthenticationService>().SignInAsync(new Richasy.BiliKernel.Bili.AuthorizeExecutionSettings { Cookies = cookieDict }, token);
                    if (!_isClosed)
                    {
                        Close();
                    }
                }
            }
        }
        catch (Exception) when (_isClosed)
        {
            // In-flight WebView/sign-in calls may finish after the window closes.
        }
    }
}
