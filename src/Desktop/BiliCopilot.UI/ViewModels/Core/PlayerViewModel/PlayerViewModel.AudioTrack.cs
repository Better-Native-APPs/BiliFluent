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
    /// The audio languages of the current media: the original audio first, then the dubbing languages the API offers.
    /// </summary>
    public ObservableCollection<AudioLanguageItemViewModel> AudioLanguages { get; } = [];

    /// <summary>
    /// The audio qualities of the language that plays: best standard quality first, then Dolby and Hi-Res.
    /// </summary>
    public ObservableCollection<AudioQualityItemViewModel> AudioQualities { get; } = [];

    /// <summary>
    /// Whether there is a language besides the original audio. The audio track button shows only then.
    /// </summary>
    [ObservableProperty]
    public partial bool HasMultipleAudioLanguages { get; set; }

    /// <summary>
    /// Whether there is more than one quality to choose from. The quality section shows only then.
    /// </summary>
    [ObservableProperty]
    public partial bool HasMultipleAudioQualities { get; set; }

    /// <summary>
    /// The title the API gives to its dubbing languages, shown as it is. Null when it gives none.
    /// </summary>
    [ObservableProperty]
    public partial string? AudioLanguageGroupTitle { get; set; }

    [ObservableProperty]
    public partial AudioLanguageItemViewModel? SelectedAudioLanguage { get; set; }

    [ObservableProperty]
    public partial AudioQualityItemViewModel? SelectedAudioQuality { get; set; }

    // Reads the audio choices of the media the source resolver has just loaded.
    private void LoadAudioSelections()
        => ApplyAudioSelections(GetResolverAudioSelections(_sourceResolver), keepRows: false);

    // Episodes of one season are opened with a new snapshot. The audio choices follow to the next episode,
    // like they follow to another part of a video; an episode without them plays its default audio.
    private void CarryAudioSelectionToEpisode(MediaSnapshot next)
    {
        if (_snapshot is { Type: BiliMediaType.Pgc } current && next is { Type: BiliMediaType.Pgc, Episode: not null })
        {
            next.PreferAudioLanguageId ??= current.PreferAudioLanguageId;
            next.PreferAudioQualityId ??= current.PreferAudioQualityId;
        }
    }

    private static AudioSelections GetResolverAudioSelections(MediaSourceResolverBase? resolver)
        => resolver switch
        {
            VideoMediaSourceResolver video => video.GetAudioSelections(),
            PgcMediaSourceResolver pgc => pgc.GetAudioSelections(),
            _ => AudioSelections.Empty,
        };

    private static void ResetResolverAudioSelection(MediaSourceResolverBase? resolver, MediaSnapshot snapshot, bool reloadLanguage)
    {
        if (resolver is VideoMediaSourceResolver video)
        {
            video.ResetAudioSelection(snapshot, reloadLanguage);
        }
        else if (resolver is PgcMediaSourceResolver pgc)
        {
            pgc.ResetSnapshot(snapshot);
        }
    }

    private void ApplyAudioSelections(AudioSelections selections, bool keepRows)
    {
        var version = _mediaVersion;
        var languages = selections.Languages
            .DistinctBy(language => language.Id)
            .Select(language => new AudioLanguageItemViewModel(language, selections.LanguageGroupTitle, item => IsCurrentPlayback(version)
                ? OwnWork(SelectAudioLanguageAsync(item)) : Task.CompletedTask))
            .ToList();

        // The default quality sits at the top: standard qualities, then Dolby and Hi-Res.
        var qualities = selections.Qualities
            .DistinctBy(quality => quality.Id)
            .OrderBy(quality => quality.Kind switch
            {
                AudioQualityKind.Standard => 0,
                AudioQualityKind.Dolby => 1,
                _ => 2,
            })
            .ThenByDescending(quality => quality.Kind == AudioQualityKind.Standard ? quality.Segment?.Bandwidth ?? 0 : 0)
            .ThenByDescending(quality => quality.Kind == AudioQualityKind.Standard ? quality.Id : string.Empty, StringComparer.Ordinal)
            .Select(quality => new AudioQualityItemViewModel(quality, item => IsCurrentPlayback(version)
                ? OwnWork(SelectAudioQualityAsync(item)) : Task.CompletedTask))
            .ToList();

        // An open list keeps its rows when the same entries come back, as they do after a switch.
        if (!keepRows || languages.Count != AudioLanguages.Count
            || !languages.Zip(AudioLanguages).All(pair => pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name
                && pair.First.GroupTitle == pair.Second.GroupTitle && pair.First.IsEnabled == pair.Second.IsEnabled))
        {
            AudioLanguages.Clear();
            foreach (var item in languages)
            {
                AudioLanguages.Add(item);
            }
        }

        if (!keepRows || qualities.Count != AudioQualities.Count
            || !qualities.Zip(AudioQualities).All(pair => pair.First.Id == pair.Second.Id && pair.First.IsEnabled == pair.Second.IsEnabled))
        {
            AudioQualities.Clear();
            foreach (var item in qualities)
            {
                AudioQualities.Add(item);
            }
        }

        MarkSelectedAudioLanguage(selections.LanguageId);
        MarkSelectedAudioQuality(selections.QualityId);
        AudioLanguageGroupTitle = selections.LanguageGroupTitle;
        HasMultipleAudioLanguages = AudioLanguages.Count > 1;
        HasMultipleAudioQualities = AudioQualities.Count(item => item.IsEnabled) > 1;
    }

    private void MarkSelectedAudioLanguage(string? id)
    {
        foreach (var item in AudioLanguages)
        {
            item.IsSelected = item.Id == id;
        }

        SelectedAudioLanguage = AudioLanguages.FirstOrDefault(item => item.IsSelected);
    }

    private void MarkSelectedAudioQuality(string? id)
    {
        foreach (var item in AudioQualities)
        {
            item.IsSelected = item.Id == id;
        }

        SelectedAudioQuality = AudioQualities.FirstOrDefault(item => item.IsSelected);
    }

    private Task SelectAudioLanguageAsync(AudioLanguageItemViewModel language)
    {
        if (language is not { IsEnabled: true } || IsPlaybackClosing || IsConnecting || Player is null
            || !AudioLanguages.Any(item => ReferenceEquals(item, language)) || ReferenceEquals(language, SelectedAudioLanguage)
            || _sourceResolver is not VideoMediaSourceResolver)
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
            if (!AudioLanguages.Any(item => ReferenceEquals(item, language)) || ReferenceEquals(language, SelectedAudioLanguage))
            {
                return;
            }

            // Only the language code is remembered. The resolver requests the play detail again with it.
            var previousId = snapshot.PreferAudioLanguageId;
            var previousSelectedId = SelectedAudioLanguage?.Id;
            snapshot.PreferAudioLanguageId = language.Id;
            ResetResolverAudioSelection(resolver, snapshot, reloadLanguage: true);
            MarkSelectedAudioLanguage(language.Id);

            // Same as a quality switch: replay consumes the captured position and restores the subtitle.
            await ReplayWithSubtitleRestoreAsync(player, position);
            if (!IsCurrentPlayback(version) || !ReferenceEquals(resolver, _sourceResolver))
            {
                return;
            }

            var selections = GetResolverAudioSelections(resolver);
            if (selections.LanguageId == language.Id)
            {
                // The qualities are those of the new language now.
                ApplyAudioSelections(selections, keepRows: true);
                return;
            }

            // The response did not serve the language, or the request failed. Go back to the previous
            // language instead of leaving a choice that does not match what is playing.
            snapshot.PreferAudioLanguageId = previousId;
            if (selections.LanguageId != (previousSelectedId ?? AudioLanguageInformation.OriginalId))
            {
                ResetResolverAudioSelection(resolver, snapshot, reloadLanguage: true);

                // Replay once more even when nothing new was loaded: the first attempt already retired the
                // danmaku and skip-segment bindings of the file that is playing, and only a load brings them back.
                await ReplayWithSubtitleRestoreAsync(player, position);
                if (!IsCurrentPlayback(version) || !ReferenceEquals(resolver, _sourceResolver))
                {
                    return;
                }

                selections = GetResolverAudioSelections(resolver);
            }

            if (selections.Languages.Count > 0)
            {
                ApplyAudioSelections(selections, keepRows: true);
            }
            else
            {
                // Nothing is loaded. Keep the list, so the user can try again.
                MarkSelectedAudioLanguage(previousSelectedId);
            }

            Window?.ShowTip(ResourceToolkit.GetLocalizedString(StringNames.AudioTrackSwitchFailed), InfoType.Error);
        }, nativeReplay: true);
    }

    private Task SelectAudioQualityAsync(AudioQualityItemViewModel quality)
    {
        if (quality is not { IsEnabled: true } || IsPlaybackClosing || IsConnecting || Player is null
            || !AudioQualities.Any(item => ReferenceEquals(item, quality)) || ReferenceEquals(quality, SelectedAudioQuality)
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
            if (!AudioQualities.Any(item => ReferenceEquals(item, quality)) || ReferenceEquals(quality, SelectedAudioQuality))
            {
                return;
            }

            // Only the stable ID is remembered. The resolver looks the address up again on every load.
            var previousId = snapshot.PreferAudioQualityId;
            var previousSelectedId = SelectedAudioQuality?.Id;
            snapshot.PreferAudioQualityId = quality.Id;
            ResetResolverAudioSelection(resolver, snapshot, reloadLanguage: false);
            MarkSelectedAudioQuality(quality.Id);

            await ReplayWithSubtitleRestoreAsync(player, position);
            if (!IsCurrentPlayback(version) || !ReferenceEquals(resolver, _sourceResolver))
            {
                return;
            }

            var selections = GetResolverAudioSelections(resolver);
            if (selections.QualityId == quality.Id)
            {
                ApplyAudioSelections(selections, keepRows: true);
                return;
            }

            if (selections.Qualities.Count > 0)
            {
                // The loaded media has no such quality. Its default one is already playing,
                // which is what "no choice" means.
                snapshot.PreferAudioQualityId = default;
                ApplyAudioSelections(selections, keepRows: true);
                return;
            }

            // The request failed. Go back to the previous quality instead of leaving a choice
            // that does not match what is playing.
            snapshot.PreferAudioQualityId = previousId;
            ResetResolverAudioSelection(resolver, snapshot, reloadLanguage: false);

            // Replay once more even when nothing new was loaded: the first attempt already retired the
            // danmaku and skip-segment bindings of the file that is playing, and only a load brings them back.
            await ReplayWithSubtitleRestoreAsync(player, position);
            if (!IsCurrentPlayback(version) || !ReferenceEquals(resolver, _sourceResolver))
            {
                return;
            }

            selections = GetResolverAudioSelections(resolver);
            if (selections.Qualities.Count > 0)
            {
                ApplyAudioSelections(selections, keepRows: true);
            }
            else
            {
                // Nothing is loaded. Keep the list, so the user can try again.
                MarkSelectedAudioQuality(previousSelectedId);
            }

            Window?.ShowTip(ResourceToolkit.GetLocalizedString(StringNames.AudioTrackSwitchFailed), InfoType.Error);
        }, nativeReplay: true);
    }
}
