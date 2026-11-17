// Copyright (c) BiliFluent. All rights reserved.

namespace BiliCopilot.UI.Resolvers;

internal enum AdaptiveBandwidthSource
{
    None,
    Session,
    Previous,
}

internal readonly record struct AdaptiveBandwidthEstimate(double BitsPerSecond, TimeSpan ObservedAt, AdaptiveBandwidthSource Source)
{
    public bool IsFresh(TimeSpan now)
        => Source != AdaptiveBandwidthSource.None && now >= ObservedAt && now - ObservedAt < AdaptiveQualityThresholds.EvidenceLifetime;
}

/// <summary>Only stable media may leave evidence for the next one. Nothing is persisted.</summary>
internal sealed class AdaptiveBandwidthHistory
{
    private readonly object _sync = new();
    private AdaptiveBandwidthEstimate _previous;

    public static AdaptiveBandwidthHistory Session { get; } = new();

    public AdaptiveBandwidthEstimate Read(TimeSpan now)
    {
        lock (_sync)
        {
            if (!_previous.IsFresh(now))
            {
                _previous = default;
                return default;
            }

            return _previous with { Source = AdaptiveBandwidthSource.Previous };
        }
    }

    public void RecordStable(AdaptiveBandwidthEstimate estimate)
    {
        if (estimate.Source == AdaptiveBandwidthSource.None || !double.IsFinite(estimate.BitsPerSecond) || estimate.BitsPerSecond <= 0) return;
        lock (_sync)
        {
            if (_previous.Source == AdaptiveBandwidthSource.None || estimate.ObservedAt >= _previous.ObservedAt)
                _previous = estimate with { Source = AdaptiveBandwidthSource.Previous };
        }
    }
}
