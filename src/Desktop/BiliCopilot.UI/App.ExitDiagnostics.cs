// Copyright (c) Bili Copilot. All rights reserved.

using Serilog;

namespace BiliCopilot.UI;

public partial class App
{
    internal static void TraceExitReason(string reason)
    {
        // Callers pass fixed reason codes only, never account, activation or exception data.
        try { Log.Information("App | exit reason={Reason:l}", reason); }
        catch { /* Diagnostics cannot change the owned shutdown result. */ }
    }

    private static void InitializeExitDiagnostics()
    {
        AppDomain.CurrentDomain.ProcessExit += OnRuntimeProcessExit;
        AppDomain.CurrentDomain.UnhandledException += OnRuntimeUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static void OnRuntimeProcessExit(object? sender, EventArgs args)
    {
        TraceExitReason("runtime-process-exit");
        // ProcessExit has no asynchronous completion contract; flush the final attribution line.
#pragma warning disable VSTHRD103
        try { Log.CloseAndFlush(); }
#pragma warning restore VSTHRD103
        catch { /* A logger failure must not throw from the runtime exit callback. */ }
    }

    private static void OnRuntimeUnhandledException(object sender, System.UnhandledExceptionEventArgs args)
    {
        if (!args.IsTerminating) return;
        TraceExitReason("runtime-unhandled-exception");
        try
        {
            var error = args.ExceptionObject as Exception;
            Log.Error("App | terminating exception type={ExceptionType} hresult={HResult}",
                args.ExceptionObject.GetType().Name, error?.HResult);
        }
        catch { /* Fixed, bounded diagnostics only; leave runtime termination unchanged. */ }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        try
        {
            Log.Warning("App | unobserved task exception type={ExceptionType} hresult={HResult}",
                args.Exception.GetType().Name, args.Exception.HResult);
        }
        catch { /* Observation diagnostics do not change task-exception policy. */ }
    }
}
