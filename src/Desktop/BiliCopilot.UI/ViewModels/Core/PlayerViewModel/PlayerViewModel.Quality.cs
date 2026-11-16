// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Resolvers;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using Richasy.BiliKernel.Models.Media;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Core.Enums;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    // How long a Dolby or Hi-Res stream may take to show up among the tracks after the file is loaded.
    private static readonly TimeSpan AutomaticAudioGrace = TimeSpan.FromSeconds(1.5);

    private MpvClient? _automaticAudioClient;

    // Counts the files that start and finish loading, so a check that waited can tell that another load began.
    private long _automaticAudioLoads;

    /// <summary>
    /// Whether the quality is automatic: neither a video quality nor an audio quality is chosen by hand.
    /// The automatic entry of the quality menu is the selected one then.
    /// </summary>
    [ObservableProperty]
    public partial bool IsQualityAuto { get; set; }

    // One "automatic" stands for the video quality and the audio quality together:
    //   - the automatic entry puts both back to automatic;
    //   - a video quality is kept for the media that follow, and keeps the audio that plays for this playback;
    //   - an audio quality is kept for this playback, and keeps the video quality that plays for this playback.
    // So a media that follows a video quality chosen by hand plays that quality with the best audio.
    private Task SelectQualityEntryAsync(SourceItemViewModel source)
    {
        if (!CanSelectOnDemandQuality) return Task.CompletedTask;
        var resolver = OnDemandResolver;
        if (source is not { IsEnabled: true } || IsPlaybackClosing || IsConnecting || !Sources.Contains(source) || Player is null
            || resolver is not (VideoMediaSourceResolver or PgcMediaSourceResolver))
        {
            return SelectSourceAsync(source);
        }

        var player = Player;
        var position = player.Position;
        var snapshot = _snapshot;
        var version = _mediaVersion;
        var audioBefore = GetResolverAudioSelections(resolver).QualityId;
        var videoBefore = GetPlayingFormat(resolver)?.Quality;

        snapshot.PreferAudioQualityId = source.IsAuto ? default : snapshot.PreferAudioQualityId ?? audioBefore;
        ResetResolverAudioSelection(resolver, snapshot, reloadLanguage: false);
        var audioAfter = GetResolverAudioSelections(resolver);

        // The video part of the choice names the automatic entry again, so it has to know the audio first.
        foreach (var item in Sources.Where(item => item.IsAuto))
        {
            item.AutoAudioName = source.IsAuto ? GetSpecialAudioName(audioAfter) : default;
        }

        var videoTask = SelectSourceAsync(source);
        var isVideoReplayed = GetPlayingFormat(resolver) is { } videoAfter && videoAfter.Quality != videoBefore;
        if (isVideoReplayed)
        {
            // That replay loads the audio as well.
            return FinishAsync();
        }

        ApplyAudioSelections(audioAfter, keepRows: true);
        if (audioAfter.QualityId == audioBefore)
        {
            return videoTask;
        }

        // Only the audio changes: "automatic" was chosen while its video quality already plays.
        return RunWithoutPlayerInputAsync(async () =>
        {
            if (!IsCurrentPlayback(version) || !ReferenceEquals(resolver, _sourceResolver))
            {
                return;
            }

            await ReplayWithSubtitleRestoreAsync(player, position);
            if (IsCurrentPlayback(version) && ReferenceEquals(resolver, _sourceResolver))
            {
                ApplyAudioSelections(GetResolverAudioSelections(resolver), keepRows: true);
            }
        }, nativeReplay: true);

        async Task FinishAsync()
        {
            // The task is the replay this very call has just started.
#pragma warning disable VSTHRD003
            await videoTask;
#pragma warning restore VSTHRD003
            if (IsCurrentPlayback(version) && ReferenceEquals(resolver, _sourceResolver))
            {
                ApplyAudioSelections(GetResolverAudioSelections(resolver), keepRows: true);
            }
        }
    }

    // Marks the entry of the quality list that is in effect. The automatic entry is the one only while both
    // the video and the audio quality are automatic; it is named after what it picked, such as "Auto (1080P)",
    // or "Auto (1080P · Hi-Res lossless)" when it picked Dolby or Hi-Res.
    private void MarkQualityMenu()
    {
        if (!CanSelectOnDemandQuality) return;
        var resolver = OnDemandResolver;
        if (resolver is null) return;
        var playing = GetPlayingFormat(resolver);
        var isVideoAuto = resolver switch
        {
            VideoMediaSourceResolver video => video.IsAutoQuality(),
            PgcMediaSourceResolver pgc => pgc.IsAutoQuality(),
            _ => false,
        };

        var isAuto = isVideoAuto && playing is not null && string.IsNullOrEmpty(_snapshot?.PreferAudioQualityId);
        var audioName = isAuto ? GetSpecialAudioName(GetResolverAudioSelections(resolver)) : default;
        foreach (var item in Sources)
        {
            if (item.IsAuto)
            {
                item.AutoAudioName = audioName;
                item.ShowAutoQuality(isAuto ? playing : default);
                item.IsSelected = isAuto;
            }
            else
            {
                item.IsSelected = !isAuto && item.Data.Quality == playing?.Quality;
            }
        }

        SelectedSource = Sources.FirstOrDefault(item => item.IsSelected);
        IsQualityAuto = isAuto;
    }

    // The name of the audio quality that plays when it is Dolby or Hi-Res. Null for the standard quality.
    private static string? GetSpecialAudioName(AudioSelections selections)
        => selections.Qualities.FirstOrDefault(quality => quality.Id == selections.QualityId) is { Kind: not AudioQualityKind.Standard } playing
            ? AudioQualityItemViewModel.GetDisplayName(playing)
            : default;

    // The automatic choice may be Dolby or Hi-Res. When that stream does not load, the standard quality
    // plays instead, without a tip. The check runs each time a file is loaded, so the client is watched
    // before the first load starts.
    private void WatchAutomaticAudio()
    {
        var client = CanSelectOnDemandQuality && OnDemandResolver is not null ? Client : null;
        if (ReferenceEquals(client, _automaticAudioClient))
        {
            return;
        }

        if (_automaticAudioClient is { IsDisposed: false } previous)
        {
            previous.ReachFileLoading -= OnAutomaticAudioFileLoading;
            previous.ReachFileLoaded -= OnAutomaticAudioFileLoaded;
        }

        _automaticAudioClient = client;
        if (client is not null)
        {
            client.ReachFileLoading += OnAutomaticAudioFileLoading;
            client.ReachFileLoaded += OnAutomaticAudioFileLoaded;
        }
    }

    private void OnAutomaticAudioFileLoading(object? sender, EventArgs e)
        => Interlocked.Increment(ref _automaticAudioLoads);

    private void OnAutomaticAudioFileLoaded(object? sender, EventArgs e)
    {
        var load = Interlocked.Increment(ref _automaticAudioLoads);
        var version = _mediaVersion;
        queue.TryEnqueue(() =>
        {
            if (IsCurrentPlayback(version) && ReferenceEquals(sender, Client))
            {
                _ = OwnWork(VerifyAutomaticAudioAsync(version, load));
            }
        });
    }

    private async Task VerifyAutomaticAudioAsync(long version, long load)
    {
        if (!CanSelectOnDemandQuality || OnDemandResolver is null) return;
        var snapshot = _snapshot;
        var resolver = OnDemandResolver;
        var player = Player;
        var client = Client;
        var token = _activeMediaToken;
        bool IsCurrent() => IsCurrentPlayback(version) && Interlocked.Read(ref _automaticAudioLoads) == load
            && ReferenceEquals(resolver, _sourceResolver) && ReferenceEquals(player, Player) && ReferenceEquals(client, Client) && ReferenceEquals(snapshot, _snapshot);
        bool IsAutomaticSpecial() => !snapshot.IsAutomaticAudioStandardOnly && string.IsNullOrEmpty(snapshot.PreferAudioQualityId)
            && GetSpecialAudioName(GetResolverAudioSelections(resolver)) is not null;

        try
        {
            if (snapshot is null || player is null || client is null || !IsCurrent() || !IsAutomaticSpecial() || await HasAudioTrackAsync())
            {
                return;
            }

            // The stream is opened while the file loads. Look once more before giving it up.
            await Task.Delay(AutomaticAudioGrace, token);
            if (!IsCurrent() || !IsAutomaticSpecial() || await HasAudioTrackAsync() || !IsCurrent() || !IsAutomaticSpecial())
            {
                return;
            }

            var loaded = await client.GetCurrentPositionAsync();
            if (!IsCurrent() || !IsAutomaticSpecial())
            {
                return;
            }

            var position = loaded.IsSuccess && double.IsFinite(loaded.Value) && loaded.Value > 0 ? loaded.Value : player.Position;
            snapshot.IsAutomaticAudioStandardOnly = true;
            ResetResolverAudioSelection(resolver, snapshot, reloadLanguage: false);
            ApplyAudioSelections(GetResolverAudioSelections(resolver), keepRows: true);
            await RunWithoutPlayerInputAsync(async () =>
            {
                if (IsCurrent())
                {
                    await ReplayWithSubtitleRestoreAsync(player, position);
                }
            }, nativeReplay: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }

        // An unreadable track list says nothing, so it counts as loaded.
        async Task<bool> HasAudioTrackAsync()
        {
            var tracks = await client!.GetTracksAsync();
            return !tracks.IsSuccess || tracks.Value.Any(track => track.Type == MpvTrackType.Audio);
        }
    }
}
