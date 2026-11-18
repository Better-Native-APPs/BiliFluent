// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.Extensions.Logging;
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
    private bool _isImporting;

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

    // While this window is open, the startup page stops its own sign-in paths; it picks them up again
    // if this window closes without a session.
    private void OnActivated(object sender, WindowActivatedEventArgs args)
        => this.Get<StartupPageViewModel>().SuspendSignInCommand.Execute(default);

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

        var startup = this.Get<StartupPageViewModel>();
        var localToken = this.Get<IBiliTokenResolver>().GetToken();
        if (localToken != null)
        {
            // Signed in: shut the startup page down for good before the app restarts.
            startup.ExitCommand.Execute(false);
            this.Get<AppViewModel>().RestartCommand.Execute(default);
        }
        else
        {
            startup.ResumeSignInCommand.Execute(default);
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

        try
        {
            var source = sender.CoreWebView2.Source;
            if (source.StartsWith("https://passport.bilibili.com"))
            {
                await sender.CoreWebView2.ExecuteScriptAsync("document.querySelector('.international-footer').remove();document.querySelector('.top-header').remove()");
            }
            else if (source.StartsWith("https://www.bilibili.com"))
            {
                await ImportSessionAsync();
            }
        }
        catch (Exception) when (_isClosed)
        {
            // In-flight WebView calls may finish after the window closes.
        }
        catch (Exception ex)
        {
            // Nothing here may reach the UI thread's unhandled-exception path.
            this.Get<ILogger<WebSignInWindow>>().LogWarning("The web sign-in page could not be processed: {ExceptionType}", ex.GetType().FullName);
        }
    }

    private async void OnImportRetryButtonClickAsync(object sender, RoutedEventArgs e)
        => await ImportSessionAsync();

    // Hands the cookies of the signed-in web page to the app. Failing is expected (the app token may not
    // arrive in time), so the outcome is shown in the window and the user can try again.
    private async Task ImportSessionAsync()
    {
        if (_isClosed || _isImporting)
        {
            return;
        }

        _isImporting = true;
        var isFailed = false;
        try
        {
            var token = _cancellationTokenSource.Token;
            var pageUri = MainView.Source?.AbsoluteUri;
            if (pageUri is null || !pageUri.StartsWith("https://www.bilibili.com"))
            {
                // A retry can come after the user has browsed elsewhere; the session cookies live here.
                pageUri = "https://www.bilibili.com/";
            }

            var cookies = await MainView.CoreWebView2.CookieManager.GetCookiesAsync(pageUri);
            if (_isClosed)
            {
                return;
            }

            var cookieDict = cookies.Select(p => (p.Name, p.Value)).ToDictionary();
            if (cookieDict.Count == 0)
            {
                return;
            }

            ImportErrorBar.IsOpen = false;
            ImportProgressBar.IsOpen = true;
            await this.Get<IAuthenticationService>().SignInAsync(new Richasy.BiliKernel.Bili.AuthorizeExecutionSettings { Cookies = cookieDict }, token);
            if (!_isClosed)
            {
                Close();
            }
        }
        catch (Exception) when (_isClosed)
        {
            // In-flight WebView/sign-in calls may finish after the window closes.
        }
        catch (Exception ex)
        {
            // Cookies stay out of the log; the exception type is enough to tell the cases apart.
            this.Get<ILogger<WebSignInWindow>>().LogWarning("Importing the web session failed: {ExceptionType}", ex.GetType().FullName);
            isFailed = true;
        }
        finally
        {
            _isImporting = false;
            if (!_isClosed)
            {
                ImportProgressBar.IsOpen = false;
                if (isFailed)
                {
                    ImportErrorBar.IsOpen = true;
                    ImportRetryButton.Focus(FocusState.Programmatic);
                }
            }
        }
    }
}
