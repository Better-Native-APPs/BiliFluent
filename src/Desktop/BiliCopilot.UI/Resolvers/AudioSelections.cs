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
    // The API's Dolby Atmos stream. Any other Dolby stream is only called Dolby.
    private const string DolbyAtmosId = "dolby:30250";

    public static AudioSelections Empty => new([], default, [], default);

    /// <summary>
    /// The quality to play: the one chosen by hand when the media has it with an address, otherwise the automatic choice.
    /// </summary>
    /// <param name="qualities">The audio qualities of the media.</param>
    /// <param name="preferredId">The quality chosen by hand. Null or empty means automatic.</param>
    /// <param name="standardOnly">Whether the automatic choice leaves out Dolby and Hi-Res, because they did not load.</param>
    public static AudioQualityInformation? PickQuality(IList<AudioQualityInformation>? qualities, string? preferredId, bool standardOnly = false)
    {
        if (!string.IsNullOrEmpty(preferredId))
        {
            // The standard quality is one choice: whichever standard bitrate was named, the highest one plays.
            var preferred = preferredId.StartsWith("std:", StringComparison.Ordinal)
                ? PickStandard(qualities)
                : qualities?.FirstOrDefault(quality => quality.Id == preferredId && quality.IsEnabled);
            if (preferred is not null)
            {
                return preferred;
            }
        }

        return PickAutomatic(qualities, standardOnly);
    }

    /// <summary>
    /// The automatic choice: the best quality that comes with an address. This is the only place that ranks
    /// the audio qualities: Hi-Res lossless, then Dolby Atmos, then Dolby, then the highest standard bitrate.
    /// </summary>
    /// <param name="qualities">The audio qualities of the media.</param>
    /// <param name="standardOnly">Whether Dolby and Hi-Res are left out, because they did not load.</param>
    public static AudioQualityInformation? PickAutomatic(IList<AudioQualityInformation>? qualities, bool standardOnly = false)
        => standardOnly && PickStandard(qualities) is { } standard
            ? standard
            : qualities?.Where(quality => quality.IsEnabled)
                .OrderByDescending(quality => quality.Kind switch
                {
                    AudioQualityKind.HiRes => 3,
                    AudioQualityKind.Dolby => quality.Id == DolbyAtmosId ? 2 : 1,
                    _ => 0,
                })
                .ThenByDescending(GetStreamId)
                .FirstOrDefault();

    /// <summary>
    /// The highest standard bitrate that comes with an address.
    /// </summary>
    public static AudioQualityInformation? PickStandard(IList<AudioQualityInformation>? qualities)
        => qualities?.Where(quality => quality.Kind == AudioQualityKind.Standard && quality.IsEnabled).MaxBy(GetStreamId);

    private static int GetStreamId(AudioQualityInformation quality)
        => int.TryParse(quality.Segment?.Id, out var streamId) ? streamId : 0;
}
