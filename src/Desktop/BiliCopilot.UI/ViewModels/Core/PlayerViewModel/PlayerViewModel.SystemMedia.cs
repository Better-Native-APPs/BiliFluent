// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Core.Enums;
using Richasy.MpvKernel.Core.Models;
using System.ComponentModel;
using Windows.Media;
using Windows.Storage.Streams;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private readonly SystemMediaTimelinePublisher _systemMediaTimeline = new();
    private SystemMediaTransportControls? _systemMediaControls;
    private DispatcherQueueTimer? _systemMediaTimer;
    private MpvClient? _systemMediaClient;
    private long _systemMediaEpoch;
    private long _systemMediaReadyGeneration = -1;
    private long _systemMediaPlaylistGeneration = -1;
    private bool _systemMediaAwaitingInitializationReset;
    private SystemMediaButtons? _systemMediaButtons;
    private SystemMediaPlaybackStatus? _systemMediaStatus;
    private double _systemMediaRate = double.NaN;
    private PlayerInformationUpdatedEventArgs? _systemMediaMetadata;
    private long _systemMediaMetadataGeneration = -1;

    private void InitializeSystemMediaTransportControls()
    {
        _nativeMp = new Windows.Media.Playback.MediaPlayer();
        _nativeMp.CommandManager.IsEnabled = false;
        _smtc = _nativeMp.SystemMediaTransportControls;
        _systemMediaControls = _smtc;
        Interlocked.Increment(ref _systemMediaEpoch);
        _systemMediaButtons = null;
        _systemMediaStatus = null;
        _systemMediaRate = double.NaN;
        _systemMediaTimeline.Reset();
        _systemMediaReadyGeneration = -1;
        _systemMediaAwaitingInitializationReset = Player?.IsPlaybackInitialized == true;
        _smtc.ButtonPressed += OnSmtcButtonPressed;
        _smtc.PlaybackPositionChangeRequested += OnSmtcPlaybackPositionChangeRequested;
        _smtc.PlaybackRateChangeRequested += OnSmtcPlaybackRateChangeRequested;
        PropertyChanged += OnSystemMediaOwnerPropertyChanged;
        PlayNextCommand.CanExecuteChanged += OnSystemMediaNavigationCanExecuteChanged;
        PlayPreviousCommand.CanExecuteChanged += OnSystemMediaNavigationCanExecuteChanged;
        _systemMediaTimer = queue.CreateTimer();
        _systemMediaTimer.Interval = SystemMediaTimelinePublisher.UpdateInterval;
        _systemMediaTimer.Tick += OnSystemMediaTimelineTick;
        BindSystemMediaClient();
        RefreshSystemMediaState(forceTimeline: true);
        if (_systemMediaMetadataGeneration == _mediaVersion && _systemMediaMetadata is { } metadata)
            UpdateSystemMediaMetadata(_smtc, metadata);
    }

    // Release only this registration. An older player's close never reads or changes another VM's SMTC.
    private void ReleaseSystemMediaTransportControls(SystemMediaTransportControls? controls)
    {
        if (controls is null || !ReferenceEquals(controls, _systemMediaControls)) return;
        _systemMediaControls = null;
        Interlocked.Increment(ref _systemMediaEpoch);
        if (_systemMediaTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnSystemMediaTimelineTick;
            _systemMediaTimer = null;
        }
        PropertyChanged -= OnSystemMediaOwnerPropertyChanged;
        PlayNextCommand.CanExecuteChanged -= OnSystemMediaNavigationCanExecuteChanged;
        PlayPreviousCommand.CanExecuteChanged -= OnSystemMediaNavigationCanExecuteChanged;
        UnbindSystemMediaClient();
        controls.ButtonPressed -= OnSmtcButtonPressed;
        controls.PlaybackPositionChangeRequested -= OnSmtcPlaybackPositionChangeRequested;
        controls.PlaybackRateChangeRequested -= OnSmtcPlaybackRateChangeRequested;
        ApplySystemMediaState(controls, default, forceTimeline: true);
        ClearSystemMediaMetadata(controls);
        _systemMediaReadyGeneration = -1;
        if (ReferenceEquals(_smtc, controls)) _smtc = null;
    }

    private void ResetSystemMediaForMediaChange()
    {
        _systemMediaReadyGeneration = -1;
        _systemMediaPlaylistGeneration = -1;
        _systemMediaAwaitingInitializationReset = Player?.IsPlaybackInitialized == true;
        _systemMediaMetadata = null;
        _systemMediaMetadataGeneration = -1;
        _systemMediaTimeline.Reset();
        if (_systemMediaControls is not { } controls) return;
        var snapshot = ReadSystemMediaSnapshot() with
        {
            IsActive = !IsPlaybackClosing,
            CanAcceptInput = false,
            IsPlaybackInitialized = false,
            Status = IsPlaybackClosing ? SystemMediaPlaybackStatus.Closed : SystemMediaPlaybackStatus.Changing,
            PlaybackRate = 1,
        };
        ApplySystemMediaState(controls, snapshot, forceTimeline: true);
        ClearSystemMediaMetadata(controls);
    }

    private void OnSystemMediaOwnerPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MediaGeneration)
            || (args.PropertyName == nameof(IsConnecting) && IsConnecting))
        {
            ResetSystemMediaForMediaChange();
            return;
        }
        if (args.PropertyName == nameof(Player))
        {
            BindSystemMediaClient();
            if (Player?.IsPlaybackInitialized != true)
            {
                _systemMediaReadyGeneration = -1;
                _systemMediaAwaitingInitializationReset = false;
            }
        }
        if (args.PropertyName is nameof(Player) or nameof(IsConnecting) or nameof(CanSeek)
            or nameof(CanChangePlaybackRate) or nameof(CanNavigatePlaylist)
            or nameof(IsNextButtonEnabled) or nameof(IsPrevButtonEnabled))
        {
            RefreshSystemMediaState();
        }
    }

    private void OnSystemMediaNavigationCanExecuteChanged(object? sender, EventArgs args)
        => RefreshSystemMediaState();

    private void NotifySystemMediaPlaylistInitialized()
    {
        _systemMediaPlaylistGeneration = _mediaVersion;
        RefreshSystemMediaState();
    }

    // Called from the existing, coalesced UI-thread player-property path, before its asynchronous work.
    private void ObserveSystemMediaPlayerPropertyChanged(string? propertyName)
    {
        if (_systemMediaControls is null || !IsCurrentPlayback(_mediaVersion)) return;
        if (propertyName == nameof(Player.IsPlaybackInitialized))
        {
            if (!Player.IsPlaybackInitialized)
            {
                _systemMediaAwaitingInitializationReset = false;
                _systemMediaReadyGeneration = -1;
            }
            else if (!_systemMediaAwaitingInitializationReset)
            {
                _systemMediaReadyGeneration = _mediaVersion;
            }
        }
        if (propertyName is nameof(Player.Position) or nameof(Player.Duration)
            or nameof(Player.PlaybackState) or nameof(Player.PlaybackRate) or nameof(Player.IsPlaybackInitialized))
        {
            RefreshSystemMediaState(forceTimeline: propertyName != nameof(Player.Position));
        }
    }

    private void BindSystemMediaClient()
    {
        if (ReferenceEquals(_systemMediaClient, Client)) return;
        UnbindSystemMediaClient();
        if (_systemMediaControls is not null && Client is { IsDisposed: false } client)
        {
            _systemMediaClient = client;
            client.DataNotify += OnSystemMediaPlaybackRestart;
        }
    }

    private void UnbindSystemMediaClient()
    {
        if (_systemMediaClient is { } client) client.DataNotify -= OnSystemMediaPlaybackRestart;
        _systemMediaClient = null;
    }

    private void OnSystemMediaPlaybackRestart(object? sender, MpvClientNotifyEventArgs args)
    {
        if (args.Id != MpvClientEventId.PlaybackRestart) return;
        var controls = _systemMediaControls;
        var generation = Interlocked.Read(ref _mediaVersion);
        var epoch = Interlocked.Read(ref _systemMediaEpoch);
        queue.TryEnqueue(() =>
        {
            if (ReferenceEquals(sender, _systemMediaClient) && controls is not null
                && IsCurrentSystemMediaRequest(controls, generation, epoch))
                RefreshSystemMediaState(forceTimeline: true);
        });
    }

    private SystemMediaSnapshot ReadSystemMediaSnapshot()
    {
        var active = HasAdmittedMedia && IsCurrentPlayback(_mediaVersion);
        var input = active && !IsConnecting && !IsPlayerInputCancellationPending
            && Client?.IsDisposed == false && Player is not null;
        var initialized = _systemMediaReadyGeneration == _mediaVersion && Player?.IsPlaybackInitialized == true;
        var status = !active ? SystemMediaPlaybackStatus.Closed : IsConnecting ? SystemMediaPlaybackStatus.Changing
            : Player?.PlaybackState switch
            {
                MpvPlayerState.Playing => SystemMediaPlaybackStatus.Playing,
                MpvPlayerState.Paused => SystemMediaPlaybackStatus.Paused,
                MpvPlayerState.Buffering or MpvPlayerState.Seeking => SystemMediaPlaybackStatus.Changing,
                _ => SystemMediaPlaybackStatus.Stopped,
            };
        var playlist = _systemMediaPlaylistGeneration == _mediaVersion && Volatile.Read(ref _navigationBusy) == 0;
        return new(_mediaVersion, active, input, initialized, CanSeek, CanChangePlaybackRate, CanNavigatePlaylist,
            playlist && IsNextButtonEnabled && PlayNextCommand.CanExecute(null),
            playlist && IsPrevButtonEnabled && PlayPreviousCommand.CanExecute(null),
            status, Player?.Position ?? 0, Player?.Duration ?? 0, Player?.PlaybackRate ?? 1)
        {
            AllowRecoveryPauseOrSeek = active && !IsConnecting && IsAudioResumeSourceWaiting
                && Client?.IsDisposed == false && Player is not null,
        };
    }

    private void RefreshSystemMediaState(bool forceTimeline = false)
    {
        if (_systemMediaControls is { } controls)
            ApplySystemMediaState(controls, ReadSystemMediaSnapshot(), forceTimeline);
    }

    private void ApplySystemMediaState(SystemMediaTransportControls controls, SystemMediaSnapshot snapshot, bool forceTimeline)
    {
        var buttons = SystemMediaTransportPolicy.GetButtons(snapshot);
        if (_systemMediaButtons != buttons)
        {
            controls.IsPlayEnabled = buttons.Play;
            controls.IsPauseEnabled = buttons.Pause;
            controls.IsStopEnabled = buttons.Stop;
            controls.IsNextEnabled = buttons.Next;
            controls.IsPreviousEnabled = buttons.Previous;
            controls.IsFastForwardEnabled = buttons.FastForward;
            controls.IsRewindEnabled = buttons.Rewind;
            _systemMediaButtons = buttons;
        }
        var status = snapshot.IsActive ? snapshot.Status : SystemMediaPlaybackStatus.Closed;
        if (_systemMediaStatus != status)
        {
            controls.PlaybackStatus = status switch
            {
                SystemMediaPlaybackStatus.Playing => MediaPlaybackStatus.Playing,
                SystemMediaPlaybackStatus.Paused => MediaPlaybackStatus.Paused,
                SystemMediaPlaybackStatus.Changing => MediaPlaybackStatus.Changing,
                SystemMediaPlaybackStatus.Stopped => MediaPlaybackStatus.Stopped,
                _ => MediaPlaybackStatus.Closed,
            };
            controls.IsEnabled = snapshot.IsActive;
            _systemMediaStatus = status;
        }
        var rate = SystemMediaTransportPolicy.GetPlaybackRate(snapshot);
        if (_systemMediaRate != rate)
        {
            controls.PlaybackRate = rate;
            _systemMediaRate = rate;
        }
        if (_systemMediaTimeline.TryCreateUpdate(snapshot, Environment.TickCount64, forceTimeline, out var timeline))
        {
            controls.UpdateTimelineProperties(new()
            {
                StartTime = timeline.StartTime,
                EndTime = timeline.EndTime,
                MinSeekTime = timeline.MinSeekTime,
                MaxSeekTime = timeline.MaxSeekTime,
                Position = timeline.Position,
            });
        }
        if (_systemMediaTimer is { } timer)
        {
            var ticking = snapshot.IsActive && snapshot.Status == SystemMediaPlaybackStatus.Playing
                && SystemMediaTransportPolicy.CreateTimeline(snapshot).IsAvailable;
            if (ticking && !timer.IsRunning) timer.Start();
            else if (!ticking && timer.IsRunning) timer.Stop();
        }
    }

    private void OnSystemMediaTimelineTick(DispatcherQueueTimer sender, object args)
    {
        if (ReferenceEquals(sender, _systemMediaTimer)) RefreshSystemMediaState();
    }

    private void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        => QueueSystemMediaRequest(sender, args.Button switch
        {
            SystemMediaTransportControlsButton.Play => SystemMediaRequest.Play,
            SystemMediaTransportControlsButton.Pause => SystemMediaRequest.Pause,
            SystemMediaTransportControlsButton.Stop => SystemMediaRequest.Stop,
            SystemMediaTransportControlsButton.Next => SystemMediaRequest.Next,
            SystemMediaTransportControlsButton.Previous => SystemMediaRequest.Previous,
            SystemMediaTransportControlsButton.FastForward => SystemMediaRequest.FastForward,
            SystemMediaTransportControlsButton.Rewind => SystemMediaRequest.Rewind,
            _ => SystemMediaRequest.None,
        });

    private void OnSmtcPlaybackPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args)
        => QueueSystemMediaRequest(sender, SystemMediaRequest.PlaybackPosition, args.RequestedPlaybackPosition.TotalSeconds);

    private void OnSmtcPlaybackRateChangeRequested(SystemMediaTransportControls sender, PlaybackRateChangeRequestedEventArgs args)
        => QueueSystemMediaRequest(sender, SystemMediaRequest.PlaybackRate, args.RequestedPlaybackRate);

    private void QueueSystemMediaRequest(SystemMediaTransportControls sender, SystemMediaRequest request, double value = 0)
    {
        if (request == SystemMediaRequest.None) return;
        var generation = Interlocked.Read(ref _mediaVersion);
        var epoch = Interlocked.Read(ref _systemMediaEpoch);
        queue.TryEnqueue(() =>
        {
            if (IsCurrentSystemMediaRequest(sender, generation, epoch))
                _ = ObserveSystemMediaRequestAsync(sender, generation, epoch, request, value);
        });
    }

    private bool IsCurrentSystemMediaRequest(SystemMediaTransportControls sender, long generation, long epoch)
        => IsCurrentPlayback(generation) && epoch == Interlocked.Read(ref _systemMediaEpoch)
            && ReferenceEquals(sender, _systemMediaControls) && ReferenceEquals(sender, _smtc);

    private async Task ObserveSystemMediaRequestAsync(SystemMediaTransportControls sender, long generation, long epoch,
        SystemMediaRequest request, double value)
    {
        var snapshot = ReadSystemMediaSnapshot();
        var action = SystemMediaTransportPolicy.GetAction(request, SystemMediaTransportPolicy.GetButtons(snapshot));
        if (action == SystemMediaAction.None) return;
        try
        {
            await OwnWork(ExecuteSystemMediaActionAsync(action, snapshot, value));
        }
        catch (OperationCanceledException) when (!IsCurrentSystemMediaRequest(sender, generation, epoch)) { }
        catch (Exception ex)
        {
            if (IsCurrentSystemMediaRequest(sender, generation, epoch)) ReportPlaybackFailure(ex);
            else logger.LogWarning("A system media request ended after its player session changed.");
        }
        finally
        {
            if (IsCurrentSystemMediaRequest(sender, generation, epoch)) RefreshSystemMediaState(forceTimeline: true);
        }
    }

    private async Task ExecuteSystemMediaActionAsync(SystemMediaAction action, SystemMediaSnapshot snapshot, double value)
    {
        switch (action)
        {
            case SystemMediaAction.Play:
                if (CanSeek && Player?.PlaybackState is MpvPlayerState.Idle or MpvPlayerState.End) await ReplayAsync();
                else await SetUserPausedAsync(false);
                break;
            case SystemMediaAction.Pause:
                await SetUserPausedAsync(true);
                break;
            case SystemMediaAction.Next:
                await PlayNextCommand.ExecuteAsync(null);
                break;
            case SystemMediaAction.Previous:
                await PlayPreviousCommand.ExecuteAsync(null);
                break;
            case SystemMediaAction.SkipForward:
                await SkipForwardCommand.ExecuteAsync(null);
                break;
            case SystemMediaAction.SkipBackward:
                await SkipBackwardCommand.ExecuteAsync(null);
                break;
            case SystemMediaAction.Seek when SystemMediaTransportPolicy.TryGetSeekPosition(snapshot, value, out var position):
                await ChangePositionAsync(position);
                break;
            case SystemMediaAction.ChangeRate when SystemMediaTransportPolicy.IsSupportedPlaybackRate(value):
                await ChangeSpeedAsync(value);
                break;
        }
    }

    private void OnConnectorPropertiesUpdated(object? sender, PlayerInformationUpdatedEventArgs args)
    {
        if (!queue.HasThreadAccess)
        {
            var generation = Interlocked.Read(ref _mediaVersion);
            queue.TryEnqueue(() =>
            {
                if (IsCurrentPlayback(generation) && ReferenceEquals(sender, Connector))
                    OnConnectorPropertiesUpdated(sender, args);
            });
            return;
        }
        if (!IsCurrentPlayback(_mediaVersion) || !ReferenceEquals(sender, Connector)) return;
        if (IsLiveMedia)
        {
            Title = args.Title;
            NotifyLivePropertiesChanged();
        }
        _systemMediaMetadata = args;
        _systemMediaMetadataGeneration = _mediaVersion;
        if (_systemMediaControls is { } controls) UpdateSystemMediaMetadata(controls, args);
    }

    private static void UpdateSystemMediaMetadata(SystemMediaTransportControls controls, PlayerInformationUpdatedEventArgs args)
    {
        var updater = controls.DisplayUpdater;
        updater.Type = MediaPlaybackType.Video;
        updater.VideoProperties.Title = args.Title;
        updater.VideoProperties.Subtitle = args.Subtitle;
        updater.Thumbnail = args.Cover switch
        {
            RandomAccessStreamReference stream => stream,
            Uri uri => RandomAccessStreamReference.CreateFromUri(uri),
            _ => null,
        };
        updater.Update();
    }

    private static void ClearSystemMediaMetadata(SystemMediaTransportControls controls)
    {
        controls.DisplayUpdater.ClearAll();
        controls.DisplayUpdater.Type = MediaPlaybackType.Video;
        controls.DisplayUpdater.Update();
    }
}
