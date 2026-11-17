// Copyright (c) Bili Copilot. All rights reserved.

using System.Text.Json;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Richasy.BiliKernel.Models.Authorization;
using Windows.Globalization;
using Windows.Graphics;
using WinUIEx;

namespace BiliCopilot.UI.Forms;

/// <summary>
/// A small window that shows one GeeTest challenge and hands the answer back to the sign-in form.
/// </summary>
/// <remarks>
/// The view only ever loads the page shipped in <c>Assets/Captcha</c> plus the GeeTest hosts that page needs.
/// Account data never enters the view: the page receives the challenge identifiers and nothing else.
/// </remarks>
public sealed partial class CaptchaWindow : WindowEx
{
    private const string PageHost = "captcha.bilifluent.example";
    private const string PageUri = "https://" + PageHost + "/index.html";
    private const string NoticeActivity = "SignInCaptchaNotice";
    private const int MaxMessageLength = 2048;
    private const int MaxParameterLength = 128;
    private const int MaxAnswerLength = 256;
    private const int MaxErrorCodeLength = 32;

    // GeeTest's published allow-list for its v3 web client.
    private static readonly string[] _challengeHostSuffixes = [".geetest.com", ".geevisit.com", ".gsensebot.com"];
    private static readonly TimeSpan _loadTimeout = TimeSpan.FromSeconds(30);
    private static CaptchaWindow? _current;

    private readonly ILogger<CaptchaWindow> _logger;
    private BiliLoginCaptchaParameters _parameters;
    private Func<Task<BiliLoginCaptchaParameters?>>? _refreshParameters;
    private readonly TaskCompletionSource<BiliLoginCaptchaResult?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherQueueTimer _loadTimer;
    private CoreWebView2? _core;
    private bool _isInitializing;
    private bool _isRefreshing;
    private bool _initialParametersChecked;
    private bool _isPageActive;
    private bool _isInitSent;
    private bool _isChallengeShown;
    private bool _isClosed;

    private CaptchaWindow(BiliLoginCaptchaParameters parameters, Func<Task<BiliLoginCaptchaParameters?>> refreshParameters, Window? owner)
    {
        _parameters = parameters;
        _refreshParameters = refreshParameters;
        _logger = this.Get<ILogger<CaptchaWindow>>();
        InitializeComponent();
        IsMaximizable = false;
        IsMinimizable = false;
        IsResizable = false;

        // The sign-in windows are compact overlays (always on top), so this one has to be as well.
        AppWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay);

        Width = 420;
        Height = 600;

        Title = ResourceToolkit.GetLocalizedString(StringNames.CaptchaWindowTitle);

        if (owner?.Content is FrameworkElement ownerRoot)
        {
            RootGrid.RequestedTheme = ownerRoot.ActualTheme;
        }

        MoveOver(owner);
        this.SetIcon("Assets/logo.ico");
        this.ExtendIntoTitleBar(TitleBar);
        this.Get<AppViewModel>().Windows.Add(this);

        _loadTimer = DispatcherQueue.CreateTimer();
        _loadTimer.Interval = _loadTimeout;
        _loadTimer.IsRepeating = false;
        _loadTimer.Tick += OnLoadTimerTick;

        Closed += OnClosed;
    }

    /// <summary>
    /// Show the challenge and wait for the user.
    /// </summary>
    /// <param name="owner">The window the challenge belongs to; the new window is centered over it.</param>
    /// <param name="parameters">Challenge parameters issued by the login service.</param>
    /// <param name="refreshParameters">Requests another challenge on retry; a failed request closes the window.</param>
    /// <param name="cancellationToken">Closes the window when canceled.</param>
    /// <returns>The answer, or <c>null</c> when the window was closed without one.</returns>
    public static async Task<BiliLoginCaptchaResult?> RequestAsync(Window? owner, BiliLoginCaptchaParameters parameters, Func<Task<BiliLoginCaptchaParameters?>> refreshParameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(refreshParameters);
        cancellationToken.ThrowIfCancellationRequested();

        // One challenge at a time: a newer request replaces whatever is still open.
        _current?.Close();
        var window = new CaptchaWindow(parameters, refreshParameters, owner);
        _current = window;
        var dispatcher = window.DispatcherQueue;
        using var registration = cancellationToken.Register(() => dispatcher.TryEnqueue(window.CloseIfOpen));
        window.Activate();
#pragma warning disable VSTHRD003 // The window's own completion source: it is completed by this window's UI callbacks and never waits on the caller.
        return await window._completion.Task;
#pragma warning restore VSTHRD003
    }

    /// <summary>
    /// Bring the open challenge back in front of the sign-in window, if there is one.
    /// </summary>
    /// <returns>Whether a challenge window is open.</returns>
    public static bool TryActivateCurrent()
    {
        var window = _current;
        if (window is null || window._isClosed)
        {
            return false;
        }

        window.Activate();
        return true;
    }

    private static bool IsChallengeHost(string host)
    {
        foreach (var suffix in _challengeHostSuffixes)
        {
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPageUri(string? uri)
        => string.Equals(uri, PageUri, StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedRequest(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var target))
        {
            return false;
        }

        if (target.Scheme is "data" or "blob" or "about")
        {
            return true;
        }

        return target.Scheme == Uri.UriSchemeHttps
            && (target.Host.Equals(PageHost, StringComparison.OrdinalIgnoreCase) || IsChallengeHost(target.Host));
    }

    private static bool IsAllowedFrame(string? uri)
        => Uri.TryCreate(uri, UriKind.Absolute, out var target)
            && (target.Scheme == "about" || (target.Scheme == Uri.UriSchemeHttps && IsChallengeHost(target.Host)));

    private static bool IsToken(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength)
        {
            return false;
        }

        foreach (var ch in value)
        {
            // Printable ASCII without spaces; GeeTest values are hex strings with a few separators.
            if (ch is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsErrorCode(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxErrorCodeLength)
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '_')
            {
                return false;
            }
        }

        return true;
    }

    // Returns the message as name/value pairs only when it is a flat object of short strings.
    private static Dictionary<string, string>? ReadMessage(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > MaxMessageLength)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String || fields.Count >= 4)
                {
                    return null;
                }

                var value = property.Value.GetString();
                if (value is null || value.Length > MaxAnswerLength || !fields.TryAdd(property.Name, value))
                {
                    return null;
                }
            }

            return fields;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void MoveOver(Window? owner)
    {
        if (owner is null)
        {
            this.CenterOnScreen();
            return;
        }

        var ownerPosition = owner.AppWindow.Position;
        var ownerSize = owner.AppWindow.Size;
        var size = AppWindow.Size;
        var x = ownerPosition.X + ((ownerSize.Width - size.Width) / 2);
        var y = ownerPosition.Y + ((ownerSize.Height - size.Height) / 2);
        var area = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Nearest)?.WorkArea;
        if (area is { } bounds)
        {
            x = Math.Clamp(x, bounds.X, Math.Max(bounds.X, bounds.X + bounds.Width - size.Width));
            y = Math.Clamp(y, bounds.Y, Math.Max(bounds.Y, bounds.Y + bounds.Height - size.Height));
        }

        AppWindow.Move(new PointInt32(x, y));
    }

    private void CloseIfOpen()
    {
        if (!_isClosed)
        {
            Close();
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        _refreshParameters = null;
        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }

        Closed -= OnClosed;
        _loadTimer.Stop();
        _loadTimer.Tick -= OnLoadTimerTick;
        this.Get<AppViewModel>().Windows.Remove(this);
        CaptchaView.Loaded -= OnCaptchaViewLoadedAsync;
        if (_core is { } core)
        {
            _core = null;
            core.WebMessageReceived -= OnWebMessageReceived;
            core.WebResourceRequested -= OnWebResourceRequested;
            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.FrameNavigationStarting -= OnFrameNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.PermissionRequested -= OnPermissionRequested;
            core.DownloadStarting -= OnDownloadStarting;
            core.LaunchingExternalUriScheme -= OnLaunchingExternalUriScheme;
            core.ContextMenuRequested -= OnContextMenuRequested;
            core.ProcessFailed -= OnProcessFailed;
        }

        CaptchaView.Close();

        // Closing without an answer means the user backed out.
        _completion.TrySetResult(null);
    }

    private async void OnCaptchaViewLoadedAsync(object sender, RoutedEventArgs e)
        => await InitializeViewAsync();

    private async Task InitializeViewAsync()
    {
        if (_isClosed || _isInitializing || _core is not null)
        {
            return;
        }

        if (!IsToken(_parameters.Gt, MaxParameterLength) || !IsToken(_parameters.Challenge, MaxParameterLength))
        {
            ShowError(ResourceToolkit.GetLocalizedString(StringNames.CaptchaErrorGeneric), "parameters");
            return;
        }

        _isInitializing = true;
        ShowLoading();
        try
        {
            await CaptchaView.EnsureCoreWebView2Async();
            if (_isClosed)
            {
                return;
            }

            var core = CaptchaView.CoreWebView2;
            var settings = core.Settings;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsBuiltInErrorPageEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsPinchZoomEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsSwipeNavigationEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsWebMessageEnabled = true;

            // The page is served from the package through a host name that exists only inside this view.
            core.SetVirtualHostNameToFolderMapping(
                PageHost,
                Path.Combine(Package.Current.InstalledPath, "Assets", "Captcha"),
                CoreWebView2HostResourceAccessKind.Deny);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += OnWebResourceRequested;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.FrameNavigationStarting += OnFrameNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.PermissionRequested += OnPermissionRequested;
            core.DownloadStarting += OnDownloadStarting;
            core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
            core.ContextMenuRequested += OnContextMenuRequested;
            core.ProcessFailed += OnProcessFailed;
            core.WebMessageReceived += OnWebMessageReceived;
            _core = core;
            LoadPage();
        }
        catch (Exception) when (_isClosed)
        {
            // Closing the window can interrupt initialization.
        }
        catch (Exception)
        {
            ShowError(ResourceToolkit.GetLocalizedString(StringNames.CaptchaErrorComponent), null);
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void LoadPage()
    {
        if (_isClosed || _core is not { } core)
        {
            return;
        }

        _isPageActive = true;
        _isInitSent = false;
        _initialParametersChecked = false;
        _isChallengeShown = false;
        ShowLoading();
        _loadTimer.Stop();
        _loadTimer.Start();
        core.Navigate(PageUri);
    }

    private void SendParameters()
    {
        if (_isClosed || _isInitSent || _core is not { } core)
        {
            return;
        }

        _isInitSent = true;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "init");
            writer.WriteString("gt", _parameters.Gt);
            writer.WriteString("challenge", _parameters.Challenge);
            writer.WriteString("lang", ApplicationLanguages.Languages[0].StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-cn" : "en");
            writer.WriteString("theme", RootGrid.ActualTheme == ElementTheme.Dark ? "dark" : "light");
            writer.WriteString("startText", ResourceToolkit.GetLocalizedString(StringNames.CaptchaStart));
            writer.WriteEndObject();
        }

        core.PostWebMessageAsJson(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_isClosed || !_isPageActive || !IsPageUri(args.Source))
        {
            return;
        }

        // Anything that is not one of the four expected shapes is dropped without a reaction.
        var message = ReadMessage(args.WebMessageAsJson);
        if (message is null || !message.TryGetValue("type", out var type))
        {
            return;
        }

        switch (type)
        {
            case "loaded" when message.Count == 1:
                SendParameters();
                break;
            case "ready" when message.Count == 1 && _isInitSent:
                ShowChallenge();
                break;
            case "error" when message.Count == 2 && _isInitSent
                && message.TryGetValue("code", out var code) && IsErrorCode(code):
                if (code == "error_34")
                {
                    // This is a GeeTest site-account lookup error, not a network diagnosis.
                    ShowError($"{ResourceToolkit.GetLocalizedString(StringNames.SignInErrorCaptchaFailed)} ({code})", null);
                }
                else
                {
                    ShowError(ResourceToolkit.GetLocalizedString(StringNames.CaptchaErrorGeneric), code);
                }
                break;
            case "success" when message.Count == 4 && _isChallengeShown
                && message.TryGetValue("challenge", out var challenge) && IsToken(challenge, MaxAnswerLength)
                && message.TryGetValue("validate", out var validate) && IsToken(validate, MaxAnswerLength)
                && message.TryGetValue("seccode", out var seccode) && IsToken(seccode, MaxAnswerLength):
                _completion.TrySetResult(new BiliLoginCaptchaResult(_parameters.Token, challenge, validate, seccode));
                Close();
                break;
            default:
                break;
        }
    }

    private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var target);
        if (!IsAllowedRequest(args.Request.Uri))
        {
            args.Response = sender.Environment.CreateWebResourceResponse(null, 403, "Forbidden", string.Empty);
            if (target?.Scheme is "https" or "http")
            {
                // Queries contain challenge tickets; only the resource host and path are diagnostic.
                _logger.LogWarning("Captcha resource blocked: {Host}{Path} ({ResourceContext}).", target.Host, target.AbsolutePath, args.ResourceContext);
            }
        }
        else if (!_initialParametersChecked && _isInitSent && target is not null
            && IsChallengeHost(target.Host) && target.AbsolutePath == "/get.php")
        {
            _initialParametersChecked = true;
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Captcha initialization parameters: gt matches issued value={GtMatches}, challenge matches issued value={ChallengeMatches}.",
                    ReadQueryParameter(target, "gt") == _parameters.Gt,
                    ReadQueryParameter(target, "challenge") == _parameters.Challenge);
            }
        }
    }

    private static string ReadQueryParameter(Uri uri, string name)
    {
        var prefix = name + "=";
        foreach (var field in uri.Query.TrimStart('?').Split('&'))
        {
            if (field.StartsWith(prefix, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(field[prefix.Length..]);
            }
        }

        return string.Empty;
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsPageUri(args.Uri))
        {
            args.Cancel = true;
        }
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!_isClosed && !args.IsSuccess && args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
        {
            ShowError(ResourceToolkit.GetLocalizedString(StringNames.CaptchaErrorGeneric), FormattableString.Invariant($"page_{(int)args.WebErrorStatus}"));
        }
    }

    private void OnFrameNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsAllowedFrame(args.Uri))
        {
            args.Cancel = true;
        }
    }

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
        => args.Handled = true;

    private void OnPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args)
        => args.State = CoreWebView2PermissionState.Deny;

    private void OnDownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
        => args.Cancel = true;

    private void OnLaunchingExternalUriScheme(CoreWebView2 sender, CoreWebView2LaunchingExternalUriSchemeEventArgs args)
        => args.Cancel = true;

    private void OnContextMenuRequested(CoreWebView2 sender, CoreWebView2ContextMenuRequestedEventArgs args)
        => args.Handled = true;

    private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        if (!_isClosed)
        {
            ShowError(ResourceToolkit.GetLocalizedString(StringNames.CaptchaErrorGeneric), "process");
        }
    }

    private void OnLoadTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!_isClosed && !_isChallengeShown)
        {
            ShowError(ResourceToolkit.GetLocalizedString(StringNames.CaptchaErrorTimeout), null);
        }
    }

    private void ShowLoading()
    {
        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Visible;
        CoverPanel.Visibility = Visibility.Visible;
        CaptchaView.IsTabStop = false;
    }

    private void ShowChallenge()
    {
        _loadTimer.Stop();
        _isChallengeShown = true;
        CoverPanel.Visibility = Visibility.Collapsed;
        CaptchaView.IsTabStop = true;
        CaptchaView.Focus(FocusState.Programmatic);
    }

    private void ShowError(string description, string? code)
    {
        if (_isClosed)
        {
            return;
        }

        _loadTimer.Stop();
        _isChallengeShown = false;

        // Stop listening to the page until the user asks for another try.
        _isPageActive = false;
        CaptchaView.IsTabStop = false;
        var text = code is null ? description : string.Format(System.Globalization.CultureInfo.CurrentCulture, description, code);
        ErrorDescriptionBlock.Text = text;
        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        CoverPanel.Visibility = Visibility.Visible;
        RetryButton.Focus(FocusState.Programmatic);
        if (FrameworkElementAutomationPeer.FromElement(ErrorTitleBlock) is { } peer)
        {
            peer.RaiseNotificationEvent(
                AutomationNotificationKind.ActionAborted,
                AutomationNotificationProcessing.ImportantMostRecent,
                $"{ErrorTitleBlock.Text} {text}",
                NoticeActivity);
        }
    }

    private async void OnRetryButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isClosed || _isRefreshing || _refreshParameters is not { } refreshParameters)
        {
            return;
        }

        _isRefreshing = true;
        _isPageActive = false;
        _loadTimer.Stop();
        ShowLoading();
        try
        {
            var parameters = await refreshParameters();
            if (_isClosed)
            {
                return;
            }

            if (parameters is null)
            {
                CloseIfOpen();
                return;
            }

            _parameters = parameters;
            if (_core is null)
            {
                await InitializeViewAsync();
            }
            else
            {
                LoadPage();
            }
        }
        catch (OperationCanceledException)
        {
            CloseIfOpen();
        }
        catch (Exception)
        {
            ShowError(ResourceToolkit.GetLocalizedString(StringNames.CaptchaErrorGeneric), "refresh");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
        => CloseIfOpen();

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        CloseIfOpen();
    }
}
