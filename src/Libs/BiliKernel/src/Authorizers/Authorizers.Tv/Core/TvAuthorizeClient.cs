// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using Richasy.BiliKernel.Authenticator;
using Richasy.BiliKernel.Bili;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Http;
using Richasy.BiliKernel.Models.Authorization;
using System.Text.Json;

namespace Richasy.BiliKernel.Authorizers.TV.Core;

internal sealed class TVAuthorizeClient
{
    private readonly LoginDeviceIdentity _deviceIdentity;
    private readonly BiliHttpClient _httpClient;
    private readonly IBiliCookiesResolver _localCookiesResolver;
    private readonly IBiliTokenResolver _localTokenResolver;
    private readonly BiliAuthenticator _basicAuthenticator;
    private bool _isScanCheckInvoking;

    public TVAuthorizeClient(
        BiliHttpClient httpClient,
        IBiliCookiesResolver localCookiesResolver,
        IBiliTokenResolver localTokenResolver,
        BiliAuthenticator basicAuthenticator,
        LoginDeviceIdentity deviceIdentity)
    {
        _deviceIdentity = deviceIdentity;
        _httpClient = httpClient;
        _localCookiesResolver = localCookiesResolver;
        _localTokenResolver = localTokenResolver;
        _basicAuthenticator = basicAuthenticator;
    }

    public async Task EnsureTokenValidAsync(CancellationToken cancellationToken = default)
    {
        var localToken = _localTokenResolver.GetToken();
        BasicTokenCheck(localToken);
        var expiredTime = DateTimeOffset.FromUnixTimeSeconds(localToken.ExpiresIn);
        if (DateTimeOffset.Now < expiredTime)
        {
            return;
        }

        var newToken = await RefreshTokenAsync(cancellationToken).ConfigureAwait(false);
        if (newToken != null)
        {
            SaveToken(newToken);
            return;
        }

        throw new KernelException("令牌已过期，请重新登录");
    }

    public async Task<TVQRCode> GetQRCodeAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateLoginRequest(BiliApis.Passport.QRCode);
        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseObj = await BiliHttpClient.ParseAsync(response, SourceGenerationContext.Default.BiliDataResponseTVQRCode).ConfigureAwait(false);
        return responseObj.Data;
    }

    public async Task<BiliToken?> WaitQRCodeScanAsync(TVQRCode code, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_isScanCheckInvoking)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                continue;
            }

            _isScanCheckInvoking = true;
            try
            {
                var token = await GetTokenIfCodeScannedAsync(code.AuthCode, cancellationToken).ConfigureAwait(false);
                TrySaveCookiesFromToken(token);
                SaveToken(token);
                return token;
            }
            catch (KernelException ke)
            {
                if (ke.InnerException is TaskCanceledException)
                {
                    break;
                }

                // The response body keeps the API's lower-case keys, so read the code from it directly.
                // No readable code means a transport failure: like an unconfirmed code, poll again after a pause.
                var errorCode = ReadErrorCode(ke);
                var qrStatus = errorCode is null or 86090 or 86039
                    ? QRCodeStatus.NotConfirm
                    : errorCode is 86038 or -3 ? QRCodeStatus.Expired : QRCodeStatus.Failed;

                if (qrStatus == QRCodeStatus.NotConfirm)
                {
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    throw new Exception(qrStatus == QRCodeStatus.Expired ? "二维码已过期，请刷新二维码" : "二维码扫描失败", ke.InnerException);
                }
            }
            finally
            {
                _isScanCheckInvoking = false;
            }
        }

        return default;
    }

    public void SignOut()
    {
        _localTokenResolver.RemoveToken();
        _localCookiesResolver.RemoveCookies();
    }

    public async Task SignInWithCookiesAsync(IDictionary<string, string> cookies, CancellationToken cancellationToken)
    {
        // Stage the existing Cookie -> TV QR confirm/poll flow entirely in memory.
        // Shared credentials are written only after a usable App token is available.
        using var credentials = new CookieImportCredentials(cookies);
        using var httpClient = new BiliHttpClient();
        var authenticator = new BiliAuthenticator(credentials, credentials);
        var client = new TVAuthorizeClient(httpClient, credentials, credentials, authenticator, _deviceIdentity);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var qrCode = await client.GetQRCodeAsync(timeout.Token).ConfigureAwait(false)
                ?? throw new KernelException("Cookie session import failed.");
            var parameters = new Dictionary<string, string> { ["auth_code"] = qrCode.AuthCode };
            using var request = client.CreateLoginRequest(BiliApis.Passport.QRCodeConfirm, parameters,
                new BiliAuthorizeExecutionSettings { ForceNoToken = true, NeedCSRF = true, RequireCookie = true });
            using var response = await httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            parameters.Clear();

            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                BiliToken token;
                try
                {
                    token = await client.GetTokenIfCodeScannedAsync(qrCode.AuthCode, timeout.Token).ConfigureAwait(false);
                }
                catch (KernelException ex) when (IsCookieImportPending(ex))
                {
                    await Task.Delay(1000, timeout.Token).ConfigureAwait(false);
                    continue;
                }

                BasicTokenCheck(token);
                timeout.Token.ThrowIfCancellationRequested();
                _localCookiesResolver.SaveCookies(credentials.GetCookies());
                SaveToken(token);
                return;
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The cookie session import was canceled.", cancellationToken);
        }
        catch (Exception)
        {
            // Never attach response bodies, temporary QR codes, cookies, or tokens to an exception.
            throw new KernelException("Cookie session import failed.");
        }
    }

    private static bool IsCookieImportPending(KernelException exception)
        => ReadErrorCode(exception) is 86090 or 86039;

    // BiliHttpClient attaches the raw response body of a non-zero API code as the inner exception message.
    private static int? ReadErrorCode(KernelException exception)
    {
        if (exception.InnerException is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(exception.InnerException.Message);
            return WebLoginProtocol.GetInt(document.RootElement, "code");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void BasicTokenCheck(BiliToken? localToken)
    {
        if (localToken == null)
        {
            throw new InvalidOperationException("没有本地缓存的令牌信息，请重新登录");
        }

        if (string.IsNullOrEmpty(localToken.AccessToken)
            || string.IsNullOrEmpty(localToken.RefreshToken)
            || localToken.ExpiresIn < 1)
        {
            throw new KernelException("令牌信息不完整，请重新登录");
        }
    }

    private async Task<BiliToken> RefreshTokenAsync(CancellationToken cancellationToken = default)
    {
        var localToken = _localTokenResolver.GetToken();
        BasicTokenCheck(localToken);

        var paramters = new Dictionary<string, string>
        {
            { "access_token", localToken.AccessToken },
            { "refresh_token", localToken.RefreshToken },
        };

        var request = BiliHttpClient.CreateRequest(HttpMethod.Post, new Uri(BiliApis.Passport.RefreshToken));
        _basicAuthenticator.AuthorizeRestRequest(request, paramters);
        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseObj = await BiliHttpClient.ParseAsync(response, SourceGenerationContext.Default.BiliDataResponseBiliToken).ConfigureAwait(false);
        var token = responseObj.Data;
        TrySaveCookiesFromToken(token);

        return token;
    }

    private async Task<BiliToken> GetTokenIfCodeScannedAsync(string authCode, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string>
        {
            { "auth_code", authCode },
        };

        using var request = CreateLoginRequest(BiliApis.Passport.QRCodeCheck, parameters);
        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseObj = await BiliHttpClient.ParseAsync(response, SourceGenerationContext.Default.BiliDataResponseBiliToken).ConfigureAwait(false);
        var token = responseObj?.Data;
        TrySaveCookiesFromToken(token);
        return token;
    }

    internal HttpRequestMessage CreateLoginRequest(string endpoint, Dictionary<string, string>? parameters = null, BiliAuthorizeExecutionSettings? settings = null)
    {
        var fields = parameters ?? new Dictionary<string, string>();
        foreach (var identityField in _deviceIdentity.CreateParameters())
        {
            fields[identityField.Key] = identityField.Value;
        }

        var request = BiliHttpClient.CreateRequest(HttpMethod.Post, new Uri(endpoint));
        _basicAuthenticator.AuthorizeRestRequest(request, fields, settings ?? new BiliAuthorizeExecutionSettings { ForceNoToken = true });
        _deviceIdentity.ApplyHeaders(request);
        return request;
    }

    private void TrySaveCookiesFromToken(BiliToken? token)
    {
        if (token?.CookieInfo is null)
        {
            return;
        }

        var cookies = token.CookieInfo.Cookies.ToDictionary(cookie => cookie.Name!, cookie => cookie.Value ?? string.Empty);
        _localCookiesResolver.SaveCookies(cookies);
    }

    private void SaveToken(BiliToken token)
    {
        token.ExpiresIn = DateTimeOffset.Now.ToLocalTime().ToUnixTimeSeconds() + token.ExpiresIn;
        _localTokenResolver.SaveToken(token);
    }
}
