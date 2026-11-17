// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

using Richasy.BiliKernel.Http;

namespace Richasy.BiliKernel.Authorizers.TV.Core;

internal sealed class LoginDeviceIdentity
{
    // A public PC-client UA profile; use it only for TV authorization, never Web captcha/login or media requests.
    internal const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) bilibili_pc/1.13.5 Chrome/106.0.5249.199 Electron/21.3.3 Safari/537.36";
    // Keep these values URL-safe: the existing App signer signs raw parameter values.
    internal const string DeviceName = "Bilibili-Windows-PC";
    internal const string DevicePlatform = "Windows";

    internal LoginDeviceIdentity(string deviceId)
    {
        if (!Guid.TryParseExact(deviceId, "N", out var installationId) || installationId == Guid.Empty)
        {
            throw new ArgumentException("A persisted installation identifier is required.", nameof(deviceId));
        }

        LocalId = installationId.ToString("N");
        DeviceGuid = installationId.ToString("D");
        // Reuse the BUVID format with a random installation seed, without reading a hardware address.
        Buvid = new Buvid(LocalId).Generate().ToUpperInvariant();
    }

    internal string LocalId { get; }

    internal string DeviceGuid { get; }

    internal string Buvid { get; }

    internal Dictionary<string, string> CreateParameters() => new()
    {
        ["local_id"] = LocalId,
        ["buvid"] = Buvid,
        ["guid"] = DeviceGuid,
        ["device_name"] = DeviceName,
        ["device_platform"] = DevicePlatform,
    };

    internal void ApplyHeaders(HttpRequestMessage request)
    {
        // Explicit request headers override BiliHttpClient's browser defaults without changing other callers.
        request.Headers.UserAgent.Clear();
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Buvid", Buvid);
    }
}
