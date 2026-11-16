// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Player.Models;

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
        IMpvMediaSourceResolver? sourceResolver = null;
        IMpvMediaHistoryResolver? historyResolver = null;
        if (!TryRunPlayerWork(() =>
        {
            sourceResolver = _sourceResolver ?? throw new InvalidOperationException("Source resolver is not set.");
            historyResolver = _historyResolver;
            IsInternalLoading = true;
            ResetProperties();
            ReleaseRelayStreams();
        })) return;

        MpvMediaSource source;
        try
        {
            if (!TryStartPlayerOperation(() => sourceResolver!.GetSourceAsync(), out var sourceWork)) return;
            // Preserve normal UI affinity; a late continuation must re-enter the work gate.
            source = await sourceWork;
            if (_isDisposed) return;
            if (historyResolver != null)
            {
                if (!TryStartPlayerOperation(historyResolver.GetStartPositionAsync, out var historyWork)) return;
                var start = await historyWork;
                if (!TryRunPlayerWork(() => source.Options.StartPosition = start)) return;
            }

            if (!TryRunPlayerWork(() =>
            {
                _cachedSource = source;
                if (!string.IsNullOrEmpty(PreferExtraLoader)) source.Options?.InitExtraLoader = PreferExtraLoader;
                Title = source.Title;
                IsInternalLoading = false;
                _statusTimer.Start();
                if (historyResolver != null) _historyTimer.Start();
            })) return;
        }
        catch (Exception ex)
        {
            MpvCallbackError.Log(_logger, "media-source", ex);
            RunPlayerCallback(() =>
            {
                IsInternalLoading = false;
                Client.ThrowError(MpvError.VoInitFailed);
            }, "initialize-error");
            return;
        }

        if (alsoPlay)
        {
            CheckStateProperties();
            try
            {
                if (!TryStartPlayerOperation(() =>
                {
                    var (url, options) = PrepareLoad(source);
                    return Client.PlayAsync(url, options);
                }, out var play)) return;
                await play;
                Task<MpvResult>? resume = null;
                if (!TryRunPlayerWork(() =>
                {
                    if (!Client.IsDisposed && PlaybackState == MpvPlayerState.Paused) resume = Client.ResumeAsync();
                })) return;
                if (resume is not null) await resume;
            }
            catch (Exception ex)
            {
                MpvCallbackError.Log(_logger, "play-media", ex);
                RunPlayerCallback(() => Client.ThrowError(MpvError.OptionError), "play-error");
            }
        }
    }

    /// <summary>
    /// 更新解析器.
    /// </summary>
    public void UpdateResolvers(IMpvMediaSourceResolver sourceResolver, IMpvMediaHistoryResolver? historyResolver = null, IMpvMediaSubtitleResolver? subtitleResolver = null)
    {
        TryRunPlayerWork(() =>
        {
            _sourceResolver = sourceResolver ?? throw new ArgumentNullException(nameof(sourceResolver));
            _historyResolver = historyResolver;
            _subtitleResolver = subtitleResolver;
            _statusTimer?.Stop();
            _historyTimer?.Stop();
        });
    }

    /// <summary>
    /// 刷新播放器状态.
    /// </summary>
    /// <returns><see cref="Task"/>.</returns>
    public Task RefreshStatusAsync()
        => RefreshStatusAsync(refreshObservedProperties: true);

    private async Task RefreshStatusAsync(bool refreshObservedProperties)
    {
        if (_isDisposed || !Client.IsInitialized || Client.IsDisposed)
        {
            return;
        }

        if (!TryStartPlayerOperation(Client.GetPlayerStateAsync, out var stateWork)) return;
        var state = await stateWork.ConfigureAwait(false);
        if (_isDisposed) return;
        if (state.IsSuccess)
        {
            PostCounted(_ => PlaybackState = state.Value);
        }

        if (!refreshObservedProperties) return;

        if (!TryStartPlayerOperation(Client.GetDurationAsync, out var durationWork)) return;
        var duration = await durationWork.ConfigureAwait(false);
        if (_isDisposed) return;
        if (duration.IsSuccess)
        {
            PostCounted(_ => Duration = duration.Value);
        }

        long positionVersion = 0;
        if (!TryRunPlayerWork(() => positionVersion = Interlocked.Read(ref _positionUpdateVersion))) return;
        if (!TryStartPlayerOperation(Client.GetCurrentPositionAsync, out var positionWork)) return;
        var position = await positionWork.ConfigureAwait(false);
        if (_isDisposed) return;
        if (position.IsSuccess)
        {
            PostCounted(_ =>
            {
                // A newer time-pos event wins over an in-flight correction.
                if (positionVersion == Interlocked.Read(ref _positionUpdateVersion)) Position = position.Value;
            });
        }

        if (!TryStartPlayerOperation(Client.GetVolumeAsync, out var volumeWork)) return;
        var volume = await volumeWork.ConfigureAwait(false);
        if (_isDisposed) return;
        if (volume.IsSuccess)
        {
            PostCounted(_ => Volume = volume.Value);
        }

        if (!TryStartPlayerOperation(Client.GetSpeedAsync, out var rateWork)) return;
        var rate = await rateWork.ConfigureAwait(false);
        if (_isDisposed) return;
        if (rate.IsSuccess)
        {
            PostCounted(_ => PlaybackRate = rate.Value);
        }

        if (!TryStartPlayerOperation(Client.GetFullScreenStateAsync, out var fullScreenWork)) return;
        var isFullScreen = await fullScreenWork.ConfigureAwait(false);
        if (_isDisposed) return;
        if (isFullScreen.IsSuccess)
        {
            PostCounted(_ => IsFullScreen = isFullScreen.Value);
        }

        if (!TryStartPlayerOperation(Client.GetCompactOverlayStateAsync, out var compactWork)) return;
        var isCompactOverlay = await compactWork.ConfigureAwait(false);
        if (_isDisposed) return;
        if (isCompactOverlay.IsSuccess)
        {
            PostCounted(_ => IsCompactOverlay = isCompactOverlay.Value);
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
        IMpvMediaSourceResolver? resolver = null;
        if (!TryRunPlayerWork(() => resolver = _sourceResolver)) return false;
        try
        {
            if (!TryStartPlayerOperation(() => resolver!.GetSourceAsync(), out var sourceWork)) return false;
            var source = await sourceWork;
            if (_isDisposed) return false;
            if (!TryStartPlayerOperation(() =>
            {
                _cachedSource = source;
                if (!string.IsNullOrEmpty(PreferExtraLoader)) source.Options?.InitExtraLoader = PreferExtraLoader;
                if (preferPosition != null) source.Options?.StartPosition = preferPosition.Value;
                if (speed is > 0) source.Options?.InitialSpeed = speed.Value;
                Title = source.Title;
                var (url, options) = PrepareLoad(source);
                return Client.PlayAsync(url, options);
            }, out var play)) return false;
            await play;
            if (_isDisposed) return false;
            if (resume)
            {
                if (!TryStartPlayerOperation(Client.ResumeAsync, out var resumeWork)) return false;
                await resumeWork;
            }

            return !_isDisposed;
        }
        catch (Exception ex)
        {
            MpvCallbackError.Log(_logger, "replay-media", ex);
            RunPlayerCallback(() => Client.ThrowError(MpvError.VoInitFailed), "replay-error");
            return false;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Calls share one completion and failure. Await asynchronously while the UI context
    /// dispatches the final state; synchronous waits inside callbacks are not supported.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
#pragma warning disable VSTHRD003 // The player owns this one pool-based close, including its cached failure.
            if (_disposeTask is not null) return new(_disposeTask);
            _isDisposed = true;
            var drained = _callbackWork.CloseAsync();
            _disposeTask = Task.Run(() => DisposeCoreAsync(drained));
            return new(_disposeTask);
#pragma warning restore VSTHRD003
        }
    }

    private async Task DisposeCoreAsync(Task callbacksDrained)
    {
        DetachClientEvents();
#pragma warning disable VSTHRD003 // Only synchronous side-effect segments are joined; no network await is a segment.
        await callbacksDrained.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        _statusTimer.Elapsed -= OnStatusTimerElapsedAsync;
        _statusTimer.Stop();
        _statusTimer.Dispose();
        _historyTimer.Elapsed -= OnHistoryTimerElapsedAsync;
        _historyTimer.Stop();
        _historyTimer.Dispose();

        if (_historyResolver != null && Duration > 0)
        {
#pragma warning disable RCS1075 // Avoid empty catch clause that catches System.Exception
            try
            {
                await _historyResolver.SaveHistoryAsync(Position, Duration, PlaybackState, isExiting: true).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
#pragma warning restore RCS1075 // Avoid empty catch clause that catches System.Exception
        }

        try
        {
            try
            {
                await DisposeSeamlessAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await Client.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    // After mpv: nothing reads from the relay any more.
                    await DisposeRelayAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await SetShutdownPropertiesAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 触发属性更改事件.
    /// </summary>
    /// <param name="propertyName">属性名称.</param>
    public void RaisePropertyChanged(string propertyName)
    {
        PostCounted(_ => OnPropertyChanged(propertyName));
    }

    private void ResetProperties()
    {
        PostCounted(_ =>
        {
            IsPlaybackInitialized = false;
            Duration = 0;
            Position = 0;
        });
    }

    partial void OnPlaybackStateChanged(MpvPlayerState value)
        => CheckStateProperties();

    partial void OnPositionChanged(double value)
        => TryShowSubtitle();
}
