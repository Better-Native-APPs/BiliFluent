// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Authenticator;

/// <summary>Persists an anonymous installation identifier in the host application's settings.</summary>
public sealed class BiliLoginDeviceIdStore
{
    /// <summary>The non-credential application setting containing the installation identifier.</summary>
    public const string SettingKey = "BiliLoginDeviceId";

    private readonly object _sync = new();
    private readonly Func<string?> _readSetting;
    private readonly Action<string> _writeSetting;
    private string? _deviceId;

    /// <summary>Initializes the store with the host application's setting accessors.</summary>
    /// <param name="readSetting">Reads the installation identifier setting.</param>
    /// <param name="writeSetting">Persists the installation identifier setting.</param>
    public BiliLoginDeviceIdStore(Func<string?> readSetting, Action<string> writeSetting)
    {
        _readSetting = readSetting ?? throw new ArgumentNullException(nameof(readSetting));
        _writeSetting = writeSetting ?? throw new ArgumentNullException(nameof(writeSetting));
    }

    /// <summary>Gets the installation identifier, creating it once if the setting is missing or invalid.</summary>
    /// <returns>A non-empty GUID in the N format.</returns>
    public string GetOrCreateDeviceId()
    {
        lock (_sync)
        {
            if (_deviceId is not null)
            {
                return _deviceId;
            }

            if (Guid.TryParseExact(_readSetting(), "N", out var existing) && existing != Guid.Empty)
            {
                return _deviceId = existing.ToString("N");
            }

            var created = Guid.NewGuid().ToString("N");
            // A failed write must not silently give this login an unpersisted, temporary identity.
            _writeSetting(created);
            return _deviceId = created;
        }
    }
}
