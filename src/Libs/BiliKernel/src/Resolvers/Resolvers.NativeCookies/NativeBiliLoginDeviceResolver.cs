// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

using Richasy.BiliKernel.Authenticator;
using Richasy.BiliKernel.Bili.Authorization;

namespace Richasy.BiliKernel.Resolvers.NativeCookies;

/// <summary>Stores the native host's installation identifier separately from its credential files.</summary>
public sealed class NativeBiliLoginDeviceResolver : IBiliLoginDeviceResolver
{
    private const string DeviceIdFileName = "login-device-id.txt";
    private readonly BiliLoginDeviceIdStore _store = new(
        () => File.Exists(DeviceIdFileName) ? File.ReadAllText(DeviceIdFileName) : null,
        value => File.WriteAllText(DeviceIdFileName, value));

    /// <inheritdoc/>
    public string GetOrCreateDeviceId() => _store.GetOrCreateDeviceId();
}
