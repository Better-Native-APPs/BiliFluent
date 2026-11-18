// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Models.Authorization;
using Richasy.BiliKernel.Resolvers.Storage;
using System.Text.Json.Serialization;

namespace Richasy.BiliKernel.Resolvers.WinUIToken;

/// <summary>
/// 适用于 WinUI 的令牌解析器.
/// </summary>
public sealed class WinUIBiliTokenResolver : IBiliTokenResolver
{
    private const string TokenKey = "BiliUserTokenInfo";
    private const string TokenFileName = "app-token.dpapi";
    private static readonly byte[] _entropy = "BiliFluent.Credentials.AppToken.v1"u8.ToArray();
    private readonly Lock _sync = new();
    private readonly ProtectedCredentialStore<BiliToken> _store;
    private BiliToken? _cacheToken;

    /// <summary>
    /// Initializes a new instance of the <see cref="WinUIBiliTokenResolver"/> class.
    /// </summary>
    public WinUIBiliTokenResolver()
        => _store = ProtectedCredentialStore<BiliToken>.CreateForApplication(
            TokenFileName, _entropy, TokenSourceGenerationContext.Default.BiliToken, TokenKey);

    internal WinUIBiliTokenResolver(
        string localFolderPath,
        Func<string?> readLegacyValue,
        Action removeLegacyValue,
        Func<string, byte[]>? readFile = null,
        Action<Stream, byte[]>? writeFile = null)
        => _store = new ProtectedCredentialStore<BiliToken>(
            () => localFolderPath, TokenFileName, _entropy, TokenSourceGenerationContext.Default.BiliToken,
            readLegacyValue, removeLegacyValue, readFile, writeFile);

    /// <inheritdoc/>
    public BiliToken? GetToken()
    {
        lock (_sync)
        {
            _cacheToken ??= _store.Read();
            return _cacheToken;
        }
    }

    /// <inheritdoc/>
    public void RemoveToken()
    {
        lock (_sync)
        {
            _cacheToken = null;
            _store.Remove();
        }
    }

    /// <inheritdoc/>
    public void SaveToken(BiliToken token)
    {
        lock (_sync)
        {
            _store.Save(token);
            _cacheToken = token;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(BiliToken))]
internal sealed partial class TokenSourceGenerationContext : JsonSerializerContext
{
}
