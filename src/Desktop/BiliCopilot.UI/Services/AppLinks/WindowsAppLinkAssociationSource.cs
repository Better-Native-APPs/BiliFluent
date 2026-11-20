// Copyright (c) BiliFluent. All rights reserved.

using System.Runtime.InteropServices;
using Windows.System;

namespace BiliCopilot.UI.Services.AppLinks;

/// <summary>
/// The Windows lookups behind <see cref="AppLinkDefaultApp"/>. All of them only read:
/// nothing here prompts the user, activates a link or changes an association.
/// </summary>
internal sealed class WindowsAppLinkAssociationSource : IAppLinkAssociationSource
{
    // ASSOCF and ASSOCSTR values from shlwapi.h.
    private const uint NoTruncate = 0x00000020;
    private const uint IsProtocol = 0x00001000;
    private const int FriendlyAppName = 4;
    private const int AppId = 21;
    private const int BufferLength = 1024;

    /// <inheritdoc/>
    public string? GetOwnAppUserModelId() => AppInfo.Current.AppUserModelId;

    /// <inheritdoc/>
    public AppLinkAssociation? QueryDefault(string scheme)
    {
        var appUserModelId = Query(scheme, AppId);
        var friendlyName = Query(scheme, FriendlyAppName);
        return appUserModelId is null && friendlyName is null ? null : new(appUserModelId, friendlyName);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AppLinkRegistration>> FindPackagedHandlersAsync(string scheme)
    {
        var apps = await Launcher.FindUriSchemeHandlersAsync(scheme);
        return [.. apps.Select(static app => new AppLinkRegistration(app.AppUserModelId, TryGetDisplayName(app)))];
    }

    private static string? TryGetDisplayName(AppInfo app)
    {
        try
        {
            return app.DisplayInfo?.DisplayName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // A failed or oversized answer is reported as "no answer"; a truncated one is never returned.
    private static string? Query(string scheme, int kind)
    {
        var buffer = new char[BufferLength];
        var length = (uint)buffer.Length;
        if (AssocQueryStringW(IsProtocol | NoTruncate, kind, scheme, null, buffer, ref length) != 0) return null;
        var end = Array.IndexOf(buffer, '\0');
        return end > 0 ? new string(buffer, 0, end) : null;
    }

    [DllImport("shlwapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int AssocQueryStringW(uint flags, int kind, string association, string? extra, [Out] char[] output, ref uint length);
}
