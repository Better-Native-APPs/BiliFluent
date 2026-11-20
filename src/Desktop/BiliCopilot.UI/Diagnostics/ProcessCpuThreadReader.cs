// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BiliCopilot.UI.Diagnostics;

internal readonly record struct ProcessCpuThread(uint Id, long CreatedFileTime, long CpuTicks, string Name);

internal readonly record struct ProcessCpuCounters(long Timestamp, long CapturedFileTime, long CpuTicks,
    int Gen0, int Gen1, int Gen2, long GcPauseTicks, long HeapBytes, long WorkingSetBytes);

internal sealed class ProcessCpuThreadReader : IDisposable
{
    private const uint QueryLimitedInformation = 0x0800;
    private const uint DuplicateSameAccess = 2;
    private const int NoMoreEntries = unchecked((int)0x8000001A);
    private readonly Dictionary<uint, CachedThread> _cache = new(64);
    private readonly List<ProcessCpuThread> _threads = new(64);
    private readonly List<uint> _retired = new(16);
    private long _serial;

    internal static uint CurrentThreadId => GetCurrentThreadId();
    internal ReadOnlySpan<ProcessCpuThread> Threads => CollectionsMarshal.AsSpan(_threads);

    internal ProcessCpuCounters Capture()
    {
        _threads.Clear();
        _retired.Clear();
        var serial = ++_serial;
        var process = GetCurrentProcess();
        nint cursor = 0;
        try
        {
            // This native iterator visits only our process, without a system-wide thread snapshot.
            while (true)
            {
                var status = NtGetNextThread(process, cursor, QueryLimitedInformation, 0, 0, out var next);
                if (status == NoMoreEntries) break;
                if (status != 0) throw new InvalidOperationException("Thread enumeration failed.");
                if (cursor != 0) _ = CloseHandle(cursor);
                cursor = next;
                var id = GetThreadId(cursor);
                if (id == 0 || GetThreadTimes(cursor, out var created, out var exited, out var kernel, out var user) == 0) continue;
                // The native iterator can still expose a terminated thread while another owner holds it.
                // Our retained handle below reports its final delta once, without reopening it every tick.
                if (exited != 0) continue;

                if (_cache.TryGetValue(id, out var cached) && cached.CreatedFileTime != created)
                {
                    _ = CloseHandle(cached.Handle);
                    _cache.Remove(id);
                    cached = default;
                }

                if (cached.Handle == 0)
                {
                    // Retain an independent handle so an exited thread's final CPU time is not lost.
                    if (DuplicateHandle(process, cursor, process, out var retained, 0, 0, DuplicateSameAccess) == 0) continue;
                    cached = new(retained, created, ReadDescription(cursor), serial);
                }
                else
                {
                    // A thread may get its name just after its first observation.
                    cached = cached with { Name = cached.Name.Length == 0 ? ReadDescription(cursor) : cached.Name, Seen = serial };
                }

                _cache[id] = cached;
                _threads.Add(new(id, created, kernel + user, cached.Name));
            }
        }
        finally
        {
            if (cursor != 0) _ = CloseHandle(cursor);
        }

        foreach (var (id, cached) in _cache)
        {
            if (cached.Seen == serial) continue;
            if (GetThreadTimes(cached.Handle, out var created, out var exited, out var kernel, out var user) != 0)
            {
                _threads.Add(new(id, created, kernel + user, cached.Name));
                if (exited == 0) continue;
            }
            _retired.Add(id);
        }

        foreach (var id in _retired)
        {
            if (_cache.Remove(id, out var cached)) _ = CloseHandle(cached.Handle);
        }

        if (GetProcessTimes(process, out _, out _, out var processKernel, out var processUser) == 0)
            throw new InvalidOperationException("Process CPU query failed.");
        var timestamp = Stopwatch.GetTimestamp();
        GetSystemTimePreciseAsFileTime(out var capturedFileTime);
        var memory = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
        if (K32GetProcessMemoryInfo(process, ref memory, memory.Size) == 0)
            throw new InvalidOperationException("Process memory query failed.");
        return new(timestamp, capturedFileTime, processKernel + processUser,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration().Ticks,
            GC.GetTotalMemory(false), (long)memory.WorkingSetSize);
    }

    public void Dispose()
    {
        foreach (var cached in _cache.Values) _ = CloseHandle(cached.Handle);
        _cache.Clear();
    }

    private static string ReadDescription(nint thread)
    {
        nint description = 0;
        try
        {
            return GetThreadDescription(thread, out description) >= 0 && description != 0
                ? Marshal.PtrToStringUni(description) ?? string.Empty : string.Empty;
        }
        finally
        {
            if (description != 0) _ = LocalFree(description);
        }
    }

    private readonly record struct CachedThread(nint Handle, long CreatedFileTime, string Name, long Seen);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        internal uint Size;
        internal uint PageFaultCount;
        internal nuint PeakWorkingSetSize;
        internal nuint WorkingSetSize;
        internal nuint QuotaPeakPagedPoolUsage;
        internal nuint QuotaPagedPoolUsage;
        internal nuint QuotaPeakNonPagedPoolUsage;
        internal nuint QuotaNonPagedPoolUsage;
        internal nuint PagefileUsage;
        internal nuint PeakPagefileUsage;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtGetNextThread(nint process, nint thread, uint access, uint attributes, uint flags, out nint next);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetThreadId(nint thread);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetThreadTimes(nint thread, out long created, out long exited, out long kernel, out long user);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetProcessTimes(nint process, out long created, out long exited, out long kernel, out long user);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetThreadDescription(nint thread, out nint description);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint target,
        uint access, int inherit, uint options);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int K32GetProcessMemoryInfo(nint process, ref ProcessMemoryCounters counters, uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void GetSystemTimePreciseAsFileTime(out long time);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint LocalFree(nint memory);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int CloseHandle(nint handle);
}
