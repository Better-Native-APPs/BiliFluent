// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Models.Authorization;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// 启动页视图模型.
/// </summary>
public sealed partial class StartupPageViewModel
{
    private readonly ILogger<StartupPageViewModel> _logger;
    private readonly IAuthenticationService _authenticationService;
    private readonly IBiliLoginService _loginService;
    private readonly DispatcherQueue _dispatcherQueue;

    private StartupLoginAttempt? _loginAttempt;
    private bool _isExiting;

    // Sign-in form state. Sessions and contexts are opaque: they are only kept, passed back and disposed.
    private StartupLoginAttempt? _formAttempt;
    private StartupLoginAttempt? _countryAttempt;
    private BiliLoginSession? _passwordSession;
    private BiliLoginSession? _smsSession;
    private BiliLoginSmsContext? _smsContext;
    private StartupCountryItem? _smsSentCountry;
    private string _smsSentPhoneNumber = string.Empty;
    private BiliLoginSession? _verificationSession;
    private BiliLoginVerificationContext? _verificationContext;
    private BiliLoginSmsContext? _verificationSmsContext;
    private DispatcherQueueTimer? _cooldownTimer;
    private long _smsCooldownDeadline;
    private long _verificationCooldownDeadline;
    private bool _hasSentSmsCode;
    private bool _hasSentVerificationCode;
    private bool _isPageActive;
    private bool _isSuspended;
    private StartupSignInMessageAction _signInMessageAction;

    [ObservableProperty]
    private string _version;

    [ObservableProperty]
    private string _errorTip;

    [ObservableProperty]
    private bool _isQRCodeLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPasswordFormVisible))]
    [NotifyPropertyChangedFor(nameof(IsSmsFormVisible))]
    private bool _isSmsMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCredentialStep))]
    [NotifyPropertyChangedFor(nameof(IsPasswordFormVisible))]
    [NotifyPropertyChangedFor(nameof(IsSmsFormVisible))]
    private bool _isVerificationStep;

    [ObservableProperty]
    private string _account = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _phoneNumber = string.Empty;

    [ObservableProperty]
    private string _smsCode = string.Empty;

    [ObservableProperty]
    private string _verificationCode = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendSmsCode))]
    private StartupCountryItem? _selectedCountry;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectCountry))]
    private bool _isCountryLoading;

    [ObservableProperty]
    private string _countryPlaceholder = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignInIdle))]
    [NotifyPropertyChangedFor(nameof(CanSelectCountry))]
    [NotifyPropertyChangedFor(nameof(CanSendSmsCode))]
    [NotifyPropertyChangedFor(nameof(CanSendVerificationCode))]
    private bool _isSignInBusy;

    [ObservableProperty]
    private string _signInStatus = string.Empty;

    [ObservableProperty]
    private bool _isSignInMessageOpen;

    [ObservableProperty]
    private string _signInMessage = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _signInMessageSeverity = InfoBarSeverity.Error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignInMessageAction))]
    private string _signInMessageActionText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendSmsCode))]
    private int _smsCooldown;

    [ObservableProperty]
    private string _sendSmsCodeText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendVerificationCode))]
    private int _verificationCooldown;

    [ObservableProperty]
    private string _sendVerificationCodeText = string.Empty;

    /// <summary>
    /// Raised when a failure suggests the web sign-in window and the user accepts.
    /// </summary>
    public event EventHandler? WebSignInRequested;

    /// <summary>
    /// Raised when keyboard focus should move to a specific part of the sign-in form.
    /// </summary>
    public event EventHandler<StartupSignInField>? SignInFocusRequested;

    /// <summary>
    /// 二维码图片控件.
    /// </summary>
    public Image? QRCodeImage { get; private set; }

    /// <summary>
    /// Shows a human verification challenge and returns the answer, or <c>null</c> when the user dismissed it.
    /// The view provides it, because the challenge needs a window. Retry requests fresh parameters.
    /// </summary>
    public Func<BiliLoginCaptchaParameters, Func<Task<BiliLoginCaptchaParameters?>>, CancellationToken, Task<BiliLoginCaptchaResult?>>? CaptchaPresenter { get; set; }

    /// <summary>
    /// Countries and regions for SMS sign-in, common ones first.
    /// </summary>
    public ObservableCollection<StartupCountryItem> Countries { get; } = [];

    /// <summary>
    /// Whether the account or phone number step is on screen (as opposed to the phone verification step).
    /// </summary>
    public bool IsCredentialStep => !IsVerificationStep;

    /// <summary>
    /// Whether the account and password fields are on screen.
    /// </summary>
    public bool IsPasswordFormVisible => !IsVerificationStep && !IsSmsMode;

    /// <summary>
    /// Whether the phone number and SMS code fields are on screen.
    /// </summary>
    public bool IsSmsFormVisible => !IsVerificationStep && IsSmsMode;

    /// <summary>
    /// Whether no sign-in request is running, so the form accepts input.
    /// </summary>
    public bool IsSignInIdle => !IsSignInBusy;

    /// <summary>
    /// Whether the country list can be opened.
    /// </summary>
    public bool CanSelectCountry => !IsSignInBusy && !IsCountryLoading;

    /// <summary>
    /// Whether an SMS code can be requested for SMS sign-in right now.
    /// </summary>
    public bool CanSendSmsCode => !IsSignInBusy && SmsCooldown == 0;

    /// <summary>
    /// Whether an SMS code can be requested for phone verification right now.
    /// </summary>
    public bool CanSendVerificationCode => !IsSignInBusy && VerificationCooldown == 0;

    /// <summary>
    /// Whether the current message offers an action.
    /// </summary>
    public bool HasSignInMessageAction => !string.IsNullOrEmpty(SignInMessageActionText);
}
