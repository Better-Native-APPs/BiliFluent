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

    // The network load that the standard quality replaced. The node failover looks at the same missing
    // track after the same delay; it leaves that load alone, or the file would be loaded twice.
    private long _automaticAudioFallbackLoad = -1;

    // The automatic choice is a Dolby or Hi-Res stream that the standard quality can still replace.
    private bool IsAutomaticSpecialAudioPending(MediaSourceResolverBase resolver)
        => _snapshot is { IsAutomaticAudioStandardOnly: false } snapshot && string.IsNullOrEmpty(snapshot.PreferAudioQualityId)
            && GetSpecialAudioName(GetResolverAudioSelections(resolver)) is not null;

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
        => SelectSourceAsync(source);

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

        var audioPreference = resolver.PlayingSelection is { } selected ? selected.Preferences.AudioQualityId : _snapshot?.PreferAudioQualityId;
        var isAuto = isVideoAuto && playing is not null && string.IsNullOrEmpty(audioPreference);
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
        WatchSeamlessPlayback();
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
            _automaticAudioFallbackLoad = _networkLoads;
            snapshot.IsAutomaticAudioStandardOnly = true;
            await RunWithoutPlayerInputAsync(async () =>
            {
                if (IsCurrent())
                {
                    CancelSeamlessSelection();
                    resolver.SetReloadSelection(new(new(snapshot.PreferQuality, null, snapshot.PreferAudioLanguageId, true),
                        GetPlayingFormat(resolver)?.Quality));
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
