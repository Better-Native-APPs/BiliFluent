// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

using Richasy.BiliKernel.Models.Authorization;

namespace Richasy.BiliKernel.Bili.Authorization;

/// <summary>
/// Performs native Web Passport login and imports the completed session into the existing authenticator.
/// </summary>
/// <remarks>
/// Create and dispose a separate session for each login attempt. Never log or persist captcha values,
/// SMS codes, or opaque contexts. Business failures are returned without server messages or URLs.
/// </remarks>
public interface IBiliLoginService
{
    /// <summary>Creates an isolated, in-memory login attempt. No request is sent.</summary>
    BiliLoginSession CreateSession(CancellationToken cancellationToken = default);

    /// <summary>Gets a fresh captcha for password login or SMS sending in this session.</summary>
    Task<BiliLoginCaptchaResponse> GetCaptchaAsync(BiliLoginSession session, CancellationToken cancellationToken = default);

    /// <summary>Submits a password after fetching a fresh RSA key, then imports a successful session.</summary>
    Task<BiliLoginResult> SignInWithPasswordAsync(BiliLoginSession session, string account, string password, BiliLoginCaptchaResult captcha, CancellationToken cancellationToken = default);

    /// <summary>Gets the common and other SMS country lists.</summary>
    Task<BiliLoginCountriesResponse> GetSmsCountriesAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends one SMS code. The caller controls retries and the resend countdown.</summary>
    Task<BiliLoginSmsResponse> SendSmsCodeAsync(BiliLoginSession session, BiliLoginCountry country, string phoneNumber, BiliLoginCaptchaResult captcha, CancellationToken cancellationToken = default);

    /// <summary>Submits an SMS code using the opaque context returned by <see cref="SendSmsCodeAsync"/>.</summary>
    Task<BiliLoginResult> SignInWithSmsAsync(BiliLoginSession session, BiliLoginCountry country, string phoneNumber, string smsCode, BiliLoginSmsContext smsContext, CancellationToken cancellationToken = default);

    /// <summary>Gets a captcha for the security-center context returned by a login attempt.</summary>
    Task<BiliLoginCaptchaResponse> GetVerificationCaptchaAsync(BiliLoginVerificationContext context, CancellationToken cancellationToken = default);

    /// <summary>Sends one secondary-verification SMS to the account's bound phone.</summary>
    Task<BiliLoginSmsResponse> SendVerificationSmsCodeAsync(BiliLoginVerificationContext context, BiliLoginCaptchaResult captcha, CancellationToken cancellationToken = default);

    /// <summary>Verifies the secondary SMS, exchanges cookies, and completes the existing App-token import.</summary>
    Task<BiliLoginResult> CompleteVerificationAsync(BiliLoginVerificationContext context, string smsCode, BiliLoginSmsContext smsContext, CancellationToken cancellationToken = default);
}
