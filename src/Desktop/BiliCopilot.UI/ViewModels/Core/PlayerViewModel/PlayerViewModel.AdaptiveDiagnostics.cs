// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Resolvers;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    partial void LogAdaptiveDecision(AdaptiveQualityDecision decision)
        => AdaptiveQualityDiagnostics.LogDecision(logger, decision);

    partial void DescribeAdaptiveSample(TimeSpan now, bool automatic)
        => _networkDiagnostics = _networkDiagnostics with
        {
            AdaptiveEstimate = _adaptiveQuality.GetEstimate(now).BitsPerSecond,
            AdaptiveMode = automatic ? "auto" : "manual",
        };
}
