// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HanumanInstitute.LibMpv.Core;

namespace Richasy.MpvKernel.Core;

/// <summary>
/// Points the HanumanInstitute.LibMpv bindings at the libmpv file chosen by the application.
/// </summary>
internal static partial class MpvLibrary
{
    private static readonly Lock _sync = new();
    private static string? _loadedPath;

    /// <summary>
    /// Gets the libmpv file the bindings are bound to, or <c>null</c> before the first successful load.
    /// </summary>
    public static string? LoadedPath
    {
        get
        {
            lock (_sync)
            {
                return _loadedPath;
            }
        }
    }

    /// <summary>
    /// Binds the native functions to the given libmpv file.
    /// The bindings cache their function pointers for the whole process, so the first successful
    /// call wins and a later call with a different path keeps using the library already loaded.
    /// </summary>
    /// <param name="dllPath">Full path of the libmpv file, for example <c>libmpv-2.dll</c>.</param>
    /// <returns><c>true</c> when the requested file is the one in use.</returns>
    public static bool Initialize(string dllPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(dllPath);
        var fullPath = Path.GetFullPath(dllPath);
        lock (_sync)
        {
            if (_loadedPath is not null)
            {
                return string.Equals(_loadedPath, fullPath, StringComparison.OrdinalIgnoreCase);
            }

            // The bindings can only build the file name as "<name>-<version>.dll".
            var match = LibraryFileNameRegex().Match(Path.GetFileName(fullPath));
            if (!match.Success)
            {
                throw new DllNotFoundException($"The libmpv file name must look like 'libmpv-2.dll': {Path.GetFileName(fullPath)}");
            }

            // Load it here first: an absolute path makes Windows resolve the library's own
            // dependencies next to it. The load done by the bindings then reuses this module.
            NativeLibrary.Load(fullPath);

            var name = match.Groups["name"].Value;
            MpvApi.RootPath = Path.GetDirectoryName(fullPath) ?? string.Empty;
            MpvApi.DllName = name;
            MpvApi.LibraryVersionMap[name] = int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture);

            // Resolve one function now, so that a broken file fails here and not in the middle of playback.
            _ = MpvApi.ClientApiVersion();
            _loadedPath = fullPath;
            return true;
        }
    }

    [GeneratedRegex(@"^(?<name>.+)-(?<version>\d+)\.dll$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LibraryFileNameRegex();
}
