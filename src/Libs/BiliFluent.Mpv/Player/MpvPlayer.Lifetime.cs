// Copyright (c) Bili Copilot. All rights reserved.

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Richasy.MpvKernel.Core;

namespace Richasy.MpvKernel.Player;

public sealed partial class MpvPlayer
{
    private readonly MpvWorkGate _callbackWork = new();
    private readonly object _disposeSync = new();
    private Task? _disposeTask;

    private bool TryRunPlayerWork(Action action)
    {
        using var work = _callbackWork.TryEnter();
        if (!work.IsActive || _isDisposed) return false;
        action();
        return true;
    }

    private bool TryStartPlayerOperation(Func<Task> operation, out Task pending)
    {
        using var work = _callbackWork.TryEnter();
        if (!work.IsActive || _isDisposed || Client.IsDisposed)
        {
            pending = null!;
            return false;
        }

        pending = operation();
        return true;
    }

    private bool TryStartPlayerOperation<T>(Func<Task<T>> operation, out Task<T> pending)
    {
        using var work = _callbackWork.TryEnter();
        if (!work.IsActive || _isDisposed || Client.IsDisposed)
        {
            pending = null!;
            return false;
        }

        pending = operation();
        return true;
    }

    private void RunPlayerCallback(Action action, string name)
    {
        using var callback = _callbackWork.TryEnter();
        if (!callback.IsActive || _isDisposed) return;
        try { action(); }
        catch (Exception ex) { MpvCallbackError.Log(_logger, name, ex); }
    }

    private void RunPlayerCallbackAsync(Func<Task> action, string name)
    {
        using var callback = _callbackWork.TryEnter();
        if (!callback.IsActive || _isDisposed) return;
        try { _ = ObservePlayerCallbackAsync(action(), _logger, name); }
        catch (Exception ex) { MpvCallbackError.Log(_logger, name, ex); }
    }

    private static async Task ObservePlayerCallbackAsync(Task task, ILogger logger, string name)
    {
#pragma warning disable VSTHRD003 // Observe the operation just started by this callback; no caller synchronously joins it.
        try { await task.ConfigureAwait(false); }
#pragma warning restore VSTHRD003
        catch (Exception ex) { MpvCallbackError.Log(logger, name, ex); }
    }

    private void DetachClientEvents()
    {
        Client.Shutdown -= OnClientShutdown;
        Client.ReachFileLoaded -= OnFileLoaded;
        Client.ReachFileLoading -= OnFileLoading;
        Client.ReachFileEnd -= OnFileEnd;
        Client.DataNotify -= OnDataNotify;
        Client.ErrorOccurred -= OnErrorOccurred;
        Client.FileFailed -= OnRelayFileFailed;
    }

    private void SetShutdownProperties()
    {
        PlaybackState = MpvPlayerState.Idle;
        Position = 0;
        Duration = 0;
        Volume = 0;
        PlaybackRate = 0;
        IsLoading = false;
        IsFullScreen = false;
        IsCompactOverlay = false;
        IsInternalLoading = false;
        // The ordinary derived-state post is now invalid, so finish it inline.
        IsPlaying = false;
        IsBuffering = false;
        IsStopped = true;
    }

    private Task SetShutdownPropertiesAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // This is the only closing post allowed to mutate state; disposal awaits it.
            _uiContext.Post(_ =>
            {
                try
                {
                    SetShutdownProperties();
                    completion.TrySetResult();
                }
                catch (Exception ex) { completion.TrySetException(ex); }
            }, null);
        }
        catch (Exception ex) { completion.TrySetException(ex); }
#pragma warning disable VSTHRD003 // This player owns the UI completion; callers await asynchronously and let UI run.
        return completion.Task;
#pragma warning restore VSTHRD003
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        try { base.OnPropertyChanged(e); }
        catch (Exception ex) { MpvCallbackError.Log(_logger, "property-changed", ex); }
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanging(PropertyChangingEventArgs e)
    {
        try { base.OnPropertyChanging(e); }
        catch (Exception ex) { MpvCallbackError.Log(_logger, "property-changing", ex); }
    }
}
