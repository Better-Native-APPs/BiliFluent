// Copyright (c) BiliFluent. All rights reserved.

using Microsoft.Extensions.Logging;
using Richasy.MpvKernel.Relay;
using System.Globalization;

namespace BiliCopilot.UI.Resolvers;

internal static class AdaptiveQualityDiagnostics
{
    public static void LogDecision(ILogger logger, AdaptiveQualityDecision decision)
    {
        if (!logger.IsEnabled(LogLevel.Information)) return;
        if (decision.ReportCap)
        {
            logger.LogInformation("Playback | abr capped qn={Quality} switches={Switches}",
                decision.FromQuality, AdaptiveQualityThresholds.MaxSwitchesPerMedia);
            return;
        }

        if (decision.Action is not (AdaptiveQualityAction.Start or AdaptiveQualityAction.Up or AdaptiveQualityAction.Down)) return;
        var action = decision.Action switch
        {
            AdaptiveQualityAction.Up => "up",
            AdaptiveQualityAction.Down => "down",
            _ => "start",
        };
        var reason = decision.Reason switch
        {
            AdaptiveQualityReason.NoEvidence => "no-evidence",
            AdaptiveQualityReason.RebufferCount => "rebuffer-count",
            AdaptiveQualityReason.RebufferDuration => "rebuffer-duration",
            AdaptiveQualityReason.BandwidthLimited => "bandwidth-limited",
            AdaptiveQualityReason.Initial => "initial",
            AdaptiveQualityReason.Direct => "direct",
            _ => "affordable",
        };
        var evidence = decision.Estimate.Source switch
        {
            AdaptiveBandwidthSource.Session => "session",
            AdaptiveBandwidthSource.Previous => "previous",
            _ => "none",
        };
        logger.LogInformation("Playback | abr decision={Decision} from={FromQuality} to={ToQuality} reason={Reason} est-kbps={EstimateKbps} need-kbps={NeedKbps} cache-secs={CacheSeconds} evidence={Evidence}",
            action, decision.FromQuality, decision.ToQuality, reason,
            (decision.Estimate.BitsPerSecond / AdaptiveQualityThresholds.BitsPerKilobit).ToString("0", CultureInfo.InvariantCulture),
            (decision.RequiredBitsPerSecond / AdaptiveQualityThresholds.BitsPerKilobit).ToString("0", CultureInfo.InvariantCulture),
            decision.CacheSeconds.ToString("0.###", CultureInfo.InvariantCulture), evidence);
    }

    public static void LogEvidence(ILogger logger, AdaptiveBandwidthEstimate estimate, RelayThroughputObservation? observation, bool nodeChanged)
    {
        if (!logger.IsEnabled(LogLevel.Information)) return;
        logger.LogInformation("Playback | abr evidence kbps={EvidenceKbps} bytes={Bytes} ms={Milliseconds} kind={Kind} stream=video node-changed={NodeChanged}",
            (estimate.BitsPerSecond / AdaptiveQualityThresholds.BitsPerKilobit).ToString("0", CultureInfo.InvariantCulture),
            observation?.Bytes ?? 0, (observation?.Duration.TotalMilliseconds ?? 0).ToString("0", CultureInfo.InvariantCulture),
            observation is { IsNetworkLimited: true } ? "network" : "floor", nodeChanged);
    }
}
