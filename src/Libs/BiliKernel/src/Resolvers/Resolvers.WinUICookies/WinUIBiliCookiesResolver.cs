// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Resolvers.Storage;
using System.Text.Json.Serialization;

namespace Richasy.BiliKernel.Resolvers.WinUICookies;

/// <summary>
/// 适用于 WinUI 的 Cookie 解析器.
/// </summary>
public sealed class WinUIBiliCookiesResolver : IBiliCookiesResolver
{
    private const string CookiesKey = "BiliUserCookies";
    private const string CookiesFileName = "cookies.dpapi";
    private static readonly byte[] _entropy = "BiliFluent.Credentials.Cookies.v1"u8.ToArray();
    private readonly Lock _sync = new();
    private readonly ProtectedCredentialStore<IDictionary<string, string>> _store;
    private IDictionary<string, string>? _cacheCookies;

    /// <summary>
    /// Initializes a new instance of the <see cref="WinUIBiliCookiesResolver"/> class.
    /// </summary>
    public WinUIBiliCookiesResolver()
        => _store = ProtectedCredentialStore<IDictionary<string, string>>.CreateForApplication(
            CookiesFileName, _entropy, CookiesSourceGenerationContext.Default.IDictionaryStringString, CookiesKey);

    internal WinUIBiliCookiesResolver(
        string localFolderPath,
        Func<string?> readLegacyValue,
        Action removeLegacyValue,
        Func<string, byte[]>? readFile = null,
        Action<Stream, byte[]>? writeFile = null)
        => _store = new ProtectedCredentialStore<IDictionary<string, string>>(
            () => localFolderPath, CookiesFileName, _entropy, CookiesSourceGenerationContext.Default.IDictionaryStringString,
            readLegacyValue, removeLegacyValue, readFile, writeFile);

    /// <inheritdoc/>
    public IDictionary<string, string> GetCookies()
    {
        lock (_sync)
        {
            if (_cacheCookies?.Count > 0)
            {
                return _cacheCookies;
            }

            _cacheCookies = _store.Read();
            // Preserve the existing null result and the reload of an empty cache.
            return _cacheCookies!;
        }
    }

    /// <inheritdoc/>
    public string GetCookieString()
    {
        lock (_sync)
        {
            var cookies = GetCookies();
            var cookieList = cookies.Select(item => $"{item.Key}={item.Value}");
            return string.Join("; ", cookieList);
        }
    }

    /// <inheritdoc/>
    public void RemoveCookies()
    {
        lock (_sync)
        {
            _cacheCookies = null;
            _store.Remove();
        }
    }

    /// <inheritdoc/>
    public void SaveCookies(IDictionary<string, string> cookies)
    {
        lock (_sync)
        {
            _store.Save(cookies);
            _cacheCookies = cookies;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(IDictionary<string, string>))]
internal sealed partial class CookiesSourceGenerationContext : JsonSerializerContext
{
}
