// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// Checks of what is typed into the sign-in form. They run when a field is left and before anything is sent.
/// </summary>
public sealed partial class StartupPageViewModel
{
    // The official web sign-in asks this of a mainland number: 11 digits, 1 first, then 3 to 9.
    private const int MainlandDialingCode = 86;
    private const int MainlandPhoneLength = 11;

    // For every other region the official page takes any digits. The bounds here only keep out what cannot be a
    // number at all: no numbering plan has subscriber numbers under 4 digits, and E.164 allows 15 at most, which
    // is also where the official page stops taking input.
    private const int MinPhoneLength = 4;
    private const int MaxPhoneLength = 15;

    private const int SmsCodeLength = 6;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccountError))]
    private string _accountError = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPasswordError))]
    private string _passwordError = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPhoneNumberError))]
    private string _phoneNumberError = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSmsCodeError))]
    private string _smsCodeError = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVerificationCodeError))]
    private string _verificationCodeError = string.Empty;

    /// <summary>
    /// The most digits a phone number can have.
    /// </summary>
    public static int PhoneNumberMaxLength => MaxPhoneLength;

    /// <summary>
    /// The number of digits of an SMS code.
    /// </summary>
    public static int SmsCodeMaxLength => SmsCodeLength;

    /// <summary>
    /// Whether the account field is marked as wrong.
    /// </summary>
    public bool HasAccountError => AccountError.Length > 0;

    /// <summary>
    /// Whether the password field is marked as wrong.
    /// </summary>
    public bool HasPasswordError => PasswordError.Length > 0;

    /// <summary>
    /// Whether the phone number field is marked as wrong.
    /// </summary>
    public bool HasPhoneNumberError => PhoneNumberError.Length > 0;

    /// <summary>
    /// Whether the SMS code field is marked as wrong.
    /// </summary>
    public bool HasSmsCodeError => SmsCodeError.Length > 0;

    /// <summary>
    /// Whether the code field of the phone verification step is marked as wrong.
    /// </summary>
    public bool HasVerificationCodeError => VerificationCodeError.Length > 0;

    /// <summary>
    /// Check a field the user has just left. An empty one is left alone; it is only asked for on submit.
    /// </summary>
    /// <param name="field">The field that lost the focus.</param>
    public void CheckFieldOnLeave(StartupSignInField field)
    {
        // The fields also lose the focus when a request disables them. They were checked before it started.
        if (IsSignInBusy)
        {
            return;
        }

        switch (field)
        {
            case StartupSignInField.Account when Account.Trim().Length > 0:
                AccountError = Describe(CheckAccount(Account));
                break;
            case StartupSignInField.PhoneNumber when CleanPhoneNumber(PhoneNumber).Length > 0:
                PhoneNumberError = Describe(CheckPhoneNumber(PhoneNumber, SelectedCountry));
                break;
            case StartupSignInField.SmsCode when SmsCode.Trim().Length > 0:
                SmsCodeError = Describe(CheckSmsCode(SmsCode));
                break;
            default:
                break;
        }
    }

    // Spaces and dashes are how people group digits; the service wants digits only.
    private static string CleanPhoneNumber(string value)
        => new(value.Where(ch => !char.IsWhiteSpace(ch) && ch != '-').ToArray());

    // The account is a phone number or an email address. Which one it is decides the check.
    private static StringNames? CheckAccount(string value)
    {
        var account = value.Trim();
        if (account.Length == 0)
        {
            return StringNames.SignInAccountRequired;
        }

        if (account.Contains('@'))
        {
            return IsEmailAddress(account) ? null : StringNames.SignInErrorInvalidEmail;
        }

        if (!account.All(char.IsAsciiDigit))
        {
            return StringNames.SignInErrorAccountFormat;
        }

        // There is no region to go by here, so only the bounds every number has to meet apply.
        return account.Length is >= MinPhoneLength and <= MaxPhoneLength ? null : StringNames.SignInErrorInvalidPhoneNumber;
    }

    // Something before the @, and after it a domain with a dot inside. Anything stricter keeps real addresses out.
    private static bool IsEmailAddress(string value)
    {
        var at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@') || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var domain = value.AsSpan(at + 1);
        var dot = domain.LastIndexOf('.');
        return dot > 0 && dot < domain.Length - 1;
    }

    private static StringNames? CheckPassword(string value)
        => value.Length == 0 ? StringNames.SignInPasswordRequired : null;

    private static StringNames? CheckPhoneNumber(string value, StartupCountryItem? country)
    {
        var phoneNumber = CleanPhoneNumber(value);
        if (phoneNumber.Length == 0)
        {
            return StringNames.SignInPhoneRequired;
        }

        if (!phoneNumber.All(char.IsAsciiDigit))
        {
            return StringNames.SignInErrorInvalidPhoneNumber;
        }

        if (country?.Country.DialingCode == MainlandDialingCode)
        {
            if (phoneNumber.Length != MainlandPhoneLength)
            {
                return StringNames.SignInErrorMainlandPhoneLength;
            }

            return phoneNumber[0] == '1' && phoneNumber[1] is >= '3' and <= '9' ? null : StringNames.SignInErrorInvalidPhoneNumber;
        }

        return phoneNumber.Length is >= MinPhoneLength and <= MaxPhoneLength ? null : StringNames.SignInErrorInvalidPhoneNumber;
    }

    private static StringNames? CheckSmsCode(string value)
    {
        var code = value.Trim();
        if (code.Length == 0)
        {
            return StringNames.SignInSmsCodeRequired;
        }

        return code.Length == SmsCodeLength && code.All(char.IsAsciiDigit) ? null : StringNames.SignInErrorSmsCodeLength;
    }

    // The official page only asks that the code of the verification step is not empty.
    private static StringNames? CheckVerificationCode(string value)
        => value.Trim().Length == 0 ? StringNames.SignInSmsCodeRequired : null;

    private static string Describe(StringNames? error)
        => error is { } name ? ResourceToolkit.GetLocalizedString(name) : string.Empty;

    // While the user types, an error that is showing stays for as long as it is still true and goes the moment it
    // is not. Typing never brings up a new one; that waits until the field is left or the form is submitted.
    private static string KeepWhileTrue(string shown, StringNames? error)
        => shown.Length > 0 && string.Equals(shown, Describe(error), StringComparison.Ordinal) ? shown : string.Empty;

    partial void OnAccountChanged(string value)
        => AccountError = KeepWhileTrue(AccountError, CheckAccount(value));

    partial void OnPasswordChanged(string value)
        => PasswordError = KeepWhileTrue(PasswordError, CheckPassword(value));

    partial void OnPhoneNumberChanged(string value)
        => PhoneNumberError = KeepWhileTrue(PhoneNumberError, CheckPhoneNumber(value, SelectedCountry));

    // What a number has to look like depends on the region.
    partial void OnSelectedCountryChanged(StartupCountryItem? value)
        => PhoneNumberError = KeepWhileTrue(PhoneNumberError, CheckPhoneNumber(PhoneNumber, value));

    partial void OnSmsCodeChanged(string value)
        => SmsCodeError = KeepWhileTrue(SmsCodeError, CheckSmsCode(value));

    partial void OnVerificationCodeChanged(string value)
        => VerificationCodeError = KeepWhileTrue(VerificationCodeError, CheckVerificationCode(value));

    // Going to the other step empties the fields; what was wrong with them goes too.
    partial void OnIsVerificationStepChanged(bool value)
        => ClearFieldErrors();

    // Both fields are checked at once, so one submit shows everything that is missing.
    private bool CheckPasswordForm()
    {
        var account = CheckAccount(Account);
        var password = CheckPassword(Password);
        if (account is null && password is null)
        {
            return true;
        }

        ClearSignInMessage();
        AccountError = Describe(account);
        PasswordError = Describe(password);
        SignInFocusRequested?.Invoke(this, account is not null ? StartupSignInField.Account : StartupSignInField.Password);
        return false;
    }

    // Say what is wrong under the field itself and take the user there. Nothing is sent.
    private bool ReportFieldError(StartupSignInField field, StringNames? error)
    {
        if (error is null)
        {
            return true;
        }

        // Whatever the form said before is about an earlier try.
        ClearSignInMessage();
        var text = Describe(error);
        switch (field)
        {
            case StartupSignInField.PhoneNumber:
                PhoneNumberError = text;
                break;
            case StartupSignInField.SmsCode:
                SmsCodeError = text;
                break;
            case StartupSignInField.VerificationCode:
                VerificationCodeError = text;
                break;
            default:
                break;
        }

        SignInFocusRequested?.Invoke(this, field);
        return false;
    }

    private void ClearFieldErrors()
    {
        AccountError = string.Empty;
        PasswordError = string.Empty;
        PhoneNumberError = string.Empty;
        SmsCodeError = string.Empty;
        VerificationCodeError = string.Empty;
    }
}
