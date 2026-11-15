// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.Models;

/// <summary>
/// Decides which video quality plays. A quality chosen by hand wins; without one the quality is automatic.
/// </summary>
public static class VideoQualityPreference
{
    /// <summary>
    /// Picks the quality to play among <paramref name="playable"/>.
    /// </summary>
    /// <param name="playable">The qualities of the media that this account can play.</param>
    /// <param name="manualQuality">The quality chosen by hand. Zero or less means automatic.</param>
    /// <param name="preference">The quality preference of the settings, which the automatic choice follows.</param>
    /// <returns>The quality to play, or null when there is none.</returns>
    public static PlayerFormatInformation? Pick(IEnumerable<PlayerFormatInformation> playable, int manualQuality, PreferQualityType preference)
    {
        var formats = playable.OrderByDescending(format => format.Quality).ToList();
        if (manualQuality <= 0)
        {
            return PickAutomatic(formats, preference);
        }

        // A media without the chosen quality plays the nearest one below it.
        return formats.Find(format => format.Quality <= manualQuality) ?? formats.LastOrDefault();
    }

    /// <summary>
    /// The automatic choice. This is the only place that decides what "automatic" plays.
    /// For now it is the highest quality, or the highest one up to the tier the settings prefer.
    /// </summary>
    /// <param name="playable">The qualities of the media that this account can play.</param>
    /// <param name="preference">The quality preference of the settings.</param>
    /// <returns>The quality to play, or null when there is none.</returns>
    public static PlayerFormatInformation? PickAutomatic(IEnumerable<PlayerFormatInformation> playable, PreferQualityType preference)
    {
        var formats = playable.OrderByDescending(format => format.Quality).ToList();
        var limit = preference switch
        {
            PreferQualityType.UHD => 120,
            PreferQualityType.HD => 116,
            _ => int.MaxValue,
        };

        return formats.Find(format => format.Quality <= limit) ?? formats.LastOrDefault();
    }
}
