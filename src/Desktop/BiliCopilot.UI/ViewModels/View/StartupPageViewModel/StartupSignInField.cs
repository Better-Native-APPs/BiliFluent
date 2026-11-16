// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// Parts of the sign-in form that can receive keyboard focus on request.
/// </summary>
public enum StartupSignInField
{
    /// <summary>
    /// Account box of password sign-in.
    /// </summary>
    Account,

    /// <summary>
    /// Password box.
    /// </summary>
    Password,

    /// <summary>
    /// Phone number box of SMS sign-in.
    /// </summary>
    PhoneNumber,

    /// <summary>
    /// SMS code box of SMS sign-in.
    /// </summary>
    SmsCode,

    /// <summary>
    /// The button that sends the phone verification code.
    /// </summary>
    VerificationSend,

    /// <summary>
    /// SMS code box of phone verification.
    /// </summary>
    VerificationCode,
}

/// <summary>
/// What the action next to a sign-in message does.
/// </summary>
internal enum StartupSignInMessageAction
{
    None,
    WebSignIn,
    ReloadCountries,
}
