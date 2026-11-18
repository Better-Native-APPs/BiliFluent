// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.Extensions.Logging;

namespace Richasy.MpvKernel.Core;

/// <summary>
/// Closes admission and waits for synchronous side-effect segments, not remote awaits.
/// A segment must never await disposal: an event listener can request it and yield.
/// </summary>
internal sealed class MpvWorkGate
{
    private readonly object _sync = new();
    private bool _closed;
    private int _active;
    private TaskCompletionSource? _drained;

    public Lease TryEnter()
    {
        lock (_sync)
        {
            if (_closed) return default;
            _active++;
            return new(this);
        }
    }

    public bool TryRun(Action action)
    {
        using var lease = TryEnter();
        if (!lease.IsActive) return false;
        action();
        return true;
    }

    public Task CloseAsync()
    {
        lock (_sync)
        {
            _closed = true;
            _drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_active == 0) _drained.TrySetResult();
#pragma warning disable VSTHRD003 // This gate owns the completion; exiting its last segment releases it.
            return _drained.Task;
#pragma warning restore VSTHRD003
        }
    }

    private void Exit()
    {
        lock (_sync)
        {
            if (--_active == 0 && _closed) _drained!.TrySetResult();
        }
    }

    internal readonly struct Lease(MpvWorkGate? owner) : IDisposable
    {
        public bool IsActive => owner is not null;

        public void Dispose() => owner?.Exit();
    }
}

internal static class MpvCallbackError
{
    public static void Log(ILogger logger, string callback, Exception exception)
    {
        try
        {
            // Messages, stacks, URLs and request metadata are deliberately not logged.
            // A cancelled callback is the caller abandoning the work (closing while a source resolves), not a fault.
            if (exception is OperationCanceledException)
            {
                logger.LogDebug("Mpv | Callback {Callback} cancelled.", callback);
                return;
            }

            logger.LogError("Mpv | Callback {Callback} failed ({ExceptionType}).", callback, exception.GetType().Name);
        }
        catch (Exception)
        {
            System.Diagnostics.Debug.WriteLine("Mpv | Callback logging failed.");
        }
    }
}
