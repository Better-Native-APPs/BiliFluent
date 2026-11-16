// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Models.Authorization;

namespace Richasy.BiliKernel.Authorizers.TV.Core;

internal sealed class CookieImportCredentials : IBiliCookiesResolver, IBiliTokenResolver, IDisposable
{
    private Dictionary<string, string> _cookies;
    private BiliToken? _token;

    internal CookieImportCredentials(IDictionary<string, string> cookies)
        => _cookies = new(cookies, StringComparer.Ordinal);

    public string GetCookieString() => string.Join("; ", _cookies.Select(pair => $"{pair.Key}={pair.Value}"));

    public IDictionary<string, string> GetCookies() => new Dictionary<string, string>(_cookies, StringComparer.Ordinal);

    public void SaveCookies(IDictionary<string, string> cookies)
    {
        _cookies.Clear();
        _cookies = new(cookies, StringComparer.Ordinal);
    }

    public void RemoveCookies() => _cookies.Clear();

    public BiliToken? GetToken() => _token;

    public void SaveToken(BiliToken token) => _token = token;

    public void RemoveToken() => _token = null;

    public void Dispose()
    {
        RemoveCookies();
        RemoveToken();
    }

    public override string ToString() => "CookieImportCredentials [redacted]";
}
