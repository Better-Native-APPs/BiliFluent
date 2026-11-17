// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Bili.Authorization;

/// <summary>Resolves the installation's anonymous login device identifier, independently of credentials.</summary>
public interface IBiliLoginDeviceResolver
{
    /// <summary>Reads the existing identifier or persists a new random identifier before returning it.</summary>
    /// <returns>A non-empty GUID in the N format.</returns>
    string GetOrCreateDeviceId();
}
