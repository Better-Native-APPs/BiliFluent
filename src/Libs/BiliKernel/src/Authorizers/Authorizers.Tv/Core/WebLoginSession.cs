// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

using Richasy.BiliKernel.Bili;
using Richasy.BiliKernel.Models.Authorization;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;

namespace Richasy.BiliKernel.Authorizers.TV.Core;

internal sealed class WebLoginSession : BiliLoginSession
{
    private readonly HttpClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private int _closed;
    private int _busy;
    private bool _identityRequested;

    internal WebLoginSession(object owner, Func<CookieContainer, HttpMessageHandler>? handlerFactory = null)
    {
        Owner = owner;
        _lifetimeToken = _lifetime.Token;
        Cookies = new CookieContainer();
        _client = new HttpClient(handlerFactory?.Invoke(Cookies) ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = Cookies,
            SslProtocols = SslProtocols.None,
#if DEBUG
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
#endif
        }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", WebLoginProtocol.BrowserUserAgent);
    }

    internal object Owner { get; }

    internal CookieContainer Cookies { get; private set; }

    internal bool IsClosed => Volatile.Read(ref _closed) != 0;

    internal CancellationToken LifetimeToken => _lifetimeToken;

    internal BiliLoginCaptchaParameters? Captcha { get; set; }

    internal WebVerificationContext? Verification { get; set; }

    internal WebSmsContext? Sms { get; set; }

    internal bool PasswordSubmitted { get; set; }

    internal bool TryEnter() => !IsClosed && Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

    internal void Exit() => Interlocked.Exchange(ref _busy, 0);

    internal async Task<WebLoginResponse> SendAsync(HttpMethod method, string endpoint, Dictionary<string, string>? fields, Uri referer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsClosed)
        {
            fields?.Clear();
            return WebLoginResponse.Failed(new(BiliLoginErrorKind.SessionExpired));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        linked.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Referrer = referer;
        // A browser reports the origin of the page named by Referer, not one fixed site.
        request.Headers.TryAddWithoutValidation("Origin", referer.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        if (method == HttpMethod.Post)
        {
            request.Content = new FormUrlEncodedContent(fields ?? new Dictionary<string, string>());
        }

        // FormUrlEncodedContent has its own buffer; release the input strings before sending.
        fields?.Clear();
        int? httpStatus = null;
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            httpStatus = (int)response.StatusCode;
            if (response.Headers.TryGetValues("Set-Cookie", out var headers))
            {
                foreach (var header in headers)
                {
                    try
                    {
                        Cookies.SetCookies(new Uri(endpoint), header);
                    }
                    catch (CookieException)
                    {
                        // The default handler has already stored what it accepts. This copy only serves
                        // injected handlers, so one unusable cookie must not fail the whole response.
                    }
                }
            }

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var document = await JsonDocument.ParseAsync(stream, cancellationToken: linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var code = WebLoginProtocol.GetInt(document.RootElement, "code");
                document.Dispose();
                return WebLoginResponse.Failed(WebLoginProtocol.HttpFailure((int)response.StatusCode, code));
            }

            if (IsClosed)
            {
                document.Dispose();
                return WebLoginResponse.Failed(new(BiliLoginErrorKind.SessionExpired));
            }

            return WebLoginResponse.Succeeded(document);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The login operation was canceled.", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return WebLoginResponse.Failed(new(IsClosed ? BiliLoginErrorKind.SessionExpired : BiliLoginErrorKind.TimedOut));
        }
        catch (HttpRequestException)
        {
            // Do not retain the original exception, request URI, headers, or response body.
            throw new HttpRequestException("The login request could not be completed.");
        }
        catch (ObjectDisposedException) when (IsClosed)
        {
            return WebLoginResponse.Failed(new(BiliLoginErrorKind.SessionExpired));
        }
        catch (JsonException)
        {
            return WebLoginResponse.Failed(httpStatus is >= 300 ? WebLoginProtocol.HttpFailure(httpStatus.Value, null) : new(BiliLoginErrorKind.InvalidResponse));
        }
        catch (CookieException)
        {
            return WebLoginResponse.Failed(httpStatus is >= 300 ? WebLoginProtocol.HttpFailure(httpStatus.Value, null) : new(BiliLoginErrorKind.InvalidResponse));
        }
    }

    // Browsers always send a buvid3 cookie to these endpoints, so request an anonymous one once per attempt.
    // Whether the server requires it is unconfirmed: failing to get one never fails the login step.
    internal async Task EnsureBrowserIdentityAsync(CancellationToken cancellationToken)
    {
        if (_identityRequested)
        {
            return;
        }

        _identityRequested = true;
        try
        {
            using var response = await SendAsync(HttpMethod.Get, BiliApis.Account.Buvid, null, WebLoginProtocol.BrowserIdentityReferer, cancellationToken).ConfigureAwait(false);
            var value = WebLoginProtocol.GetString(WebLoginProtocol.GetData(response.Root), "b_3");
            if (!string.IsNullOrWhiteSpace(value))
            {
                Cookies.Add(new Cookie("buvid3", value, "/", ".bilibili.com") { Secure = true });
            }
        }
        catch (HttpRequestException)
        {
            // The request that follows reports the network failure.
        }
        catch (CookieException)
        {
            // An unusable value is the same as no value.
        }
    }

    internal Dictionary<string, string> GetAuthenticationCookies()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Cookie cookie in Cookies.GetCookies(new Uri("https://www.bilibili.com/")))
        {
            if (!cookie.Expired)
            {
                result[cookie.Name] = cookie.Value;
            }
        }

        return result;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _client.Dispose();
        _lifetime.Dispose();
        Captcha = null;
        Verification?.Invalidate();
        Verification = null;
        Sms?.Invalidate();
        Sms = null;
        Cookies = new CookieContainer();
    }
}

internal sealed class WebVerificationContext : BiliLoginVerificationContext
{
    internal WebVerificationContext(WebLoginSession session, string temporaryToken, string requestId, Uri referer, string source = "", string goUrl = "")
    {
        Session = session;
        TemporaryToken = temporaryToken;
        RequestId = requestId;
        Referer = referer;
        Source = source;
        GoUrl = goUrl;
    }

    internal WebLoginSession Session { get; }

    internal string TemporaryToken { get; private set; }

    internal string RequestId { get; private set; }

    internal Uri Referer { get; private set; }

    internal string Source { get; private set; }

    internal string GoUrl { get; private set; }

    internal BiliLoginCaptchaParameters? Captcha { get; set; }

    internal void Invalidate()
    {
        TemporaryToken = string.Empty;
        RequestId = string.Empty;
        Referer = WebLoginProtocol.PasswordReferer;
        Source = string.Empty;
        GoUrl = string.Empty;
        Captcha = null;
    }
}

internal sealed class WebSmsContext : BiliLoginSmsContext
{
    internal WebSmsContext(WebLoginSession session, string captchaKey, int countryCode, string phoneNumber, WebVerificationContext? verification)
    {
        Session = session;
        CaptchaKey = captchaKey;
        CountryCode = countryCode;
        PhoneNumber = phoneNumber;
        Verification = verification;
    }

    internal WebLoginSession Session { get; }

    internal string CaptchaKey { get; private set; }

    internal int CountryCode { get; }

    internal string PhoneNumber { get; private set; }

    internal WebVerificationContext? Verification { get; }

    internal void Invalidate()
    {
        CaptchaKey = string.Empty;
        PhoneNumber = string.Empty;
    }
}

internal sealed class WebLoginResponse : IDisposable
{
    private readonly JsonDocument? _document;

    private WebLoginResponse(JsonDocument? document, BiliLoginFailure? failure)
    {
        _document = document;
        Failure = failure;
    }

    internal JsonElement Root => _document?.RootElement ?? default;

    internal BiliLoginFailure? Failure { get; }

    internal static WebLoginResponse Succeeded(JsonDocument document) => new(document, null);

    internal static WebLoginResponse Failed(BiliLoginFailure failure) => new(null, failure);

    public void Dispose() => _document?.Dispose();
}
