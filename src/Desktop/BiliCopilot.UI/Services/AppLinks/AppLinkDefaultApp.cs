// Copyright (c) BiliFluent. All rights reserved.

namespace BiliCopilot.UI.Services.AppLinks;

/// <summary>What can be said about the program Windows sends <c>bilibili://</c> links to.</summary>
internal enum AppLinkDefaultState
{
    /// <summary>Nothing reliable could be read.</summary>
    Unknown,

    /// <summary>Windows names this app as the default.</summary>
    ThisApp,

    /// <summary>Windows names a different program as the default.</summary>
    OtherApp,

    /// <summary>This app is registered for the scheme, but Windows names no default.</summary>
    RegisteredOnly,
}

/// <summary>The outcome of one read-only status query.</summary>
internal readonly record struct AppLinkDefaultStatus(AppLinkDefaultState State, string? OtherAppName = null);

/// <summary>What the Shell reports as the default for a scheme. Either value may be missing.</summary>
internal sealed record AppLinkAssociation(string? AppUserModelId, string? FriendlyName);

/// <summary>A packaged app that declares the scheme in its manifest.</summary>
internal sealed record AppLinkRegistration(string? AppUserModelId, string? DisplayName);

/// <summary>The system lookups behind the status. None of them may prompt or activate anything.</summary>
internal interface IAppLinkAssociationSource
{
    /// <summary>Gets the application user model ID of the running app.</summary>
    string? GetOwnAppUserModelId();

    /// <summary>Asks the Shell which program is the default; <see langword="null"/> when it names none.</summary>
    AppLinkAssociation? QueryDefault(string scheme);

    /// <summary>Lists the packaged apps registered for the scheme. Desktop programs are never included.</summary>
    Task<IReadOnlyList<AppLinkRegistration>> FindPackagedHandlersAsync(string scheme);
}

/// <summary>
/// Decides what to tell the user about the default <c>bilibili://</c> handler and where the
/// Windows page for changing it is. Reads only: an app cannot make itself the default.
/// </summary>
internal static class AppLinkDefaultApp
{
    internal const string Scheme = "bilibili";
    internal const string DefaultAppsPage = "ms-settings:defaultapps";

    // Windows 11 23H2. Earlier Windows 11 releases only understand the per-app page after a
    // specific cumulative update, which cannot be told apart from the build number alone.
    internal const int FirstBuildWithAppPage = 22631;

    private const string AppPageParameter = "registeredAUMID";
    private const int MaxAppUserModelIdLength = 256;
    private const int MaxDisplayNameLength = 64;

    /// <summary>Runs the lookups and never throws: any failure that prevents a conclusion yields <see cref="AppLinkDefaultState.Unknown"/>.</summary>
    internal static async Task<AppLinkDefaultStatus> QueryAsync(IAppLinkAssociationSource source)
    {
        string? own;
        AppLinkAssociation? association;
        try
        {
            own = source.GetOwnAppUserModelId();
            if (Clean(own) is null) return default;
            association = source.QueryDefault(Scheme);
        }
        catch (Exception)
        {
            return default;
        }

        // Only needed to name another app, or to confirm that this app is registered at all.
        var handlers = IsSame(association?.AppUserModelId, own) ? null : await TryFindPackagedHandlersAsync(source);
        return Evaluate(own, association, handlers);
    }

    /// <summary>
    /// Concludes "this app" or "another app" only from an application user model ID. The Shell's
    /// friendly name alone proves nothing: with no default chosen it is the name of the app picker.
    /// </summary>
    internal static AppLinkDefaultStatus Evaluate(
        string? ownAppUserModelId,
        AppLinkAssociation? association,
        IReadOnlyList<AppLinkRegistration>? packagedHandlers)
    {
        var own = Clean(ownAppUserModelId);
        if (own is null) return default;

        var current = Clean(association?.AppUserModelId);
        if (current is not null)
        {
            if (IsSame(current, own)) return new(AppLinkDefaultState.ThisApp);
            var registered = packagedHandlers?.FirstOrDefault(handler => IsSame(handler?.AppUserModelId, current));
            return new(AppLinkDefaultState.OtherApp, CleanName(registered?.DisplayName) ?? CleanName(association?.FriendlyName));
        }

        return packagedHandlers?.Any(handler => IsSame(handler?.AppUserModelId, own)) == true
            ? new(AppLinkDefaultState.RegisteredOnly)
            : default;
    }

    /// <summary>Whether this Windows version opens the Default apps page of one app.</summary>
    internal static bool SupportsAppPage(Version? windowsVersion)
        => windowsVersion is not null
            && (windowsVersion.Major > 10 || (windowsVersion.Major == 10 && windowsVersion.Build >= FirstBuildWithAppPage));

    /// <summary>Returns the addresses to try in order. The plain Default apps page is always last.</summary>
    internal static Uri[] BuildSettingsUris(string? ownAppUserModelId, Version? windowsVersion)
    {
        var general = new Uri(DefaultAppsPage);
        var own = Clean(ownAppUserModelId);
        if (own is null || own.Length > MaxAppUserModelIdLength || !SupportsAppPage(windowsVersion)) return [general];

        // The ID comes from the running package, so test and release builds each open their own page.
        return Uri.TryCreate($"{DefaultAppsPage}?{AppPageParameter}={Uri.EscapeDataString(own)}", UriKind.Absolute, out var appPage)
            ? [appPage, general]
            : [general];
    }

    /// <summary>Tries each address until one opens. Returns the one that did, or <see langword="null"/>.</summary>
    internal static async Task<Uri?> OpenSettingsAsync(IReadOnlyList<Uri> candidates, Func<Uri, Task<bool>> launch)
    {
        foreach (var candidate in candidates)
        {
            // A refused or failed address falls through to the next, more general one.
            if (await TryLaunchAsync(candidate, launch)) return candidate;
        }

        return null;
    }

    /// <summary>Reads the running app's ID without letting a lookup failure escape.</summary>
    internal static string? TryGetOwnAppUserModelId(IAppLinkAssociationSource source)
    {
        try
        {
            return Clean(source.GetOwnAppUserModelId());
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<AppLinkRegistration>?> TryFindPackagedHandlersAsync(IAppLinkAssociationSource source)
    {
        try
        {
            return await source.FindPackagedHandlersAsync(Scheme);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<bool> TryLaunchAsync(Uri address, Func<Uri, Task<bool>> launch)
    {
        try
        {
            return await launch(address);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsSame(string? left, string? right)
        => Clean(left) is { } first && Clean(right) is { } second && string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // The name belongs to another program: keep it on one line and short enough for the card.
    private static string? CleanName(string? value)
    {
        if (Clean(value) is not { } name) return null;
        name = string.Concat(name.Where(character => !char.IsControl(character))).Trim();
        if (name.Length == 0) return null;
        if (name.Length <= MaxDisplayNameLength) return name;

        // Leave room for the ellipsis and never cut a surrogate pair in half.
        var length = MaxDisplayNameLength - 1;
        if (char.IsHighSurrogate(name[length - 1])) length--;
        return string.Concat(name.AsSpan(0, length).TrimEnd(), "…");
    }
}
