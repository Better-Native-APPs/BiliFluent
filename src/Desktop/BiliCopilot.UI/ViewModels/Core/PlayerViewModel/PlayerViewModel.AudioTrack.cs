// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Resolvers;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using Richasy.BiliKernel.Models.Media;
using Richasy.WinUIKernel.Share.Base;
using System.Collections.ObjectModel;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    /// <summary>
    /// The audio tracks of the current media: best quality first, AI dubbing languages last.
    /// </summary>
    public ObservableCollection<AudioTrackItemViewModel> AudioTracks { get; } = [];

    /// <summary>
    /// Whether there is more than one track to choose from. The audio track button shows only then.
    /// </summary>
    [ObservableProperty]
    public partial bool IsAudioTrackSelectable { get; set; }

    [ObservableProperty]
    public partial AudioTrackItemViewModel? SelectedAudioTrack { get; set; }

    // Reads the tracks of the media the source resolver has just loaded.
    private void LoadAudioTracks()
    {
        var (tracks, currentId) = GetResolverAudioTracks(_sourceResolver);
        ApplyAudioTracks(tracks, currentId, keepRows: false);
    }

    // Episodes of one season are opened with a new snapshot. The chosen track follows to the next episode,
    // like it follows to another part of a video; an episode without that track plays its default one.
    private void CarryAudioTrackToEpisode(MediaSnapshot next)
    {
        if (_snapshot is { Type: BiliMediaType.Pgc, PreferAudioTrackId: { Length: > 0 } id }
            && next is { Type: BiliMediaType.Pgc, Episode: not null, PreferAudioTrackId: null })
        {
            next.PreferAudioTrackId = id;
        }
    }

    private static (List<AudioTrackInformation> Tracks, string? CurrentId) GetResolverAudioTracks(MediaSourceResolverBase? resolver)
        => resolver switch
        {
            VideoMediaSourceResolver video => video.GetAudioTracks(),
            PgcMediaSourceResolver pgc => pgc.GetAudioTracks(),
            _ => ([], default),
        };

    private static void ResetResolverAudioTrack(MediaSourceResolverBase? resolver, MediaSnapshot snapshot)
    {
        if (resolver is VideoMediaSourceResolver video)
        {
            video.ResetAudioTrack(snapshot);
        }
        else if (resolver is PgcMediaSourceResolver pgc)
        {
            pgc.ResetSnapshot(snapshot);
        }
    }

    private void ApplyAudioTracks(List<AudioTrackInformation> tracks, string? currentId, bool keepRows)
    {
        var version = _mediaVersion;

        // The default track sits at the top: standard qualities, then Dolby, Hi-Res, and the dubbing languages.
        var ordered = tracks
            .DistinctBy(track => track.Id)
            .OrderBy(track => track.Kind switch
            {
                AudioTrackKind.Standard => 0,
                AudioTrackKind.Dolby => 1,
                AudioTrackKind.HiRes => 2,
                _ => 3,
            })
            .ThenByDescending(track => track.Kind == AudioTrackKind.Standard ? track.Segment?.Bandwidth ?? 0 : 0)
            .ThenByDescending(track => track.Kind == AudioTrackKind.Standard ? track.Id : string.Empty, StringComparer.Ordinal)
            .Select(track => new AudioTrackItemViewModel(track, item => IsCurrentPlayback(version)
                ? OwnWork(SelectAudioTrackAsync(item)) : Task.CompletedTask))
            .ToList();

        // An open list keeps its rows when the same tracks come back, as they do after a switch.
        var unchanged = keepRows && ordered.Count == AudioTracks.Count
            && ordered.Zip(AudioTracks).All(pair => pair.First.Id == pair.Second.Id && pair.First.IsEnabled == pair.Second.IsEnabled);
        if (!unchanged)
        {
            AudioTracks.Clear();
            foreach (var item in ordered)
            {
                AudioTracks.Add(item);
            }
        }

        MarkSelectedAudioTrack(currentId);
        IsAudioTrackSelectable = AudioTracks.Count(item => item.IsEnabled) > 1;
    }

    private void MarkSelectedAudioTrack(string? id)
    {
        foreach (var item in AudioTracks)
        {
            item.IsSelected = item.Id == id;
        }

        SelectedAudioTrack = AudioTracks.FirstOrDefault(item => item.IsSelected);
    }

    private Task SelectAudioTrackAsync(AudioTrackItemViewModel track)
    {
        if (track is not { IsEnabled: true } || IsPlaybackClosing || IsConnecting || Player is null
            || !AudioTracks.Any(item => ReferenceEquals(item, track)) || ReferenceEquals(track, SelectedAudioTrack)
            || _sourceResolver is not (VideoMediaSourceResolver or PgcMediaSourceResolver))
        {
            return Task.CompletedTask;
        }

        var player = Player;
        var position = player.Position;
        var snapshot = _snapshot;
        var resolver = _sourceResolver;
        var version = _mediaVersion;
        return RunWithoutPlayerInputAsync(async () =>
        {
            if (!AudioTracks.Any(item => ReferenceEquals(item, track)) || ReferenceEquals(track, SelectedAudioTrack))
            {
                return;
            }

            // Only the stable ID is remembered. The resolver looks the address up again on every load.
            var previousId = snapshot.PreferAudioTrackId;
            var previousSelectedId = SelectedAudioTrack?.Id;
            snapshot.PreferAudioTrackId = track.Id;
            ResetResolverAudioTrack(resolver, snapshot);
            MarkSelectedAudioTrack(track.Id);

            // Same as a quality switch: replay consumes the captured position and restores the subtitle.
            await ReplayWithSubtitleRestoreAsync(player, position);
            if (!IsCurrentPlayback(version) || !ReferenceEquals(resolver, _sourceResolver))
            {
                return;
            }

            var (tracks, currentId) = GetResolverAudioTracks(resolver);
            if (currentId == track.Id)
            {
                ApplyAudioTracks(tracks, currentId, keepRows: true);
                return;
            }

            if (tracks.Count > 0 && !track.IsAI)
            {
                // Leaving a dubbing language loads the original audio again. When it has no such quality,
                // its default track is already playing, which is what "no choice" means.
                snapshot.PreferAudioTrackId = default;
                ApplyAudioTracks(tracks, currentId, keepRows: true);
                return;
            }

            // The dubbing did not come back with an address, or the request failed. Go back to the
            // previous track instead of leaving a choice that does not match what is playing.
            snapshot.PreferAudioTrackId = previousId;
            ResetResolverAudioTrack(resolver, snapshot);

            // Replay once more even when nothing new was loaded: the first attempt already retired the
            // danmaku and skip-segment bindings of the file that is playing, and only a load brings them back.
            await ReplayWithSubtitleRestoreAsync(player, position);
            if (!IsCurrentPlayback(version) || !ReferenceEquals(resolver, _sourceResolver))
            {
                return;
            }

            (tracks, currentId) = GetResolverAudioTracks(resolver);
            if (tracks.Count > 0)
            {
                ApplyAudioTracks(tracks, currentId, keepRows: true);
            }
            else
            {
                // Nothing is loaded. Keep the list, so the user can try again.
                MarkSelectedAudioTrack(previousSelectedId);
            }

            Window?.ShowTip(ResourceToolkit.GetLocalizedString(StringNames.AudioTrackSwitchFailed), InfoType.Error);
        }, nativeReplay: true);
    }
}
