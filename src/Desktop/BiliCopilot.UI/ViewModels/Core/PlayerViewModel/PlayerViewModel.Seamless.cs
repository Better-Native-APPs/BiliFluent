// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Resolvers;
using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Player;
using Richasy.MpvKernel.Player.Models;
using Richasy.WinUIKernel.Share.Base;
using Richasy.WinUIKernel.Share.Toolkits;
using System.Globalization;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private readonly SeamlessRequestSequence _seamlessRequests = new();
    private SelectionIntent? _selectionIntent;
    // Each execution cancels its predecessor; its own finally disposes the CTS after the library drains.
#pragma warning disable CA2213
    private CancellationTokenSource? _selectionCancellation;
#pragma warning restore CA2213
    private long _executingSelection;
    private long _seamlessLibraryRequest;
    private bool _selectionRunning;
    private Task _selectionWork = Task.CompletedTask;
    private bool _seamlessVideoSelected;
    private bool _onDemandReplayNeedsLoad;
    private bool _lastOnDemandReplayIssued;
    private MpvPlayer? _seamlessPlayer;
    private MpvSeamlessPlaybackState? _selectedPlaybackState;

    /// <summary>Whether the latest on-demand choice is being prepared, selected or reloaded.</summary>
    [ObservableProperty]
    public partial bool IsSeamlessSwitching { get; set; }

    /// <summary>The requested name; it remains available while a paused choice waits for resume.</summary>
    [ObservableProperty]
    public partial string? SeamlessSwitchTargetName { get; set; }

    /// <summary>Low-frequency progress for the latest library request only.</summary>
    [ObservableProperty]
    public partial MpvSeamlessSwitchPhase? SeamlessSwitchPhase { get; set; }

    /// <summary>The shared tier/stream entry. Automatic changes suppress tips, not existing recovery rules.</summary>
    internal Task SwitchOnDemandSelectionAsync(OnDemandSelectionRequest request, string targetName, bool silent = false)
    {
        var resolver = OnDemandResolver;
        if (!CanSelectOnDemandQuality || IsPlaybackClosing || IsConnecting || Player is not { } player
            || resolver is not (VideoMediaSourceResolver or PgcMediaSourceResolver))
            return Task.CompletedTask;

        PrepareAdaptiveSelection(ref request, silent);
        CancelSeamlessSelection();
        var intent = new SelectionIntent(_seamlessRequests.Next(), request, _mediaVersion, player, resolver, silent);
        _selectionIntent = intent;
        SeamlessSwitchTargetName = targetName;
        return _selectionWork = OwnWork(ExecuteSelectionAsync(intent));
    }

    private void CancelSeamlessSelection()
    {
        _seamlessRequests.Next();
        _selectionCancellation?.Cancel();
        _selectionIntent = null;
        _selectionRunning = false;
        IsSeamlessSwitching = false;
        SeamlessSwitchTargetName = null;
        SeamlessSwitchPhase = null;
    }

    private bool IsCurrentSelection(SelectionIntent intent)
        => CanSelectOnDemandQuality && _seamlessRequests.IsLatest(intent.Id) && IsCurrentPlayback(intent.Version)
            && ReferenceEquals(intent.Player, Player) && ReferenceEquals(intent.Resolver, _sourceResolver);

    private async Task ExecuteSelectionAsync(SelectionIntent intent)
    {
        if (!IsCurrentSelection(intent)) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_activeMediaToken);
        _selectionCancellation = cancellation;
        _selectionRunning = true;
        IsSeamlessSwitching = true;
        var entered = false;
        var defer = false;
        var reload = false;
        OnDemandPlaybackSelection? target = null;
        var primaryKind = intent.Request.Preferences.AudioQualityId != intent.Resolver.PlayingSelection?.Preferences.AudioQualityId
            && intent.Request.ResolvedVideoQuality == intent.Resolver.PlayingSelection?.Format.Quality
                ? MpvRelayStreamKind.Audio : MpvRelayStreamKind.Video;
        try
        {
            await _initializationGate.WaitAsync(cancellation.Token);
            entered = true;
            if (!IsCurrentSelection(intent)) return;
            await RestorePhysicalPlayerInputAsync();
            if (!IsCurrentSelection(intent)) return;
            var observation = await ReadPlaybackObservationAsync(intent.Player);
            if (!IsCurrentSelection(intent)) return;
            target = await intent.Resolver.DescribeSelectionAsync(intent.Request);
            if (!IsCurrentSelection(intent)) return;
            if (observation is { Paused: true } && (_onDemandReplayNeedsLoad || target is null || intent.Resolver.PlayingSelection is not { } playing
                || !playing.HasSameStream(PlaybackStreamKind.Video, target) || !playing.HasSameStream(PlaybackStreamKind.Audio, target)))
            {
                ShowSelectionTip(intent);
                defer = true;
                return;
            }
            if (target is null || intent.Resolver.PlayingSelection is null || _onDemandReplayNeedsLoad)
            {
                LogSeamlessResult(primaryKind, new(MpvSeamlessSwitchStatus.NotSupported, _onDemandReplayNeedsLoad
                    ? MpvSeamlessSwitchReason.PlaybackUnavailable : MpvSeamlessSwitchReason.MissingMetadata));
                reload = true;
            }
            else
            {
                if (primaryKind == MpvRelayStreamKind.Video) SeamlessSwitchTargetName = target.Format.Description;
                ShowSelectionTip(intent);
                foreach (var kind in new[] { PlaybackStreamKind.Video, PlaybackStreamKind.Audio })
                {
                    if (!IsCurrentSelection(intent)) return;
                    if (intent.Resolver.PlayingSelection is not { } current || current.HasSameStream(kind, target)) continue;
                    var relayKind = ToRelayKind(kind);
                    var description = kind == PlaybackStreamKind.Video ? target.Video : target.Audio;
                    var retries = 0;
                    while (true)
                    {
                        observation = await ReadPlaybackObservationAsync(intent.Player);
                        if (!IsCurrentSelection(intent)) return;
                        var file = Interlocked.Read(ref _automaticAudioLoads);
                        MpvSeamlessSwitchResult result;
                        if (observation is { Paused: true })
                            result = new(MpvSeamlessSwitchStatus.NotSupported, MpvSeamlessSwitchReason.Paused);
                        else if (!SeamlessSwitchPolicy.CanAttempt(CanSelectOnDemandQuality, intent.Player.IsPlaybackInitialized,
                            observation is { Playing: true, Seeking: false }, false, intent.Player.IsRelaying(relayKind), description?.Target is not null))
                            result = new(MpvSeamlessSwitchStatus.NotSupported, description?.Target is null
                                ? MpvSeamlessSwitchReason.MissingMetadata : MpvSeamlessSwitchReason.PlaybackUnavailable);
                        else if ((current.Video?.Target?.Restrictions ?? MpvSeamlessRestrictions.None) != MpvSeamlessRestrictions.None
                            || (current.Audio?.Target?.Restrictions ?? MpvSeamlessRestrictions.None) != MpvSeamlessRestrictions.None)
                            result = new(MpvSeamlessSwitchStatus.NotSupported, MpvSeamlessSwitchReason.RestrictedFormat);
                        else
                        {
                            Volatile.Write(ref _executingSelection, intent.Id);
                            SeamlessSwitchPhase = MpvSeamlessSwitchPhase.Preparing;
                            result = kind == PlaybackStreamKind.Video
                                ? await intent.Player.SwitchVideoAsync(description!.Target!, cancellation.Token)
                                : await intent.Player.SwitchAudioAsync(description!.Target!, cancellation.Token);
                        }

                        LogSeamlessResult(relayKind, result);
                        var sameFile = file == Interlocked.Read(ref _automaticAudioLoads) && IsCurrentPlayback(intent.Version)
                            && ReferenceEquals(intent.Player, Player) && ReferenceEquals(intent.Resolver, _sourceResolver);
                        // Selection is irreversible. Even a just-superseded success must describe the real
                        // intermediate track; it never commits that old request's preferences or starts a reload.
                        if (result.IsSuccess && sameFile)
                        {
                            if (kind == PlaybackStreamKind.Video) _seamlessVideoSelected = true;
                            var state = await intent.Player.GetSeamlessPlaybackStateAsync(_activeMediaToken);
                            if (state.IsSuccess)
                            {
                                _selectedPlaybackState = state.Value;
                                var selected = kind == PlaybackStreamKind.Video ? state.Value.Video : state.Value.Audio;
                                if (selected.TrackId != result.TrackId) return;
                            }

                            var statistics = state.IsSuccess ? (kind == PlaybackStreamKind.Video ? state.Value.Video : state.Value.Audio).Statistics : null;
                            intent.Resolver.CommitSelectedStream(kind, target, statistics);
                            if (kind == PlaybackStreamKind.Video) OnAdaptiveSelectionCommitted(intent, current.Format.Quality, target.Format.Quality);
                            Interlocked.Increment(ref _selectedStreamEpoch);
                            ResetSelectedRelayObservation();
                            _failover.OnStreamsChanged(NetworkNow);
                            ApplyCurrentOnDemandSelection(intent.Resolver);
                        }

                        var after = await ReadPlaybackObservationAsync(intent.Player);
                        var stable = after is { Playing: true, Paused: false, Seeking: false } && observation is { } before
                            && Math.Abs(after.Value.Speed - before.Speed) <= 0.001;
                        var action = SeamlessSwitchPolicy.Decide(result, kind == PlaybackStreamKind.Video, retries,
                            IsCurrentSelection(intent), sameFile, stable);
                        if (action == SeamlessSwitchAction.RetryVideo)
                        {
                            retries++;
                            continue;
                        }

                        if (action == SeamlessSwitchAction.Defer) defer = true;
                        else if (action == SeamlessSwitchAction.Reload) reload = true;
                        else if (action == SeamlessSwitchAction.Ignore) return;
                        break;
                    }

                    if (defer || reload) break;
                }

                if (!defer && !reload && IsCurrentSelection(intent))
                {
                    intent.Resolver.CommitSelectionPreferences(target.Preferences);
                    ApplyCurrentOnDemandSelection(intent.Resolver);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || _activeMediaToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning("Playback | seamless target resolution failed error={Error}", ex.GetType().Name);
            if (IsCurrentSelection(intent))
            {
                LogSeamlessResult(primaryKind, new(MpvSeamlessSwitchStatus.Failed, MpvSeamlessSwitchReason.PreparationFailed));
                reload = true;
            }
        }
        finally
        {
            if (entered) _initializationGate.Release();
            if (ReferenceEquals(_selectionCancellation, cancellation)) _selectionCancellation = null;
            if (Volatile.Read(ref _executingSelection) == intent.Id) Volatile.Write(ref _executingSelection, 0);
            if (IsCurrentSelection(intent) && !reload)
            {
                _selectionRunning = false;
                IsSeamlessSwitching = false;
                SeamlessSwitchPhase = null;
                if (!defer)
                {
                    _selectionIntent = null;
                    SeamlessSwitchTargetName = null;
                }
            }
        }

        if (reload && IsCurrentSelection(intent))
        {
            try
            {
                // Once the serial replay boundary is admitted, a load cannot be retracted. New
                // choices wait for it, then describe the stream that really loaded, not the initial source.
                await RunWithoutPlayerInputAsync(async () =>
                {
                    if (!IsCurrentPlayback(intent.Version) || !ReferenceEquals(intent.Player, Player)
                        || !ReferenceEquals(intent.Resolver, _sourceResolver)) return;
                    intent.Resolver.SetReloadSelection(target?.ForReload() ?? intent.Request);
                    OnAdaptiveSelectionReloading(intent, target?.Format.Quality ?? intent.Request.ResolvedVideoQuality);
                    intent.Resolver.RefreshPlayUrl();
                    var current = await ReadPlaybackObservationAsync(intent.Player);
                    var loaded = await ReplayWithSubtitleRestoreAsync(intent.Player, current?.Position ?? intent.Player.Position);
                    if (!loaded && !_lastOnDemandReplayIssued && IsCurrentSelection(intent) && intent.Resolver.PlayingSelection is { } previous)
                    {
                        // No loadfile was issued, but the replay boundary retired its overlays. Preserve
                        // the existing audio-failure recovery: try the actual previous pair once.
                        intent.Resolver.SetReloadSelection(previous.ForReload());
                        intent.Resolver.RefreshPlayUrl();
                        await ReplayWithSubtitleRestoreAsync(intent.Player, current?.Position ?? intent.Player.Position);
                    }

                    if (loaded && intent.Resolver.PlayingSelection is { } loadedVideo)
                        OnAdaptiveSelectionCommitted(intent, intent.PreviousQuality, loadedVideo.Format.Quality);

                    var actualAudio = intent.Resolver.PlayingSelection?.AudioSelections.QualityId;
                    var requestedAudio = intent.Request.Preferences.AudioQualityId;
                    var audioMatched = requestedAudio is null || actualAudio == requestedAudio
                        || (requestedAudio.StartsWith("std:", StringComparison.Ordinal) && actualAudio?.StartsWith("std:", StringComparison.Ordinal) == true);
                    if (!audioMatched && primaryKind == MpvRelayStreamKind.Audio)
                    {
                        if (loaded && intent.Resolver.PlayingSelection is { } playing)
                            intent.Resolver.CommitSelectionPreferences(playing.Preferences with
                            { VideoQuality = intent.PreviousPreferences.VideoQuality, AudioQualityId = null });
                        if (!intent.Silent && IsCurrentSelection(intent))
                            Window?.ShowTip(ResourceToolkit.GetLocalizedString(StringNames.AudioQualitySwitchFailed), InfoType.Error);
                    }

                    ApplyCurrentOnDemandSelection(intent.Resolver);
                }, nativeReplay: true);
            }
            finally
            {
                OnAdaptiveSelectionFinished(intent, _lastOnDemandReplayIssued);
                if (IsCurrentSelection(intent))
                {
                    _selectionIntent = null;
                    _selectionRunning = false;
                    IsSeamlessSwitching = false;
                    SeamlessSwitchTargetName = null;
                    SeamlessSwitchPhase = null;
                }
            }
        }
    }

    private void ContinueDeferredSelection(bool paused, bool seeking)
    {
        if (paused || seeking || _selectionRunning || _selectionIntent is not { } intent || !IsCurrentSelection(intent)
            || intent.Player.IsPlaybackInitialized != true || !intent.Player.IsPlaying) return;
        _selectionWork = OwnWork(ExecuteSelectionAsync(intent));
    }

    private Task CancelAndDrainSeamlessSelectionAsync()
    {
        var work = _selectionWork;
        CancelSeamlessSelection();
        return JoinAsync();
        async Task JoinAsync()
        {
            // This is the actual admitted selection, never the VM's whole owned-work set.
#pragma warning disable VSTHRD003
            await work;
#pragma warning restore VSTHRD003
        }
    }

    private async Task SeekSelectedOnDemandAsync(double position, long version, Func<Task> seek)
    {
        var player = Player;
        if (player is null || !CanSeek || !IsCurrentPlayback(version)) return;
        var reload = false;
        var entered = false;
        try
        {
            await _initializationGate.WaitAsync(_activeMediaToken);
            entered = true;
            if (!CanSeek || !IsCurrentPlayback(version) || !ReferenceEquals(player, Player)) return;
            var state = await player.GetSeamlessPlaybackStateAsync(_activeMediaToken);
            if (!IsCurrentPlayback(version)) return;
            if (state.IsSuccess)
            {
                _selectedPlaybackState = state.Value;
                reload = _onDemandReplayNeedsLoad || SeamlessSwitchPolicy.RequiresReloadForSeek(position, state.Value.Video.IsExternal, state.Value.Video.MinimumSeekTimeSeconds);
            }
            else reload = _onDemandReplayNeedsLoad || _seamlessVideoSelected;
            if (!reload) await seek();
        }
        finally
        {
            if (entered) _initializationGate.Release();
        }

        if (reload && IsCurrentPlayback(version)) await ReplayAsync(position);
    }

    private void ApplyCurrentOnDemandSelection(MediaSourceResolverBase resolver)
    {
        if (!CanSelectOnDemandQuality || !ReferenceEquals(resolver, _sourceResolver) || resolver.PlayingSelection is not { } playing) return;
        _snapshot.PreferQuality = playing.Preferences.VideoQuality;
        _snapshot.PreferAudioQualityId = playing.Preferences.AudioQualityId;
        _snapshot.PreferAudioLanguageId = playing.Preferences.AudioLanguageId;
        _snapshot.IsAutomaticAudioStandardOnly = playing.Preferences.StandardAudioOnly;
        ApplyAudioSelections(GetResolverAudioSelections(resolver), keepRows: true);
    }

    private void ShowSelectionTip(SelectionIntent intent)
    {
        if (intent.Silent || intent.TipShown || !IsCurrentSelection(intent)) return;
        intent.TipShown = true;
        var template = this.Get<IResourceToolkit>().GetLocalizedString("SeamlessSwitchingTemplate");
        Window?.ShowTip(string.Format(CultureInfo.CurrentCulture, template, SeamlessSwitchTargetName), InfoType.Information);
    }

    private void WatchSeamlessPlayback()
    {
        var player = CanSelectOnDemandQuality ? Player : null;
        if (ReferenceEquals(player, _seamlessPlayer)) return;
        DetachSeamlessPlayback();
        _seamlessPlayer = player;
        if (player is null) return;
        player.SeamlessSwitchProgress += OnSeamlessSwitchProgress;
        player.SeamlessRelayUpstreamFailed += OnSeamlessRelayUpstreamFailed;
    }

    private void DetachSeamlessPlayback()
    {
        if (_seamlessPlayer is { } previous)
        {
            previous.SeamlessSwitchProgress -= OnSeamlessSwitchProgress;
            previous.SeamlessRelayUpstreamFailed -= OnSeamlessRelayUpstreamFailed;
        }

        _seamlessPlayer = null;
        _selectedPlaybackState = null;
        _seamlessVideoSelected = false;
        _seamlessLibraryRequest = 0;
    }

    private void OnSeamlessSwitchProgress(object? sender, MpvSeamlessSwitchEventArgs e)
    {
        var request = Volatile.Read(ref _executingSelection);
        queue.TryEnqueue(() =>
        {
            if (request == 0 || Volatile.Read(ref _executingSelection) != request || _selectionIntent is not { } intent || intent.Id != request || !IsCurrentSelection(intent)
                || !ReferenceEquals(sender, Player) || e.RequestId < _seamlessLibraryRequest) return;
            _seamlessLibraryRequest = e.RequestId;
            SeamlessSwitchPhase = e.Phase;
        });
    }

    private void OnSeamlessRelayUpstreamFailed(object? sender, MpvRelayUpstreamFailedEventArgs e)
        => OnRelayUpstreamFailed(sender, e);

    private void LogSeamlessResult(MpvRelayStreamKind kind, MpvSeamlessSwitchResult result)
    {
        if (!logger.IsEnabled(LogLevel.Information)) return;
        logger.LogInformation("Playback | seamless-switch kind={Kind} result={Result} reason={Reason} prep-ms={PreparationMs} wait-ms={WaitMs} failovers={Failovers}",
            kind == MpvRelayStreamKind.Video ? "video" : "audio", result.Status, result.Reason,
            result.Preparation.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture),
            result.BoundaryWait.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture), result.PreparationFailovers);
    }

    private static Task<PlaybackObservation?> ReadPlaybackObservationAsync(MpvPlayer player)
        => Task.Run<PlaybackObservation?>(() =>
        {
            var client = player.Client;
            if (client.IsDisposed || client.GetProperty("pause", out bool paused) != MpvError.Success
                || client.GetProperty("seeking", out bool seeking) != MpvError.Success
                || client.GetProperty("core-idle", out bool idle) != MpvError.Success
                || client.GetProperty("speed", out double speed) != MpvError.Success || !double.IsFinite(speed) || speed <= 0
                || client.GetProperty("time-pos", out double position) != MpvError.Success || !double.IsFinite(position)) return null;
            return new(paused, seeking, !idle, speed, position);
        });

    private readonly record struct PlaybackObservation(bool Paused, bool Seeking, bool Playing, double Speed, double Position);

    partial void PrepareAdaptiveSelection(ref OnDemandSelectionRequest request, bool silent);
    partial void OnAdaptiveSelectionCommitted(SelectionIntent intent, int fromQuality, int toQuality);
    partial void OnAdaptiveSelectionReloading(SelectionIntent intent, int? targetQuality);
    partial void OnAdaptiveSelectionFinished(SelectionIntent intent, bool loadIssued);

    private sealed class SelectionIntent(long id, OnDemandSelectionRequest request, long version,
        MpvPlayer player, MediaSourceResolverBase resolver, bool silent)
    {
        public long Id { get; } = id;
        public OnDemandSelectionRequest Request { get; } = request;
        public OnDemandSelectionPreferences PreviousPreferences { get; } = resolver.PlayingSelection?.Preferences ?? request.Preferences;
        public int PreviousQuality { get; } = resolver.PlayingSelection?.Format.Quality ?? 0;
        public bool Silent { get; } = silent;
        public long Version { get; } = version;
        public MpvPlayer Player { get; } = player;
        public MediaSourceResolverBase Resolver { get; } = resolver;
        public bool TipShown { get; set; }
    }
}
