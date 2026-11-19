// Copyright (c) Bili Copilot. All rights reserved.

using System.Runtime.InteropServices;

namespace BiliFluent.Mpv.RelayCheck;

internal sealed partial class CheckTimerResolution : IDisposable
{
    private readonly bool _active = OperatingSystem.IsWindows() && timeBeginPeriod(1) == 0;

    public void Dispose()
    {
        if (_active) timeEndPeriod(1);
    }

    [LibraryImport("winmm.dll")]
    private static partial uint timeBeginPeriod(uint period);

    [LibraryImport("winmm.dll")]
    private static partial uint timeEndPeriod(uint period);
}
