// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Models.Authorization;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// 启动页视图模型.
/// </summary>
public sealed partial class StartupPageViewModel
{
    private const int SmsCooldownSeconds = 60;

    /// <summary>
    /// Clear the form and drop every pending request. Called when the page goes away.
    /// </summary>
    public void ResetSignInForm()
    {
        _isPageActive = false;
        CancelFormSignIn();
        var countryAttempt = _countryAttempt;
        _countryAttempt = null;
        countryAttempt?.Cancel();
        IsCountryLoading = false;
        StopCooldowns();
        ClearSignInMessage();
        IsVerificationStep = false;
        IsSmsMode = false;
        Account = string.Empty;
        Password = string.Empty;
        PhoneNumber = string.Empty;
        SmsCode = string.Empty;
        VerificationCode = string.Empty;
        ClearFieldErrors();
    }

    /// <summary>
    /// Pause every sign-in path of the page while another window (web sign-in) takes over.
    /// </summary>
    [RelayCommand]
    private void SuspendSignIn()
    {
        if (_isExiting || _isSuspended)
        {
            return;
        }

        _isSuspended = true;
        CancelQRCodePolling();
        CancelFormSignIn();
        Password = string.Empty;
    }

    /// <summary>
    /// Undo <see cref="SuspendSignIn"/> and show a fresh QR code.
    /// </summary>
    [RelayCommand]
    private async Task ResumeSignInAsync()
    {
        if (_isExiting || !_isSuspended)
        {
            return;
        }

        _isSuspended = false;
        if (_isPageActive)
        {
            await ReloadQRCodeAsync();
        }
    }

    /// <summary>
    /// Load a new QR code only when the current one is no longer being polled (expired or failed).
    /// </summary>
    [RelayCommand]
    private async Task ResumeQRCodeAsync()
    {
        if (_isExiting || _isSuspended || !_isPageActive || _loginAttempt is not null)
        {
            return;
        }

        await ReloadQRCodeAsync();
    }

    [RelayCommand]
    private async Task SignInWithPasswordAsync()
    {
        if (IsSignInBusy || IsVerificationStep)
        {
            return;
        }

        var account = Account.Trim();
        var password = Password;
        if (!CheckPasswordForm())
        {
            return;
        }

        var attempt = BeginFormOperation(StringNames.SignInStatusPreparing);
        if (attempt is null)
        {
            return;
        }

        var token = attempt.Token;
        BiliLoginSession? session = null;
        try
        {
            // A password can be submitted once per session, so every try starts a new one.
            session = _loginService.CreateSession(token);
            _passwordSession = session;
            var captcha = await RequestCaptchaAsync(attempt, () => _loginService.GetCaptchaAsync(session, token));
            if (captcha is null)
            {
                return;
            }

            SignInStatus = ResourceToolkit.GetLocalizedString(StringNames.SignInStatusSigningIn);
            BiliLoginResult result;
            try
            {
                result = await _loginService.SignInWithPasswordAsync(session, account, password, captcha, token);
            }
            finally
            {
                // The box is emptied once the password has been handed over, whatever the outcome.
                Password = string.Empty;
            }

            token.ThrowIfCancellationRequested();
            await HandleSignInResultAsync(attempt, session, result);
        }
        catch (OperationCanceledException)
        {
            // Canceled by the user or by leaving the page; the form has already been reset.
        }
        catch (HttpRequestException)
        {
            ReportFormError(attempt, StringNames.SignInErrorNetwork);
        }
        catch (Exception ex)
        {
            ReportUnexpectedFormError(attempt, ex);
        }
        finally
        {
            if (ReferenceEquals(_passwordSession, session))
            {
                _passwordSession = null;
            }

            if (session is not null && !ReferenceEquals(_verificationSession, session))
            {
                session.Dispose();
            }

            EndFormOperation(attempt);
        }
    }

    [RelayCommand]
    private async Task LoadCountriesAsync()
    {
        if (_isExiting || IsCountryLoading || Countries.Count > 0)
        {
            return;
        }

        var attempt = new StartupLoginAttempt(ReportCancellationError);
        _countryAttempt = attempt;
        IsCountryLoading = true;
        CountryPlaceholder = ResourceToolkit.GetLocalizedString(StringNames.SignInCountryLoading);
        var isLoaded = false;
        try
        {
            var response = await _loginService.GetSmsCountriesAsync(attempt.Token);
            if (!ReferenceEquals(_countryAttempt, attempt) || attempt.Token.IsCancellationRequested)
            {
                return;
            }

            if (response.IsSuccess)
            {
                // Common entries come first; an entry listed in both groups is shown once.
                var known = new HashSet<int>();
                foreach (var country in response.Common.Concat(response.Others))
                {
                    if (country is not null && known.Add(country.Id))
                    {
                        Countries.Add(new StartupCountryItem(country));
                    }
                }

                SelectedCountry = Countries.FirstOrDefault();
                isLoaded = Countries.Count > 0;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (HttpRequestException)
        {
            // Reported below together with every other reason for an empty list.
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Loading the SMS country list failed: {ExceptionType}", ex.GetType().FullName);
        }
        finally
        {
            if (ReferenceEquals(_countryAttempt, attempt))
            {
                _countryAttempt = null;
                IsCountryLoading = false;
                CountryPlaceholder = ResourceToolkit.GetLocalizedString(StringNames.SignInCountryPlaceholder);
            }

            attempt.Complete();
        }

        if (!isLoaded && IsSmsFormVisible && !IsSignInBusy)
        {
            ShowSignInMessage(StringNames.SignInCountryLoadFailed, action: StartupSignInMessageAction.ReloadCountries);
        }
    }

    [RelayCommand]
    private async Task SendSmsCodeAsync()
    {
        if (IsSignInBusy || IsVerificationStep || SmsCooldown > 0)
        {
            return;
        }

        if (SelectedCountry is null)
        {
            // The list failed to load earlier; this is one more chance before giving up.
            await LoadCountriesAsync();
        }

        if (SelectedCountry is not { } country || !TryReadPhoneNumber(out var phoneNumber))
        {
            return;
        }

        var attempt = BeginFormOperation(StringNames.SignInStatusPreparing);
        if (attempt is null)
        {
            return;
        }

        var token = attempt.Token;
        try
        {
            // The session stays for the code that follows; asking again only needs a new challenge.
            var session = _smsSession ??= _loginService.CreateSession(token);
            var captcha = await RequestCaptchaAsync(attempt, () => _loginService.GetCaptchaAsync(session, token));
            if (captcha is null)
            {
                return;
            }

            SignInStatus = ResourceToolkit.GetLocalizedString(StringNames.SignInStatusSendingSms);
            var response = await _loginService.SendSmsCodeAsync(session, country.Country, phoneNumber, captcha, token);
            token.ThrowIfCancellationRequested();
            if (response.IsSuccess && response.Context is not null)
            {
                _smsContext = response.Context;
                _smsSentCountry = country;
                _smsSentPhoneNumber = phoneNumber;
                _hasSentSmsCode = true;
                StartCooldown(ref _smsCooldownDeadline);
                ShowSignInMessage(StringNames.SignInSmsCodeSent, InfoBarSeverity.Success);
                SignInFocusRequested?.Invoke(this, StartupSignInField.SmsCode);
            }
            else
            {
                if (response.Failure?.Kind == BiliLoginErrorKind.SessionExpired)
                {
                    ReleaseSmsSession();
                }

                ShowSignInFailure(response.Failure);
            }
        }
        catch (OperationCanceledException)
        {
            // Canceled by the user or by leaving the page; the form has already been reset.
        }
        catch (HttpRequestException)
        {
            ReportFormError(attempt, StringNames.SignInErrorNetwork);
        }
        catch (Exception ex)
        {
            ReportUnexpectedFormError(attempt, ex);
        }
        finally
        {
            EndFormOperation(attempt);
        }
    }

    [RelayCommand]
    private async Task SignInWithSmsAsync()
    {
        if (IsSignInBusy || IsVerificationStep)
        {
            return;
        }

        if (!TryReadPhoneNumber(out var phoneNumber))
        {
            return;
        }

        var code = SmsCode.Trim();
        var session = _smsSession;
        var context = _smsContext;
        var country = _smsSentCountry;
        if (session is null || context is null || country is null)
        {
            ShowSignInMessage(StringNames.SignInSmsCodeNotRequested, InfoBarSeverity.Warning);
            return;
        }

        if (!ReferenceEquals(SelectedCountry, country) || !string.Equals(phoneNumber, _smsSentPhoneNumber, StringComparison.Ordinal))
        {
            ShowSignInMessage(StringNames.SignInPhoneChanged, InfoBarSeverity.Warning);
            return;
        }

        if (!ReportFieldError(StartupSignInField.SmsCode, CheckSmsCode(code)))
        {
            return;
        }

        var attempt = BeginFormOperation(StringNames.SignInStatusSigningIn);
        if (attempt is null)
        {
            return;
        }

        var token = attempt.Token;
        try
        {
            var result = await _loginService.SignInWithSmsAsync(session, country.Country, phoneNumber, code, context, token);
            token.ThrowIfCancellationRequested();
            if (result.Status == BiliLoginStatus.Failed
                && result.Failure?.Kind is BiliLoginErrorKind.SmsCodeExpired or BiliLoginErrorKind.SessionExpired)
            {
                // The sent code can no longer be used; the next one needs a new challenge.
                ReleaseSmsSession();
                SmsCode = string.Empty;
            }

            await HandleSignInResultAsync(attempt, session, result);
        }
        catch (OperationCanceledException)
        {
            // Canceled by the user or by leaving the page; the form has already been reset.
        }
        catch (HttpRequestException)
        {
            ReportFormError(attempt, StringNames.SignInErrorNetwork);
        }
        catch (Exception ex)
        {
            ReportUnexpectedFormError(attempt, ex);
        }
        finally
        {
            EndFormOperation(attempt);
        }
    }

    [RelayCommand]
    private async Task SendVerificationCodeAsync()
    {
        var context = _verificationContext;
        if (IsSignInBusy || !IsVerificationStep || context is null || VerificationCooldown > 0)
        {
            return;
        }

        var attempt = BeginFormOperation(StringNames.SignInStatusPreparing);
        if (attempt is null)
        {
            return;
        }

        var token = attempt.Token;
        try
        {
            var captcha = await RequestCaptchaAsync(attempt, () => _loginService.GetVerificationCaptchaAsync(context, token));
            if (captcha is null)
            {
                return;
            }

            SignInStatus = ResourceToolkit.GetLocalizedString(StringNames.SignInStatusSendingSms);
            var response = await _loginService.SendVerificationSmsCodeAsync(context, captcha, token);
            token.ThrowIfCancellationRequested();
            if (response.IsSuccess && response.Context is not null)
            {
                _verificationSmsContext = response.Context;
                _hasSentVerificationCode = true;
                StartCooldown(ref _verificationCooldownDeadline);
                ShowSignInMessage(StringNames.SignInSmsCodeSent, InfoBarSeverity.Success);
                SignInFocusRequested?.Invoke(this, StartupSignInField.VerificationCode);
            }
            else
            {
                ShowVerificationFailure(response.Failure);
            }
        }
        catch (OperationCanceledException)
        {
            // Canceled by the user or by leaving the page; the form has already been reset.
        }
        catch (HttpRequestException)
        {
            ReportFormError(attempt, StringNames.SignInErrorNetwork);
        }
        catch (Exception ex)
        {
            ReportUnexpectedFormError(attempt, ex);
        }
        finally
        {
            EndFormOperation(attempt);
        }
    }

    [RelayCommand]
    private async Task CompleteVerificationAsync()
    {
        var context = _verificationContext;
        var session = _verificationSession;
        if (IsSignInBusy || !IsVerificationStep || context is null || session is null)
        {
            return;
        }

        var smsContext = _verificationSmsContext;
        if (smsContext is null)
        {
            ShowSignInMessage(StringNames.SignInSmsCodeNotRequested, InfoBarSeverity.Warning);
            SignInFocusRequested?.Invoke(this, StartupSignInField.VerificationSend);
            return;
        }

        var code = VerificationCode.Trim();
        if (!ReportFieldError(StartupSignInField.VerificationCode, CheckVerificationCode(code)))
        {
            return;
        }

        var attempt = BeginFormOperation(StringNames.SignInStatusVerifying);
        if (attempt is null)
        {
            return;
        }

        var token = attempt.Token;
        try
        {
            var result = await _loginService.CompleteVerificationAsync(context, code, smsContext, token);
            token.ThrowIfCancellationRequested();
            if (result.Status == BiliLoginStatus.Success)
            {
                await CompleteFormSignInAsync(attempt);
            }
            else if (result.Status == BiliLoginStatus.Failed)
            {
                ShowVerificationFailure(result.Failure);
            }
            else
            {
                // A second round of verification is not something this form can walk through.
                ShowSignInMessage(StringNames.SignInErrorUnsupportedVerification, action: StartupSignInMessageAction.WebSignIn);
            }
        }
        catch (OperationCanceledException)
        {
            // Canceled by the user or by leaving the page; the form has already been reset.
        }
        catch (HttpRequestException)
        {
            ReportFormError(attempt, StringNames.SignInErrorNetwork);
        }
        catch (Exception ex)
        {
            ReportUnexpectedFormError(attempt, ex);
        }
        finally
        {
            EndFormOperation(attempt);
        }
    }

    /// <summary>
    /// Abort the request that is running. The attempt's session goes with it.
    /// </summary>
    [RelayCommand]
    private void CancelSignIn()
    {
        if (_isExiting || _formAttempt is null)
        {
            return;
        }

        var wasVerifying = IsVerificationStep;
        CancelFormSignIn();
        ClearSignInMessage();
        if (wasVerifying)
        {
            SignInFocusRequested?.Invoke(this, IsSmsMode ? StartupSignInField.PhoneNumber : StartupSignInField.Account);
        }

        // Canceling while the sign-in was being finished leaves the QR code stopped; bring it back.
        ResumeQRCodeCommand.Execute(default);
    }

    /// <summary>
    /// Go back from phone verification to the account or phone number step.
    /// </summary>
    [RelayCommand]
    private void LeaveVerification()
    {
        if (_isExiting || !IsVerificationStep)
        {
            return;
        }

        CancelFormSignIn();
        ClearSignInMessage();
        SignInFocusRequested?.Invoke(this, IsSmsMode ? StartupSignInField.PhoneNumber : StartupSignInField.Account);
    }

    [RelayCommand]
    private async Task RunSignInMessageActionAsync()
    {
        switch (_signInMessageAction)
        {
            case StartupSignInMessageAction.WebSignIn:
                WebSignInRequested?.Invoke(this, EventArgs.Empty);
                break;
            case StartupSignInMessageAction.ReloadCountries:
                ClearSignInMessage();
                await LoadCountriesAsync();
                break;
            default:
                break;
        }
    }

    partial void OnIsSmsModeChanged(bool value)
    {
        // Each sign-in method has its own session; nothing carries over.
        CancelFormSignIn();
        ClearSignInMessage();
        Password = string.Empty;
        SmsCode = string.Empty;
        ClearFieldErrors();
        if (value && _isPageActive)
        {
            LoadCountriesCommand.Execute(default);
        }
    }

    private static int GetRemainingSeconds(long deadline, long now)
        => deadline <= now ? 0 : (int)((deadline - now + 999) / 1000);

    private void InitializeSignInForm()
    {
        _isPageActive = true;
        CountryPlaceholder = ResourceToolkit.GetLocalizedString(StringNames.SignInCountryPlaceholder);
        UpdateCooldowns();
        if (IsSmsMode)
        {
            LoadCountriesCommand.Execute(default);
        }
    }

    private StartupLoginAttempt? BeginFormOperation(StringNames status)
    {
        if (_isExiting || _isSuspended || !_isPageActive || IsSignInBusy)
        {
            return null;
        }

        var attempt = new StartupLoginAttempt(ReportCancellationError);
        _formAttempt = attempt;
        ClearSignInMessage();
        SignInStatus = ResourceToolkit.GetLocalizedString(status);
        IsSignInBusy = true;
        return attempt;
    }

    private void EndFormOperation(StartupLoginAttempt attempt)
    {
        if (ReferenceEquals(_formAttempt, attempt))
        {
            _formAttempt = null;
            IsSignInBusy = false;
            SignInStatus = string.Empty;
        }

        attempt.Complete();
    }

    private bool IsCurrentFormAttempt(StartupLoginAttempt attempt)
        => !_isExiting && ReferenceEquals(_formAttempt, attempt) && !attempt.Token.IsCancellationRequested;

    private void ReportCancellationError(Exception exception)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Canceling a sign-in request failed: {ExceptionType}", exception.GetType().FullName);
        }
    }

    // Fetches challenge parameters and lets the user solve them. Null means there is nothing to submit:
    // either a failure has been shown, or the user closed the challenge.
    private async Task<BiliLoginCaptchaResult?> RequestCaptchaAsync(StartupLoginAttempt attempt, Func<Task<BiliLoginCaptchaResponse>> getParameters)
    {
        var token = attempt.Token;
        SignInStatus = ResourceToolkit.GetLocalizedString(StringNames.SignInStatusPreparing);
        var parameters = await ReadParametersAsync();
        if (parameters is null)
        {
            return null;
        }

        var presenter = CaptchaPresenter;
        if (presenter is null)
        {
            ShowSignInMessage(StringNames.SignInErrorUnexpected);
            return null;
        }

        SignInStatus = ResourceToolkit.GetLocalizedString(StringNames.SignInStatusCaptcha);
        var result = await presenter(parameters, ReadParametersAsync, token);
        token.ThrowIfCancellationRequested();
        return result;

        async Task<BiliLoginCaptchaParameters?> ReadParametersAsync()
        {
            var response = await getParameters();
            token.ThrowIfCancellationRequested();
            if (response.IsSuccess && response.Parameters is not null)
            {
                return response.Parameters;
            }

            if (IsVerificationStep)
            {
                ShowVerificationFailure(response.Failure);
            }
            else
            {
                if (response.Failure?.Kind == BiliLoginErrorKind.SessionExpired)
                {
                    ReleaseSmsSession();
                }

                ShowSignInFailure(response.Failure);
            }

            return null;
        }
    }

    private async Task HandleSignInResultAsync(StartupLoginAttempt attempt, BiliLoginSession session, BiliLoginResult result)
    {
        switch (result.Status)
        {
            case BiliLoginStatus.Success:
                await CompleteFormSignInAsync(attempt);
                break;
            case BiliLoginStatus.RequiresVerification when result.VerificationContext is not null:
                EnterVerification(session, result.VerificationContext);
                break;
            case BiliLoginStatus.RequiresVerification:
                ShowSignInMessage(StringNames.SignInErrorUnsupportedVerification, action: StartupSignInMessageAction.WebSignIn);
                break;
            default:
                ShowSignInFailure(result.Failure);
                break;
        }
    }

    // The service has already stored the session, so this ends the same way a scanned QR code does.
    private async Task CompleteFormSignInAsync(StartupLoginAttempt attempt)
    {
        // Stop the QR code path first: only one of the two may finish the sign-in.
        CancelQRCodePolling();
        SignInStatus = ResourceToolkit.GetLocalizedString(StringNames.SignInStatusFinishing);
        var isSignedIn = await CheckAuthorizeStatusAsync(attempt.Token);
        if (!IsCurrentFormAttempt(attempt))
        {
            return;
        }

        if (isSignedIn)
        {
            await ExitAsync(true);
            return;
        }

        _logger.LogWarning("The sign-in form finished, but no usable authorization was found.");
        ReleaseSignInSessions();
        IsVerificationStep = false;
        ShowSignInMessage(StringNames.SignInErrorSessionImportFailed, action: StartupSignInMessageAction.WebSignIn);
        ReloadQRCodeCommand.Execute(default);
    }

    private void EnterVerification(BiliLoginSession session, BiliLoginVerificationContext context)
    {
        // Verification continues on the session that asked for it, so that session is kept alive.
        if (ReferenceEquals(_smsSession, session))
        {
            _smsSession = null;
            _smsContext = null;
            _smsSentCountry = null;
            _smsSentPhoneNumber = string.Empty;
        }

        _verificationSession = session;
        _verificationContext = context;
        _verificationSmsContext = null;
        _hasSentVerificationCode = false;
        _verificationCooldownDeadline = 0;
        UpdateCooldowns();
        SmsCode = string.Empty;
        VerificationCode = string.Empty;
        ClearSignInMessage();
        IsVerificationStep = true;
        SignInFocusRequested?.Invoke(this, StartupSignInField.VerificationSend);
    }

    private void ShowVerificationFailure(BiliLoginFailure? failure)
    {
        if (failure?.Kind == BiliLoginErrorKind.SessionExpired)
        {
            // Nothing more can be done with this attempt; start over from the first step.
            ReleaseSignInSessions();
            IsVerificationStep = false;
            VerificationCode = string.Empty;
        }

        ShowSignInFailure(failure, alwaysOfferWebSignIn: true);
    }

    private void CancelQRCodePolling()
    {
        var attempt = _loginAttempt;
        _loginAttempt = null;
        attempt?.Cancel();

        // A code that is no longer polled must not stay scannable.
        IsQRCodeLoading = true;
        ErrorTip = string.Empty;
    }

    private void CancelFormSignIn()
    {
        var attempt = _formAttempt;
        _formAttempt = null;
        attempt?.Cancel();
        ReleaseSignInSessions();
        IsSignInBusy = false;
        SignInStatus = string.Empty;
        if (IsVerificationStep)
        {
            IsVerificationStep = false;
            VerificationCode = string.Empty;
        }
    }

    private void ReleaseSignInSessions()
    {
        _passwordSession?.Dispose();
        _passwordSession = null;
        ReleaseSmsSession();
        _verificationSession?.Dispose();
        _verificationSession = null;
        _verificationContext = null;
        _verificationSmsContext = null;
    }

    private void ReleaseSmsSession()
    {
        _smsSession?.Dispose();
        _smsSession = null;
        _smsContext = null;
        _smsSentCountry = null;
        _smsSentPhoneNumber = string.Empty;
    }

    private bool TryReadPhoneNumber(out string phoneNumber)
    {
        phoneNumber = CleanPhoneNumber(PhoneNumber);
        return ReportFieldError(StartupSignInField.PhoneNumber, CheckPhoneNumber(phoneNumber, SelectedCountry));
    }

    private void StartCooldown(ref long deadline)
    {
        deadline = Environment.TickCount64 + (SmsCooldownSeconds * 1000L);
        if (_cooldownTimer is null)
        {
            _cooldownTimer = _dispatcherQueue.CreateTimer();
            _cooldownTimer.Interval = TimeSpan.FromMilliseconds(500);
            _cooldownTimer.Tick += (_, _) => UpdateCooldowns();
        }

        UpdateCooldowns();
        _cooldownTimer.Start();
    }

    private void StopCooldowns()
    {
        _smsCooldownDeadline = 0;
        _verificationCooldownDeadline = 0;
        _hasSentSmsCode = false;
        _hasSentVerificationCode = false;
        UpdateCooldowns();
    }

    private void UpdateCooldowns()
    {
        var now = Environment.TickCount64;
        SmsCooldown = GetRemainingSeconds(_smsCooldownDeadline, now);
        VerificationCooldown = GetRemainingSeconds(_verificationCooldownDeadline, now);
        SendSmsCodeText = GetSendCodeText(SmsCooldown, _hasSentSmsCode, StringNames.SignInSendSmsCode);
        SendVerificationCodeText = GetSendCodeText(VerificationCooldown, _hasSentVerificationCode, StringNames.SignInVerificationSend);
        if (SmsCooldown == 0 && VerificationCooldown == 0)
        {
            _cooldownTimer?.Stop();
        }
    }

    private string GetSendCodeText(int cooldown, bool hasSent, StringNames first)
    {
        if (cooldown > 0)
        {
            return string.Format(ResourceToolkit.GetLocalizedString(StringNames.SignInSmsCooldownTemplate), cooldown);
        }

        return ResourceToolkit.GetLocalizedString(hasSent ? StringNames.SignInResendSmsCode : first);
    }

    private void ReportFormError(StartupLoginAttempt attempt, StringNames message)
    {
        if (IsCurrentFormAttempt(attempt))
        {
            ShowSignInMessage(message);
        }
    }

    private void ReportUnexpectedFormError(StartupLoginAttempt attempt, Exception exception)
    {
        // Only the type is recorded: an unexpected message could quote what the user typed.
        _logger.LogWarning("The sign-in form failed unexpectedly: {ExceptionType}", exception.GetType().FullName);
        ReportFormError(attempt, StringNames.SignInErrorUnexpected);
    }

    private void ShowSignInFailure(BiliLoginFailure? failure, bool alwaysOfferWebSignIn = false)
    {
        var action = alwaysOfferWebSignIn || failure?.RequiresWebLogin == true
            ? StartupSignInMessageAction.WebSignIn
            : StartupSignInMessageAction.None;
        if (failure is null)
        {
            ShowSignInMessage(StringNames.SignInErrorUnknown, action: action);
            return;
        }

        var name = failure.Kind switch
        {
            BiliLoginErrorKind.InvalidCredentials => StringNames.SignInErrorInvalidCredentials,
            BiliLoginErrorKind.CaptchaFailed => StringNames.SignInErrorCaptchaFailed,
            BiliLoginErrorKind.RateLimited => StringNames.SignInErrorRateLimited,
            BiliLoginErrorKind.TimedOut => StringNames.SignInErrorTimedOut,
            BiliLoginErrorKind.InvalidParameters => StringNames.SignInErrorInvalidParameters,
            BiliLoginErrorKind.InvalidPhoneNumber => StringNames.SignInErrorInvalidPhoneNumber,
            BiliLoginErrorKind.SmsAlreadySent => StringNames.SignInErrorSmsAlreadySent,
            BiliLoginErrorKind.PhoneNumberBlocked => StringNames.SignInErrorPhoneNumberBlocked,
            BiliLoginErrorKind.InvalidSmsCode => StringNames.SignInErrorInvalidSmsCode,
            BiliLoginErrorKind.SmsCodeExpired => StringNames.SignInErrorSmsCodeExpired,
            BiliLoginErrorKind.SessionExpired => StringNames.SignInErrorSessionExpired,
            BiliLoginErrorKind.UnsupportedVerification => StringNames.SignInErrorUnsupportedVerification,
            BiliLoginErrorKind.UnsupportedCaptcha => StringNames.SignInErrorUnsupportedCaptcha,
            BiliLoginErrorKind.InvalidResponse => StringNames.SignInErrorInvalidResponse,
            BiliLoginErrorKind.SessionImportFailed => StringNames.SignInErrorSessionImportFailed,
            _ => StringNames.SignInErrorUnknown,
        };

        if (name != StringNames.SignInErrorUnknown)
        {
            ShowSignInMessage(name, action: action);
            return;
        }

        // Unrecognized failures carry their number so that it can be quoted in a bug report.
        var code = failure.Code?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? (failure.HttpStatusCode is { } status ? FormattableString.Invariant($"HTTP {status}") : null);
        if (code is null)
        {
            ShowSignInMessage(StringNames.SignInErrorUnknown, action: action);
        }
        else
        {
            ShowSignInText(
                string.Format(ResourceToolkit.GetLocalizedString(StringNames.SignInErrorUnknownWithCode), code),
                InfoBarSeverity.Error,
                action);
        }
    }

    private void ShowSignInMessage(
        StringNames message,
        InfoBarSeverity severity = InfoBarSeverity.Error,
        StartupSignInMessageAction action = StartupSignInMessageAction.None)
        => ShowSignInText(ResourceToolkit.GetLocalizedString(message), severity, action);

    private void ShowSignInText(string text, InfoBarSeverity severity, StartupSignInMessageAction action)
    {
        // Every failure of the phone verification step can be worked around on the web.
        if (IsVerificationStep && severity == InfoBarSeverity.Error && action == StartupSignInMessageAction.None)
        {
            action = StartupSignInMessageAction.WebSignIn;
        }

        _signInMessageAction = action;
        SignInMessageActionText = action switch
        {
            StartupSignInMessageAction.WebSignIn => ResourceToolkit.GetLocalizedString(StringNames.SignInSwitchToWeb),
            StartupSignInMessageAction.ReloadCountries => ResourceToolkit.GetLocalizedString(StringNames.SignInRetry),
            _ => string.Empty,
        };
        SignInMessageSeverity = severity;
        SignInMessage = text;

        // Closing first makes a message that replaces another one get announced again.
        IsSignInMessageOpen = false;
        IsSignInMessageOpen = true;
    }

    private void ClearSignInMessage()
    {
        IsSignInMessageOpen = false;
        SignInMessage = string.Empty;
        SignInMessageActionText = string.Empty;
        _signInMessageAction = StartupSignInMessageAction.None;
    }
}
