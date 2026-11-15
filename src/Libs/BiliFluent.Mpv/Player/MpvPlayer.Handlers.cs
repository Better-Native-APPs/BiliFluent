// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Timers;

namespace Richasy.MpvKernel.Player;

public sealed partial class MpvPlayer
{
    private async void OnStatusTimerElapsedAsync(object? sender, ElapsedEventArgs e)
    {
        // Five-second state checks, thirty-second correction for observed values.
        var refreshObserved = Interlocked.Increment(ref _statusRefreshTicks) % ObservedPropertyRefreshTicks == 0;
        await RefreshStatusAsync(refreshObserved);
    }

    private async void OnHistoryTimerElapsedAsync(object? sender, ElapsedEventArgs e)
    {
        if (_historyResolver != null && !IsLoading && Duration > 0 && !Client.IsDisposed)
        {
            try
            {
                await _historyResolver.SaveHistoryAsync(Position, Duration, PlaybackState);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save history.");
            }
        }
    }

    private void OnClientShutdown(object? sender, EventArgs e)
    {
        Client.Shutdown -= OnClientShutdown;
        Client.ReachFileLoaded -= OnFileLoaded;
        Client.ReachFileLoading -= OnFileLoading;
        Client.ReachFileEnd -= OnFileEnd;
        Client.DataNotify -= OnDataNotify;
        Client.ErrorOccurred -= OnErrorOccurred;
        var position = Position;
        PostCounted(_ =>
        {
            PlaybackState = MpvPlayerState.Idle;
            Position = 0;
            Duration = 0;
            Volume = 0;
            PlaybackRate = 0;
            IsLoading = false;
            IsFullScreen = false;
            IsCompactOverlay = false;
        });
    }

    private void OnFileEnd(object? sender, EventArgs e)
    {
        if (_isTlsError)
        {
            return;
        }

        PostCounted(_ =>
        {
            IsLoading = false;
            PlaybackState = MpvPlayerState.End;
        });
    }

    private async void OnDataNotify(object? sender, MpvClientNotifyEventArgs e)
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
                Interlocked.Increment(ref _positionUpdateVersion);
                PostCounted(_ => Position = (double)e.Data!);
                break;
            case MpvClientEventId.FullScreenChanged:
                PostCounted(_ => IsFullScreen = (bool)e.Data!);
                break;
            case MpvClientEventId.CompactOverlayChanged:
                PostCounted(_ => IsCompactOverlay = (bool)e.Data!);
                break;
            case MpvClientEventId.TrackCountChanged:
                await InitializeTracksAsync();
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
        if (e == MpvError.TlsError)
        {
            _isTlsError = true;
        }
    }

    private async void OnFileLoaded(object? sender, EventArgs e)
    {
        PostCounted(_ => IsLoading = false);
        await RefreshStatusAsync();
        if (Math.Abs(Duration - 1) < 0.01)
        {
            // No file loaded, throw an error.
            PostCounted(_ => Client.ThrowError(MpvError.LoadingFailed));
        }
    }

    private void OnFileLoading(object? sender, EventArgs e)
        => PostCounted(_ => IsLoading = true);

    // Same post as before; the client counts it only while stutter diagnostics run.
    private void PostCounted(SendOrPostCallback callback)
    {
        Client.CountUiPost();
        _uiContext.Post(callback, default);
    }
}
