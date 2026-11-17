// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using System.Timers;

namespace Richasy.MpvKernel.Player;

public sealed partial class MpvPlayer
{
    private void OnStatusTimerElapsedAsync(object? sender, ElapsedEventArgs e)
    {
        RunPlayerCallbackAsync(() =>
        {
            // Five-second state checks, thirty-second correction for observed values.
            var refreshObserved = Interlocked.Increment(ref _statusRefreshTicks) % ObservedPropertyRefreshTicks == 0;
            return RefreshStatusAsync(refreshObserved);
        }, "status-timer");
    }

    private void OnHistoryTimerElapsedAsync(object? sender, ElapsedEventArgs e)
    {
        RunPlayerCallbackAsync(() =>
        {
            // Capture the whole request now. Its observer only retains a logger after await.
            var history = _historyResolver;
            return history != null && !IsLoading && Duration > 0 && !Client.IsDisposed
                ? history.SaveHistoryAsync(Position, Duration, PlaybackState)
                : Task.CompletedTask;
        }, "history-timer");
    }

    private void OnClientShutdown(object? sender, EventArgs e)
    {
        RunPlayerCallback(() =>
        {
            DetachClientEvents();
            PostCounted(_ => SetShutdownProperties());
        }, "shutdown");
    }

    private void OnFileEnd(object? sender, EventArgs e)
    {
        RunPlayerCallback(() =>
        {
            if (_isTlsError) return;

            PostCounted(_ =>
            {
                IsLoading = false;
                PlaybackState = MpvPlayerState.End;
            });
        }, "file-end");
    }

    private void OnDataNotify(object? sender, MpvClientNotifyEventArgs e)
        => RunPlayerCallbackAsync(() => HandleDataNotifyAsync(e), "data-notify");

    private async Task HandleDataNotifyAsync(MpvClientNotifyEventArgs e)
    {
        switch (e.Id)
        {
            case MpvClientEventId.StateChanged:
                PostCounted(_ => PlaybackState = (MpvPlayerState)e.Data!);
                break;
            case MpvClientEventId.VolumeChanged:
                PostCounted(_ => Volume = (double)e.Data!);
                break;
            case MpvClientEventId.SpeedChanged:
                PostCounted(_ => PlaybackRate = (double)e.Data!);
                break;
            case MpvClientEventId.DurationChanged:
                PostCounted(_ => Duration = (double)e.Data!);
                break;
            case MpvClientEventId.PositionChanged:
                var positionVersion = Interlocked.Increment(ref _positionUpdateVersion);
                PostCounted(_ =>
                {
                    if (positionVersion == Interlocked.Read(ref _positionUpdateVersion)) Position = (double)e.Data!;
                });
                break;
            case MpvClientEventId.FullScreenChanged:
                PostCounted(_ => IsFullScreen = (bool)e.Data!);
                break;
            case MpvClientEventId.CompactOverlayChanged:
                PostCounted(_ => IsCompactOverlay = (bool)e.Data!);
                break;
            case MpvClientEventId.TrackCountChanged:
                await InitializeTracksAsync().ConfigureAwait(false);
                break;
            case MpvClientEventId.PlaybackRestart:
                _isTlsError = false;
                PostCounted(_ => IsPlaybackInitialized = true);
                break;
            case MpvClientEventId.CacheSpeedChanged:
                PostCounted(_ => CacheSpeed = (long)e.Data!);
                break;
            default:
                break;
        }
    }

    private void OnErrorOccurred(object? sender, MpvError e)
    {
        RunPlayerCallback(() =>
        {
            if (e == MpvError.TlsError) _isTlsError = true;
        }, "player-error");
    }

    private void OnFileLoaded(object? sender, EventArgs e)
        => RunPlayerCallbackAsync(HandleFileLoadedAsync, "file-loaded");

    private async Task HandleFileLoadedAsync()
    {
        PostCounted(_ => IsLoading = false);
        await RefreshStatusAsync().ConfigureAwait(false);
        TryRunPlayerWork(() =>
        {
            if (Math.Abs(Duration - 1) < 0.01)
                PostCounted(_ => Client.ThrowError(MpvError.LoadingFailed));
        });
    }

    private void OnFileLoading(object? sender, EventArgs e)
        => PostCounted(_ => IsLoading = true);

    // Both admission and execution are guarded: old UI queue entries become no-ops.
    private void PostCounted(SendOrPostCallback callback)
    {
        RunPlayerCallback(() =>
        {
            Client.CountUiPost();
            _uiContext.Post(_ => RunPlayerCallback(() => callback(null), "ui-post"), null);
        }, "ui-enqueue");
    }
}
