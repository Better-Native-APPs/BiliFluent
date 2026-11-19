// Copyright (c) BiliFluent. All rights reserved.

#if DEBUG
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace BiliCopilot.UI.ViewModels.Core;

internal readonly record struct PlaybackStallDumpResult(string FileName, double WriteMilliseconds, long Bytes);

internal static class PlaybackStallDump
{
    // MiniDumpNormal | MiniDumpWithThreadInfo. Stacks, contexts and modules, never the heap.
    internal const uint DumpFlags = 0x00001000;
    private static readonly object DbgHelpSync = new();

    internal static bool TryWrite(string directory, DateTime utcTimestamp, out PlaybackStallDumpResult result, out string error)
    {
        result = default;
        error = string.Empty;
        string? path = null;
        var created = false;
        lock (DbgHelpSync)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var fileName = "stall-" + utcTimestamp.ToString("yyyyMMdd'T'HHmmss.fffffff'Z'", CultureInfo.InvariantCulture) + ".dmp";
                path = Path.Combine(directory, fileName);
                var startedAt = Stopwatch.GetTimestamp();
                long bytes;
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    if (!MiniDumpWriteDump(GetCurrentProcess(), (uint)Environment.ProcessId, file.SafeFileHandle, DumpFlags, 0, 0, 0))
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    bytes = file.Length;
                }

                var elapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                PruneDirectory(directory);
                result = new(fileName, elapsed, bytes);
                return true;
            }
            catch (Exception ex)
            {
                error = ex is Win32Exception native
                    ? "0x" + native.NativeErrorCode.ToString("X8", CultureInfo.InvariantCulture) : ex.GetType().Name;
                if (created && path is not null)
                {
                    try { File.Delete(path); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }

                return false;
            }
        }
    }

    internal static void PruneDirectory(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*.dmp")
            .OrderBy(File.GetLastWriteTimeUtc).ThenBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < files.Length - 10; i++)
        {
            File.Delete(files[i]);
            File.Delete(Path.ChangeExtension(files[i], ".threads.txt"));
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("dbghelp.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(nint process, uint processId, SafeFileHandle file, uint dumpType,
        nint exception, nint userStreams, nint callback);
}
#endif
