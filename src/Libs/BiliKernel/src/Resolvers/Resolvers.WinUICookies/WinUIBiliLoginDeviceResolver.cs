// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

using Richasy.BiliKernel.Authenticator;
using Richasy.BiliKernel.Bili.Authorization;

namespace Richasy.BiliKernel.Resolvers.WinUICookies;

/// <summary>Stores the anonymous login device identifier in application settings, not the credential store.</summary>
public sealed class WinUIBiliLoginDeviceResolver : IBiliLoginDeviceResolver
{
    private readonly BiliLoginDeviceIdStore _store = new(
        () => GetSettings().Values.TryGetValue(BiliLoginDeviceIdStore.SettingKey, out var value) ? value as string : null,
        value => GetSettings().Values[BiliLoginDeviceIdStore.SettingKey] = value);

    /// <inheritdoc/>
    public string GetOrCreateDeviceId() => _store.GetOrCreateDeviceId();

    private static Microsoft.Windows.Storage.ApplicationDataContainer GetSettings()
        => Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalSettings.CreateContainer(
            "v1", Microsoft.Windows.Storage.ApplicationDataCreateDisposition.Always);
}
