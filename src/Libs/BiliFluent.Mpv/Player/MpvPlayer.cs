// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Richasy.MpvKernel.Core;

namespace Richasy.MpvKernel.Player;

/// <summary>
/// MPV 播放器.
/// </summary>
public sealed partial class MpvPlayer : ObservableObject, IAsyncDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MpvPlayer"/> class.
    /// </summary>
    public MpvPlayer(
        MpvClient client,
        IMpvMediaSourceResolver sourceResolver,
        IMpvMediaHistoryResolver? historyResolver = null,
        IMpvMediaSubtitleResolver? subtitleResolver = null,
        ILogger? logger = null)
    {
        Client = client;
        UpdateResolvers(sourceResolver, historyResolver, subtitleResolver);
        _logger = logger ?? NullLogger.Instance;
        _uiContext = SynchronizationContext.Current ?? throw new InvalidOperationException("Must be created on UI thread.");

        // Composite state includes unobserved flags; other properties use events.
        _statusTimer = new System.Timers.Timer(5000);
        _statusTimer.Elapsed += OnStatusTimerElapsedAsync;
        _historyTimer = new System.Timers.Timer(10000);
        _historyTimer.Elapsed += OnHistoryTimerElapsedAsync;
        PlaybackState = MpvPlayerState.Idle;
        Client.DataNotify += OnDataNotify;
        Client.ReachFileLoading += OnFileLoading;
        Client.ReachFileLoaded += OnFileLoaded;
        Client.ReachFileEnd += OnFileEnd;
        Client.Shutdown += OnClientShutdown;
        Client.FileFailed += OnRelayFileFailed;
    }

    /// <summary>
    /// 初始化播放器.
    /// </summary>
    /// <returns><see cref="Task"/>.</returns>
    public async Task InitializeAsync(bool alsoPlay = true)
    {
        if (_sourceResolver is null)
        {
            throw new InvalidOperationException("Source resolver is not set.");
        }

        try
        {
            IsInternalLoading = true;
            ResetProperties();

            // Another media: nothing of the previous one is served by the relay any longer.
            ReleaseRelayStreams();
            _cachedSource = await _sourceResolver.GetSourceAsync();
            if (_historyResolver != null)
            {
                _cachedSource.Options.StartPosition = await _historyResolver.GetStartPositionAsync();
            }

            if (!string.IsNullOrEmpty(PreferExtraLoader))
            {
                _cachedSource.Options?.InitExtraLoader = PreferExtraLoader;
            }

            Title = _cachedSource.Title;
        }
        catch (Exception ex)
        {
            IsInternalLoading = false;
            _logger.LogError(ex, "Failed to get media source.");
            Client.ThrowError(MpvError.VoInitFailed);
            return;
        }

        IsInternalLoading = false;
        if (_isDisposed)
        {
            return;
        }

        _statusTimer.Start();
        if (_historyResolver != null)
        {
            _historyTimer.Start();
        }

        if (alsoPlay)
        {
            CheckStateProperties();
            try
            {
                var (url, options) = PrepareLoad(_cachedSource);
                await Client.PlayAsync(url, options);
                if (PlaybackState == MpvPlayerState.Paused)
                {
                    await Client.ResumeAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to play media.");
                Client.ThrowError(MpvError.OptionError);
            }
        }
    }

    /// <summary>
    /// 更新解析器.
    /// </summary>
    public void UpdateResolvers(IMpvMediaSourceResolver sourceResolver, IMpvMediaHistoryResolver? historyResolver = null, IMpvMediaSubtitleResolver? subtitleResolver = null)
    {
        _sourceResolver = sourceResolver ?? throw new ArgumentNullException(nameof(sourceResolver));
        _historyResolver = historyResolver;
        _subtitleResolver = subtitleResolver;
        _statusTimer?.Stop();
        _historyTimer?.Stop();
    }

    /// <summary>
    /// 刷新播放器状态.
    /// </summary>
    /// <returns><see cref="Task"/>.</returns>
    public Task RefreshStatusAsync()
        => RefreshStatusAsync(refreshObservedProperties: true);

    private async Task RefreshStatusAsync(bool refreshObservedProperties)
    {
        if (!Client.IsInitialized || Client.IsDisposed)
        {
            return;
        }

        var state = await Client.GetPlayerStateAsync();
        if (state.IsSuccess)
        {
            _uiContext.Post(_ => PlaybackState = state.Value, null);
        }

        if (!refreshObservedProperties) return;

        var duration = await Client.GetDurationAsync();
        if (duration.IsSuccess)
        {
            _uiContext.Post(_ => Duration = duration.Value, null);
        }

        var positionVersion = Interlocked.Read(ref _positionUpdateVersion);
        var position = await Client.GetCurrentPositionAsync();
        if (position.IsSuccess)
        {
            _uiContext.Post(_ =>
            {
                // A newer time-pos event wins over an in-flight correction.
                if (positionVersion == Interlocked.Read(ref _positionUpdateVersion)) Position = position.Value;
            }, null);
        }

        var volume = await Client.GetVolumeAsync();
        if (volume.IsSuccess)
        {
            _uiContext.Post(_ => Volume = volume.Value, null);
        }

        var rate = await Client.GetSpeedAsync();
        if (rate.IsSuccess)
        {
            _uiContext.Post(_ => PlaybackRate = rate.Value, null);
        }

        var isFullScreen = await Client.GetFullScreenStateAsync();
        if (isFullScreen.IsSuccess)
        {
            _uiContext.Post(_ => IsFullScreen = isFullScreen.Value, null);
        }

        var isCompactOverlay = await Client.GetCompactOverlayStateAsync();
        if (isCompactOverlay.IsSuccess)
        {
            _uiContext.Post(_ => IsCompactOverlay = isCompactOverlay.Value, null);
        }
    }

    /// <summary>
    /// 重新播放当前媒体.
    /// </summary>
    /// <param name="preferPosition">Position to start from, in seconds.</param>
    /// <param name="resume">Whether to resume playback after loading; <c>false</c> keeps the user's pause.</param>
    /// <param name="speed">Playback speed to load with, instead of the one the source asks for.</param>
    /// <returns>Whether the media was handed to mpv; <c>false</c> when no source could be served or loaded.</returns>
    public async Task<bool> ReplayAsync(double? preferPosition = null, bool resume = true, double? speed = null)
    {
        try
        {
            _cachedSource = await _sourceResolver.GetSourceAsync();
            if (!string.IsNullOrEmpty(PreferExtraLoader))
            {
                _cachedSource.Options?.InitExtraLoader = PreferExtraLoader;
            }

            if (preferPosition != null)
            {
                _cachedSource.Options?.StartPosition = preferPosition.Value;
            }

            if (speed is > 0)
            {
                _cachedSource.Options?.InitialSpeed = speed.Value;
            }

            Title = _cachedSource.Title;
            var (url, options) = PrepareLoad(_cachedSource);
            await Client.PlayAsync(url, options);
            if (resume)
            {
                await Client.ResumeAsync();
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get media source.");
            Client.ThrowError(MpvError.VoInitFailed);
            return false;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _isDisposed = true;
        if (_historyResolver != null && Duration > 0)
        {
#pragma warning disable RCS1075 // Avoid empty catch clause that catches System.Exception
            try
            {
                await _historyResolver.SaveHistoryAsync(Position, Duration, PlaybackState, isExiting: true);
            }
            catch (Exception)
            {
            }
#pragma warning restore RCS1075 // Avoid empty catch clause that catches System.Exception
        }

        _statusTimer.Elapsed -= OnStatusTimerElapsedAsync;
        _statusTimer.Stop();
        _statusTimer.Dispose();
        _historyTimer.Elapsed -= OnHistoryTimerElapsedAsync;
        _historyTimer.Stop();
        _historyTimer.Dispose();
        try
        {
            await DisposeSeamlessAsync();
        }
        finally
        {
            try
            {
                await Client.DisposeAsync();
            }
            finally
            {
                // After mpv: nothing reads from the relay any more.
                await DisposeRelayAsync();
            }
        }
    }

    /// <summary>
    /// 触发属性更改事件.
    /// </summary>
    /// <param name="propertyName">属性名称.</param>
    public void RaisePropertyChanged(string propertyName)
    {
        _uiContext.Post(_ => OnPropertyChanged(propertyName), default);
    }

    private void ResetProperties()
    {
        _uiContext.Post(_ =>
        {
            IsPlaybackInitialized = false;
            Duration = 0;
            Position = 0;
        }, default);
    }

    partial void OnPlaybackStateChanged(MpvPlayerState value)
        => CheckStateProperties();

    partial void OnPositionChanged(double value)
        => TryShowSubtitle();
}
