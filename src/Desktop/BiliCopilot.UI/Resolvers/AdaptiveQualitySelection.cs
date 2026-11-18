// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.Resolvers;

internal interface IAdaptiveQualityResolver
{
    IReadOnlyList<AdaptiveQualityCandidate> GetAdaptiveCandidates();

    AdaptiveQualityDecision? AdaptiveStartDecision { get; }
}

/// <summary>Reuses account/stream filtering and the existing automatic ranking, with actual codec bitrates.</summary>
internal static class AdaptiveQualitySelection
{
    public static bool IsEnabled(OnDemandSelectionRequest request, PreferQualityType preference)
        => preference == PreferQualityType.Auto && request.Preferences.VideoQuality is not > 0 && request.ResolvedVideoQuality is null;

    public static IReadOnlyList<AdaptiveQualityCandidate> BuildCandidates(IEnumerable<PlayerFormatInformation> playable, Func<int, DashSegmentInformation?> selectVideo)
    {
        var remaining = playable.ToList();
        var candidates = new List<AdaptiveQualityCandidate>();
        while (VideoQualityPreference.PickAutomatic(remaining, PreferQualityType.Auto) is { } format)
        {
            remaining.RemoveAll(item => item.Quality == format.Quality);
            if (selectVideo(format.Quality) is not { } stream) continue;
            var candidate = new AdaptiveQualityCandidate(format.Quality, stream.Bandwidth, format.IsHdr,
                stream.Codecs.Contains("dvh1", StringComparison.OrdinalIgnoreCase) || stream.Codecs.Contains("dvhe", StringComparison.OrdinalIgnoreCase),
                stream.Width >= AdaptiveQualityThresholds.EightKWidth || stream.Height >= AdaptiveQualityThresholds.EightKHeight);
            if (candidate.IsEligible) candidates.Add(candidate);
        }

        return candidates;
    }

    public static PlayerFormatInformation? PickStartFormat(IEnumerable<PlayerFormatInformation> playable, AdaptiveQualityDecision decision)
        => decision.Action == AdaptiveQualityAction.Start
            ? VideoQualityPreference.PickAutomatic(playable.Where(format => format.Quality == decision.ToQuality), PreferQualityType.Auto)
            : null;
}
