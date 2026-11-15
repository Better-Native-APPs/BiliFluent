// Copyright (c) Bili Copilot. All rights reserved.

using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.Resolvers;

/// <summary>
/// The audio choices of the loaded media: the languages (original audio first) with the one that plays,
/// and the qualities of that language with the one that plays. Empty until the media is loaded.
/// </summary>
internal readonly record struct AudioSelections(
    List<AudioLanguageInformation> Languages,
    string? LanguageId,
    List<AudioQualityInformation> Qualities,
    string? QualityId,
    string? LanguageGroupTitle = default)
{
    public static AudioSelections Empty => new([], default, [], default);

    /// <summary>
    /// The quality to play: the preferred one when the media has it with an address,
    /// otherwise the highest standard quality.
    /// </summary>
    public static AudioQualityInformation? PickQuality(IList<AudioQualityInformation>? qualities, string? preferredId)
        => qualities?.FirstOrDefault(quality => quality.Id == preferredId && quality.IsEnabled)
            ?? qualities?.Where(quality => quality.Kind == AudioQualityKind.Standard && quality.IsEnabled)
                .MaxBy(quality => int.TryParse(quality.Segment?.Id, out var streamId) ? streamId : 0);
}
