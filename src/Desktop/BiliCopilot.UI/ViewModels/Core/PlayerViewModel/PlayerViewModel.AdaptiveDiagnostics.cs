// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Resolvers;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    partial void LogAdaptiveDecision(AdaptiveQualityDecision decision)
        => AdaptiveQualityDiagnostics.LogDecision(logger, decision);

    partial void DescribeAdaptiveSample(TimeSpan now, bool automatic)
    {
        var estimate = _adaptiveQuality.GetEstimate(now);
        var observation = _adaptiveQuality.LatestObservation;
        _networkDiagnostics = _networkDiagnostics with
        {
            AdaptiveEstimate = estimate.BitsPerSecond,
            AdaptiveMode = automatic ? "auto" : "manual",
            AdaptiveObservation = observation?.BytesPerSecond * AdaptiveQualityThresholds.BitsPerByte,
            AdaptiveObservationKind = observation is null ? "n/a" : observation.IsNetworkLimited ? "network" : "floor",
        };
        if (estimate.BitsPerSecond == _adaptiveLoggedEvidence
            || (_adaptiveEvidenceLogAt is { } logged && now - logged < AdaptiveQualityThresholds.EvidenceLogInterval)) return;
        AdaptiveQualityDiagnostics.LogEvidence(logger, estimate, observation, _adaptiveEvidenceNodeChanged);
        _adaptiveLoggedEvidence = estimate.BitsPerSecond;
        _adaptiveEvidenceLogAt = now;
        _adaptiveEvidenceNodeChanged = false;
    }
}
