// Copyright (c) Bili Copilot. All rights reserved.

#if DEBUG
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI;

/// <summary>
/// Debug-only trace of interop failures raised on the UI thread.
/// An exception that escapes a dispatcher queue callback ends the process through a fail-fast
/// that never reaches <see cref="Application.UnhandledException"/>, so the application log
/// stays empty. Recording the throw site when the exception is first raised keeps the cause
/// available for such a crash.
/// </summary>
public partial class App
{
    private const int FirstChanceTraceLimit = 40;

    [ThreadStatic]
    private static bool _tracingFirstChance;

    private static int _firstChanceTraceCount;

    partial void InitializeFirstChanceTrace()
        => AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;

    private void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs args)
    {
        // Only the interop failures that surface as E_FAIL-style results, and only on the UI thread.
        if (_tracingFirstChance || args.Exception is not (COMException or Win32Exception)
            || _dispatcherQueue?.HasThreadAccess != true || GlobalDependencies.Services is null
            || Interlocked.Increment(ref _firstChanceTraceCount) > FirstChanceTraceLimit)
        {
            return;
        }

        _tracingFirstChance = true;
        try
        {
            // No file information: method names only, never a local path.
            var stack = new StackTrace(1, fNeedFileInfo: false).ToString();
            this.Get<ILogger<App>>().LogWarning(
                "First-chance interop exception on the UI thread: {Type} 0x{HResult:X8}\n{Stack}",
                args.Exception.GetType().Name,
                args.Exception.HResult,
                stack);
        }
        catch
        {
            // Diagnostics never change the outcome of the original exception.
        }
        finally
        {
            _tracingFirstChance = false;
        }
    }
}
#endif
