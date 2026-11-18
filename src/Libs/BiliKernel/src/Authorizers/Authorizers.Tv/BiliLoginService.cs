// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

using Richasy.BiliKernel.Authorizers.TV.Core;
using Richasy.BiliKernel.Bili;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Models.Authorization;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Richasy.BiliKernel.Authorizers.TV;

/// <summary>Native Web Passport login with isolated cookies and the existing TV-token import.</summary>
public sealed class BiliLoginService : IBiliLoginService
{
    private readonly IAuthenticationService _authenticationService;
    private readonly Func<CookieContainer, HttpMessageHandler>? _handlerFactory;
    private readonly ILogger<BiliLoginService> _logger;

    /// <summary>Initializes a new instance of the <see cref="BiliLoginService"/> class.</summary>
    public BiliLoginService(IAuthenticationService authenticationService)
        : this(authenticationService, null, null)
    {
    }

    /// <summary>Initializes the login service with the application's diagnostic logger.</summary>
    public BiliLoginService(IAuthenticationService authenticationService, ILogger<BiliLoginService> logger)
        : this(authenticationService, null, logger)
    {
    }

    internal BiliLoginService(IAuthenticationService authenticationService, Func<CookieContainer, HttpMessageHandler>? handlerFactory, ILogger<BiliLoginService>? logger = null)
    {
        _authenticationService = authenticationService;
        _handlerFactory = handlerFactory;
        _logger = logger ?? NullLogger<BiliLoginService>.Instance;
    }

    /// <inheritdoc/>
    public BiliLoginSession CreateSession(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new WebLoginSession(this, _handlerFactory);
    }

    /// <inheritdoc/>
    public Task<BiliLoginCaptchaResponse> GetCaptchaAsync(BiliLoginSession session, CancellationToken cancellationToken = default)
        => UseSessionAsync(session, async attempt =>
        {
            if (attempt.PasswordSubmitted || attempt.Verification is not null)
            {
                return BiliLoginCaptchaResponse.Failed(new(BiliLoginErrorKind.SessionExpired));
            }

            attempt.Captcha = null;
            await attempt.EnsureBrowserIdentityAsync(cancellationToken).ConfigureAwait(false);
            using var response = await attempt.SendAsync(HttpMethod.Get, BiliApis.Passport.WebCaptcha + "?source=" + WebLoginProtocol.CaptchaSource, null, WebLoginProtocol.PasswordReferer, cancellationToken).ConfigureAwait(false);
            var result = await ReadCaptchaAsync(attempt, response, securityCenter: false, WebLoginProtocol.PasswordReferer, cancellationToken).ConfigureAwait(false);
            attempt.Captcha = result.Parameters;
            return result;
        }, BiliLoginCaptchaResponse.Failed, cancellationToken, "LoginCaptcha");

    /// <inheritdoc/>
    public Task<BiliLoginResult> SignInWithPasswordAsync(BiliLoginSession session, string account, string password, BiliLoginCaptchaResult captcha, CancellationToken cancellationToken = default)
        => UseSessionAsync(session, async attempt =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(account) || string.IsNullOrEmpty(password))
                {
                    return Failed(BiliLoginErrorKind.InvalidParameters);
                }

                if (attempt.PasswordSubmitted || attempt.Sms is not null || attempt.Verification is not null)
                {
                    return Failed(BiliLoginErrorKind.SessionExpired);
                }

                if (!MatchesCaptcha(attempt.Captcha, captcha))
                {
                    return Failed(BiliLoginErrorKind.CaptchaFailed);
                }

                // The hash is short-lived: fetch immediately before encryption and submission.
                var keyUri = BiliApis.Passport.PasswordEncrypt + "?_=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                using var keyResponse = await attempt.SendAsync(HttpMethod.Get, keyUri, null, WebLoginProtocol.PasswordReferer, cancellationToken).ConfigureAwait(false);
                var keyFailure = keyResponse.Failure ?? WebLoginProtocol.ReadFailure(keyResponse.Root);
                if (keyFailure is not null)
                {
                    return BiliLoginResult.Failed(keyFailure);
                }

                var data = WebLoginProtocol.GetData(keyResponse.Root);
                var hash = WebLoginProtocol.GetString(data, "hash");
                var key = WebLoginProtocol.GetString(data, "key");
                if (string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(key))
                {
                    return Failed(BiliLoginErrorKind.InvalidResponse, 0);
                }

                string encrypted;
                try
                {
                    encrypted = WebLoginPasswordEncryption.Encrypt(hash, password, key);
                }
                catch (CryptographicException)
                {
                    return Failed(BiliLoginErrorKind.InvalidParameters);
                }
                finally
                {
                    password = string.Empty;
                }

                var fields = WebLoginProtocol.CaptchaFields(captcha);
                fields["username"] = account.Trim();
                fields["password"] = encrypted;
                fields["source"] = WebLoginProtocol.LoginSource;
                fields["go_url"] = WebLoginProtocol.LoginGoUrl;
                encrypted = string.Empty;
                attempt.PasswordSubmitted = true;
                attempt.Captcha = null;
                using var response = await attempt.SendAsync(HttpMethod.Post, BiliApis.Passport.WebPasswordLogin, fields, WebLoginProtocol.PasswordReferer, cancellationToken).ConfigureAwait(false);
                return await FinishLoginAsync(attempt, response, smsLogin: false, urlCookies: false, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                password = string.Empty;
            }
        }, failure => BiliLoginResult.Failed(failure), cancellationToken, "PasswordLogin");

    /// <inheritdoc/>
    public async Task<BiliLoginCountriesResponse> GetSmsCountriesAsync(CancellationToken cancellationToken = default)
    {
        using var session = CreateSession(cancellationToken);
        return await UseSessionAsync(session, async attempt =>
        {
            using var response = await attempt.SendAsync(HttpMethod.Get, BiliApis.Passport.WebSmsCountries, null, WebLoginProtocol.SmsReferer, cancellationToken).ConfigureAwait(false);
            return response.Failure is not null ? BiliLoginCountriesResponse.Failed(response.Failure)
                : WebLoginProtocol.ParseCountries(response.Root);
        }, BiliLoginCountriesResponse.Failed, cancellationToken, "SmsCountries").ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<BiliLoginSmsResponse> SendSmsCodeAsync(BiliLoginSession session, BiliLoginCountry country, string phoneNumber, BiliLoginCaptchaResult captcha, CancellationToken cancellationToken = default)
        => UseSessionAsync(session, async attempt =>
        {
            if (!ValidPhone(country, phoneNumber))
            {
                return BiliLoginSmsResponse.Failed(new(BiliLoginErrorKind.InvalidPhoneNumber));
            }

            if (attempt.PasswordSubmitted || attempt.Verification is not null)
            {
                return BiliLoginSmsResponse.Failed(new(BiliLoginErrorKind.SessionExpired));
            }

            if (!MatchesCaptcha(attempt.Captcha, captcha))
            {
                return BiliLoginSmsResponse.Failed(new(BiliLoginErrorKind.CaptchaFailed));
            }

            var fields = WebLoginProtocol.CaptchaFields(captcha);
            // Passport's current web form uses country_code as cid, not the database id.
            fields["cid"] = country.DialingCode.ToString(CultureInfo.InvariantCulture);
            fields["tel"] = phoneNumber.Trim();
            fields["source"] = WebLoginProtocol.LoginSource;
            fields["go_url"] = WebLoginProtocol.LoginGoUrl;
            attempt.Captcha = null;
            using var response = await attempt.SendAsync(HttpMethod.Post, BiliApis.Passport.WebSmsSend, fields, WebLoginProtocol.SmsReferer, cancellationToken).ConfigureAwait(false);
            var failure = response.Failure ?? WebLoginProtocol.ReadCaptchaKey(response.Root, out _);
            if (failure is not null)
            {
                return BiliLoginSmsResponse.Failed(failure);
            }

            var key = WebLoginProtocol.GetString(WebLoginProtocol.GetData(response.Root), "captcha_key");
            attempt.Sms?.Invalidate();
            attempt.Sms = new WebSmsContext(attempt, key, country.DialingCode, phoneNumber.Trim(), null);
            return BiliLoginSmsResponse.Succeeded(attempt.Sms);
        }, BiliLoginSmsResponse.Failed, cancellationToken, "SmsSend");

    /// <inheritdoc/>
    public Task<BiliLoginResult> SignInWithSmsAsync(BiliLoginSession session, BiliLoginCountry country, string phoneNumber, string smsCode, BiliLoginSmsContext smsContext, CancellationToken cancellationToken = default)
        => UseSessionAsync(session, async attempt =>
        {
            if (!ValidPhone(country, phoneNumber))
            {
                return Failed(BiliLoginErrorKind.InvalidPhoneNumber);
            }

            if (string.IsNullOrWhiteSpace(smsCode))
            {
                return Failed(BiliLoginErrorKind.InvalidParameters);
            }

            if (smsContext is not WebSmsContext sms || !ReferenceEquals(attempt.Sms, sms) || !ReferenceEquals(sms.Session, attempt)
                || sms.Verification is not null || attempt.Verification is not null || string.IsNullOrEmpty(sms.CaptchaKey))
            {
                return Failed(BiliLoginErrorKind.SessionExpired);
            }

            if (sms.CountryCode != country.DialingCode || !sms.PhoneNumber.Equals(phoneNumber.Trim(), StringComparison.Ordinal))
            {
                return Failed(BiliLoginErrorKind.InvalidParameters);
            }

            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["cid"] = country.DialingCode.ToString(CultureInfo.InvariantCulture),
                ["tel"] = phoneNumber.Trim(),
                ["code"] = smsCode.Trim(),
                ["captcha_key"] = sms.CaptchaKey,
                ["source"] = WebLoginProtocol.LoginSource,
                ["go_url"] = WebLoginProtocol.LoginGoUrl,
            };
            smsCode = string.Empty;
            using var response = await attempt.SendAsync(HttpMethod.Post, BiliApis.Passport.WebSmsLogin, fields, WebLoginProtocol.SmsReferer, cancellationToken).ConfigureAwait(false);
            return await FinishLoginAsync(attempt, response, smsLogin: true, urlCookies: true, cancellationToken).ConfigureAwait(false);
        }, failure => BiliLoginResult.Failed(failure), cancellationToken, "SmsLogin");

    /// <inheritdoc/>
    public Task<BiliLoginCaptchaResponse> GetVerificationCaptchaAsync(BiliLoginVerificationContext context, CancellationToken cancellationToken = default)
        => UseSessionAsync((context as WebVerificationContext)?.Session, async attempt =>
        {
            if (context is not WebVerificationContext verification || !MatchesVerification(attempt, verification))
            {
                return BiliLoginCaptchaResponse.Failed(WebFallback(new(BiliLoginErrorKind.SessionExpired)));
            }

            verification.Captcha = null;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source"] = string.IsNullOrEmpty(verification.Source) ? WebLoginProtocol.CaptchaSource : verification.Source,
            };
            using var response = await attempt.SendAsync(HttpMethod.Post, BiliApis.Passport.VerificationCaptcha, fields, verification.Referer, cancellationToken).ConfigureAwait(false);
            var result = await ReadCaptchaAsync(attempt, response, securityCenter: true, verification.Referer, cancellationToken).ConfigureAwait(false);
            if (result.Failure is not null)
            {
                result = BiliLoginCaptchaResponse.Failed(WebFallback(result.Failure));
            }
            verification.Captcha = result.Parameters;
            return result;
        }, failure => BiliLoginCaptchaResponse.Failed(WebFallback(failure)), cancellationToken, "VerificationCaptcha");

    /// <inheritdoc/>
    public Task<BiliLoginSmsResponse> SendVerificationSmsCodeAsync(BiliLoginVerificationContext context, BiliLoginCaptchaResult captcha, CancellationToken cancellationToken = default)
        => UseSessionAsync((context as WebVerificationContext)?.Session, async attempt =>
        {
            if (context is not WebVerificationContext verification || !MatchesVerification(attempt, verification))
            {
                return BiliLoginSmsResponse.Failed(WebFallback(new(BiliLoginErrorKind.SessionExpired)));
            }

            if (!MatchesCaptcha(verification.Captcha, captcha))
            {
                return BiliLoginSmsResponse.Failed(WebFallback(new(BiliLoginErrorKind.CaptchaFailed)));
            }

            var fields = WebLoginProtocol.VerificationSmsFields(verification, captcha);
            verification.Captcha = null;
            using var response = await attempt.SendAsync(HttpMethod.Post, BiliApis.Passport.VerificationSmsSend, fields, verification.Referer, cancellationToken).ConfigureAwait(false);
            var failure = response.Failure ?? WebLoginProtocol.ReadCaptchaKey(response.Root, out _);
            if (failure is not null)
            {
                return BiliLoginSmsResponse.Failed(WebFallback(failure));
            }

            var key = WebLoginProtocol.GetString(WebLoginProtocol.GetData(response.Root), "captcha_key");
            attempt.Sms?.Invalidate();
            attempt.Sms = new WebSmsContext(attempt, key, 0, string.Empty, verification);
            return BiliLoginSmsResponse.Succeeded(attempt.Sms);
        }, failure => BiliLoginSmsResponse.Failed(WebFallback(failure)), cancellationToken, "VerificationSmsSend");

    /// <inheritdoc/>
    public Task<BiliLoginResult> CompleteVerificationAsync(BiliLoginVerificationContext context, string smsCode, BiliLoginSmsContext smsContext, CancellationToken cancellationToken = default)
        => UseSessionAsync((context as WebVerificationContext)?.Session, async attempt =>
        {
            if (context is not WebVerificationContext verification || !MatchesVerification(attempt, verification)
                || smsContext is not WebSmsContext sms || !ReferenceEquals(attempt.Sms, sms)
                || !ReferenceEquals(sms.Verification, verification) || string.IsNullOrEmpty(sms.CaptchaKey))
            {
                return BiliLoginResult.Failed(WebFallback(new(BiliLoginErrorKind.SessionExpired)));
            }

            if (string.IsNullOrWhiteSpace(smsCode))
            {
                return BiliLoginResult.Failed(WebFallback(new(BiliLoginErrorKind.InvalidParameters)));
            }

            var fields = WebLoginProtocol.VerificationSubmitFields(verification, sms, smsCode.Trim());
            smsCode = string.Empty;
            using var verificationResponse = await attempt.SendAsync(HttpMethod.Post, WebLoginProtocol.VerificationSubmitEndpoint(verification), fields, verification.Referer, cancellationToken).ConfigureAwait(false);
            var failure = verificationResponse.Failure ?? WebLoginProtocol.ReadExchangeCode(verificationResponse.Root, out _);
            LogResult("VerificationSubmit", failure is null ? BiliLoginResult.Succeeded() : BiliLoginResult.Failed(failure));
            if (failure is not null)
            {
                return BiliLoginResult.Failed(WebFallback(failure));
            }

            WebLoginProtocol.ReadExchangeCode(verificationResponse.Root, out var exchangeCode);
            var exchangeFields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["code"] = exchangeCode,
                ["source"] = string.IsNullOrEmpty(verification.Source) ? WebLoginProtocol.LoginSource : verification.Source,
            };
            exchangeCode = string.Empty;
            var goUrl = WebLoginProtocol.ExchangeGoUrl(verification);
            if (!string.IsNullOrEmpty(goUrl))
            {
                exchangeFields["go_url"] = goUrl;
            }

            sms.Invalidate();
            attempt.Sms = null;
            using var response = await attempt.SendAsync(HttpMethod.Post, BiliApis.Passport.WebExchangeCookie, exchangeFields, verification.Referer, cancellationToken).ConfigureAwait(false);
            failure = response.Failure ?? WebLoginProtocol.ReadFailure(response.Root);
            if (failure is not null)
            {
                return BiliLoginResult.Failed(WebFallback(failure), WebLoginProtocol.GetInt(WebLoginProtocol.GetData(response.Root), "status"));
            }

            var result = await FinishLoginAsync(attempt, response, smsLogin: false, urlCookies: false, cancellationToken).ConfigureAwait(false);
            return result.Failure is not null ? BiliLoginResult.Failed(WebFallback(result.Failure), result.ServerStatus) : result;
        }, failure => BiliLoginResult.Failed(WebFallback(failure)), cancellationToken, "VerificationLogin");

    private async Task<BiliLoginCaptchaResponse> ReadCaptchaAsync(WebLoginSession attempt, WebLoginResponse response, bool securityCenter, Uri referer, CancellationToken cancellationToken)
    {
        var result = WebLoginProtocol.ParseCaptcha(response.Root, securityCenter, out var diagnostics);
        WebLoginLog.Captcha(_logger, securityCenter, diagnostics);

        if (response.Failure is not null)
        {
            return BiliLoginCaptchaResponse.Failed(response.Failure);
        }

        if (result.Parameters is { Type: "img" } imageParameters)
        {
            result = await attempt.LoadCaptchaImageAsync(imageParameters, referer, cancellationToken).ConfigureAwait(false);
            LogResult(securityCenter ? "VerificationCaptchaImage" : "LoginCaptchaImage", result);
        }

        return result;
    }

    private async Task<BiliLoginResult> FinishLoginAsync(WebLoginSession attempt, WebLoginResponse response, bool smsLogin, bool urlCookies, CancellationToken cancellationToken)
    {
        if (response.Failure is not null)
        {
            return BiliLoginResult.Failed(response.Failure);
        }

        var result = WebLoginProtocol.ParseLogin(response.Root, attempt, smsLogin);
        if (result.Status != BiliLoginStatus.Success)
        {
            return result;
        }

        var cookies = attempt.GetAuthenticationCookies();
        try
        {
            if (urlCookies && !WebLoginProtocol.HasAuthenticationCookies(cookies))
            {
                WebLoginProtocol.AddUrlCookies(response.Root, cookies);
            }

            if (!WebLoginProtocol.HasAuthenticationCookies(cookies))
            {
                return Failed(BiliLoginErrorKind.InvalidResponse, result.Code);
            }

            if (attempt.IsClosed)
            {
                return Failed(BiliLoginErrorKind.SessionExpired);
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attempt.LifetimeToken);
            await _authenticationService.SignInAsync(new AuthorizeExecutionSettings { Cookies = cookies }, linked.Token).ConfigureAwait(false);
            await _authenticationService.EnsureTokenAsync(linked.Token).ConfigureAwait(false);
            attempt.Dispose();
            return result;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The login operation was canceled.", cancellationToken);
        }
        catch (Exception)
        {
            // Existing authorizers can attach raw response bodies to exceptions; never propagate them.
            return Failed(attempt.IsClosed ? BiliLoginErrorKind.SessionExpired : BiliLoginErrorKind.SessionImportFailed, result.Code);
        }
        finally
        {
            cookies.Clear();
        }
    }

    private async Task<T> UseSessionAsync<T>(BiliLoginSession? session, Func<WebLoginSession, Task<T>> action, Func<BiliLoginFailure, T> fail, CancellationToken cancellationToken, string step)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (session is not WebLoginSession attempt || !ReferenceEquals(attempt.Owner, this) || attempt.IsClosed)
        {
            return LogResult(step, fail(new(BiliLoginErrorKind.SessionExpired)));
        }

        if (!attempt.TryEnter())
        {
            return LogResult(step, fail(new(BiliLoginErrorKind.InvalidParameters)));
        }

        try
        {
            return LogResult(step, await action(attempt).ConfigureAwait(false));
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            WebLoginLog.Result(_logger, step, "Canceled", null, null);
            throw new OperationCanceledException("The login operation was canceled.", cancellationToken);
        }
        catch (HttpRequestException)
        {
            WebLoginLog.Result(_logger, step, "Failed", "Network", null);
            throw new HttpRequestException("The login request could not be completed.");
        }
        catch (Exception)
        {
            return LogResult(step, fail(new(attempt.IsClosed ? BiliLoginErrorKind.SessionExpired : BiliLoginErrorKind.InvalidResponse)));
        }
        finally
        {
            attempt.Exit();
        }
    }

    private T LogResult<T>(string step, T result)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return result;
        }

        var failure = result switch
        {
            BiliLoginResult login => login.Failure,
            BiliLoginCaptchaResponse captcha => captcha.Failure,
            BiliLoginSmsResponse sms => sms.Failure,
            BiliLoginCountriesResponse countries => countries.Failure,
            _ => null,
        };
        var outcome = result is BiliLoginResult loginResult ? loginResult.Status.ToString() : failure is null ? "Success" : "Failed";
        var code = result is BiliLoginResult loginCode ? loginCode.Code : failure?.Code ?? (failure is null ? 0 : (int?)null);
        WebLoginLog.Result(_logger, step, outcome, failure?.Kind.ToString(), code);
        return result;
    }

    private static BiliLoginResult Failed(BiliLoginErrorKind kind, int? code = null) => BiliLoginResult.Failed(new(kind, code));

    private static BiliLoginFailure WebFallback(BiliLoginFailure failure)
        => new(failure.Kind, failure.Code, failure.HttpStatusCode, requiresWebLogin: true);

    private static bool MatchesCaptcha(BiliLoginCaptchaParameters? parameters, BiliLoginCaptchaResult? captcha)
        => parameters is not null && captcha is not null
            && string.Equals(parameters.Token, captcha.Token, StringComparison.Ordinal)
            && string.Equals(parameters.Type, captcha.Type, StringComparison.Ordinal)
            && (captcha.Type switch
            {
                "geetest" => !string.IsNullOrWhiteSpace(captcha.Challenge) && !string.IsNullOrWhiteSpace(captcha.Validate) && !string.IsNullOrWhiteSpace(captcha.Seccode),
                "geetest4" => !string.IsNullOrWhiteSpace(captcha.LotNumber) && !string.IsNullOrWhiteSpace(captcha.CaptchaOutput)
                    && !string.IsNullOrWhiteSpace(captcha.PassToken) && !string.IsNullOrWhiteSpace(captcha.GenTime),
                "img" => captcha.ImageCode.Length == 5 && captcha.ImageCode.All(ch => ch is >= '!' and <= '~'),
                _ => false,
            });

    private static bool MatchesVerification(WebLoginSession attempt, WebVerificationContext verification)
        => ReferenceEquals(attempt.Verification, verification) && !string.IsNullOrEmpty(verification.TemporaryToken);

    private static bool ValidPhone(BiliLoginCountry? country, string? phoneNumber)
        => country is not null && country.Id > 0 && !string.IsNullOrWhiteSpace(phoneNumber)
            && phoneNumber.Trim().All(ch => ch is >= '0' and <= '9');
}
