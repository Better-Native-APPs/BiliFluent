// Copyright (c) BiliFluent. All rights reserved.

namespace BiliCopilot.UI.Resolvers;

internal sealed record AdaptiveQualityCandidate(int Quality, int VideoBandwidth, bool IsHdr = false, bool IsDolbyVision = false, bool IsEightK = false)
{
    public bool IsEligible => Quality > 0 && !IsHdr && !IsDolbyVision && !IsEightK
        && Quality != AdaptiveQualityThresholds.HdrQuality && Quality != AdaptiveQualityThresholds.DolbyVisionQuality
        && Quality < AdaptiveQualityThresholds.EightKQuality;

    public double RequiredBandwidth(int audioBandwidth, double speed, double ratio)
        => ((double)Math.Max(0, VideoBandwidth) + Math.Max(0, audioBandwidth)) * speed * ratio;
}

internal enum AdaptiveQualityAction
{
    Hold,
    Start,
    Up,
    Down,
}

internal enum AdaptiveQualityReason
{
    None,
    NoEvidence,
    Affordable,
    RebufferCount,
    RebufferDuration,
    BandwidthLimited,
    AudioBandwidthOnly,
    RecoveryCooldown,
    Settling,
    Manual,
    NoCandidates,
    Lowest,
    UpgradePending,
    SwitchLimit,
}

internal readonly record struct AdaptiveQualityDecision(
    AdaptiveQualityAction Action,
    int FromQuality,
    int ToQuality,
    AdaptiveQualityReason Reason,
    AdaptiveBandwidthEstimate Estimate,
    double RequiredBitsPerSecond,
    double CacheSeconds,
    bool ReportCap = false);

internal readonly record struct AdaptiveQualitySample(
    int CurrentQuality,
    int SelectedAudioBandwidth,
    int StandardAudioBandwidth,
    double PlaybackSpeed,
    double ForwardCacheSeconds,
    double DownloadBitsPerSecond,
    bool NetworkLimited,
    bool PausedForCache = false,
    bool UserPaused = false,
    bool Seeking = false,
    bool Switching = false,
    bool Reloading = false,
    bool NodeSettling = false,
    bool BandwidthLimited = false,
    double? LimitedBitsPerSecond = null);

/// <summary>Timestamped evidence and playback observations in; tier decisions out. No clock or player access.</summary>
internal sealed class AdaptiveQualityPolicy(AdaptiveBandwidthHistory history)
{
    private readonly List<(TimeSpan Time, double Rate)> _rates = [];
    private readonly List<TimeSpan> _cachePauses = [];
    private readonly List<(TimeSpan Start, TimeSpan End)> _bufferIntervals = [];
    private AdaptiveBandwidthEstimate _currentEvidence;
    private TimeSpan? _lastSample;
    private TimeSpan? _stableSince;
    private TimeSpan? _bufferStarted;
    private TimeSpan? _upgradeSince;
    private int _upgradeTarget;
    private TimeSpan _noDecisionBefore;
    private TimeSpan? _lastDowngrade;
    private double? _lastSpeed;
    private bool _automatic = true;
    private bool _bandwidthLimited;
    private double? _limitedRate;
    private int _switches;
    private bool _capReported;

    public int Switches => _switches;

    public bool IsAutomatic => _automatic;

    public AdaptiveBandwidthEstimate GetEstimate(TimeSpan now)
        => _currentEvidence.IsFresh(now) ? _currentEvidence : history.Read(now);

    public AdaptiveQualityDecision SelectStart(TimeSpan now, IEnumerable<AdaptiveQualityCandidate> candidates, int audioBandwidth, double speed)
    {
        var levels = Eligible(candidates);
        var estimate = GetEstimate(now);
        if (levels.Count == 0) return new(AdaptiveQualityAction.Hold, 0, 0, AdaptiveQualityReason.NoCandidates, estimate, 0, 0);
        var selected = estimate.Source == AdaptiveBandwidthSource.None
            ? levels.Find(level => level.Quality <= AdaptiveQualityThresholds.InitialQuality) ?? levels[^1]
            : Affordable(levels, estimate.BitsPerSecond, audioBandwidth, NormalizeSpeed(speed), AdaptiveQualityThresholds.InitialBandwidthRatio) ?? levels[^1];
        return new(AdaptiveQualityAction.Start, 0, selected.Quality,
            estimate.Source == AdaptiveBandwidthSource.None ? AdaptiveQualityReason.NoEvidence : AdaptiveQualityReason.Affordable,
            estimate, selected.RequiredBandwidth(audioBandwidth, NormalizeSpeed(speed), AdaptiveQualityThresholds.InitialBandwidthRatio), 0);
    }

    public void SetAutomatic(bool automatic)
    {
        if (_automatic == automatic) return;
        _automatic = automatic;
        ResetContinuity();
        _bandwidthLimited = false;
        _limitedRate = null;
    }

    public void OnDiscontinuity(TimeSpan now)
    {
        _noDecisionBefore = now + AdaptiveQualityThresholds.DiscontinuitySettle;
        _rates.Clear();
        ResetContinuity();
    }

    /// <summary>Count actual automatic changes, not preparations, cancellations or failed reloads.</summary>
    public void OnSelectionCompleted(TimeSpan now, int fromQuality, int toQuality)
    {
        if (fromQuality != toQuality)
        {
            _switches++;
            if (toQuality < fromQuality) _lastDowngrade = now;
        }

        ResetContinuity();
        _bandwidthLimited = false;
        _limitedRate = null;
    }

    public AdaptiveQualityDecision OnSample(TimeSpan now, AdaptiveQualitySample sample, IEnumerable<AdaptiveQualityCandidate> candidates)
    {
        var estimate = GetEstimate(now);
        AdaptiveQualityDecision Hold(AdaptiveQualityReason reason, bool reportCap = false)
            => new(AdaptiveQualityAction.Hold, sample.CurrentQuality, sample.CurrentQuality, reason, estimate, 0, sample.ForwardCacheSeconds, reportCap);

        if (_lastSample is { } last && now <= last) return Hold(AdaptiveQualityReason.Settling);
        if (_lastSample is { } previous && now - previous > AdaptiveQualityThresholds.SampleGapReset)
        {
            _rates.Clear();
            ResetContinuity();
        }

        _lastSample = now;
        _rates.RemoveAll(rate => now - rate.Time > AdaptiveQualityThresholds.BandwidthWindow);
        var speed = NormalizeSpeed(sample.PlaybackSpeed);
        if (_lastSpeed is { } before && Math.Abs(speed - before) > AdaptiveQualityThresholds.SpeedChangeTolerance) OnDiscontinuity(now);
        _lastSpeed = speed;
        if (_automatic && sample.BandwidthLimited)
        {
            _bandwidthLimited = true;
            _limitedRate = sample.LimitedBitsPerSecond;
        }

        if (sample.UserPaused || sample.Seeking || sample.Switching || sample.Reloading || sample.NodeSettling || now < _noDecisionBefore)
        {
            ResetContinuity();
            return Hold(AdaptiveQualityReason.Settling);
        }

        ObserveBandwidth(now, sample);
        estimate = GetEstimate(now);
        ObserveStablePlayback(now, sample);
        if (!_automatic) return Hold(AdaptiveQualityReason.Manual);
        if (_switches >= AdaptiveQualityThresholds.MaxSwitchesPerMedia)
        {
            var report = !_capReported;
            _capReported = true;
            return Hold(AdaptiveQualityReason.SwitchLimit, report);
        }

        var levels = Eligible(candidates);
        var current = levels.Find(level => level.Quality == sample.CurrentQuality);
        if (current is null) return Hold(AdaptiveQualityReason.NoCandidates);
        ObserveBuffering(now, sample.PausedForCache);
        var downReason = _bandwidthLimited ? AdaptiveQualityReason.BandwidthLimited
            : _cachePauses.Count >= AdaptiveQualityThresholds.RebufferCount ? AdaptiveQualityReason.RebufferCount
            : BufferingSeconds(now) >= AdaptiveQualityThresholds.RebufferDurationSeconds ? AdaptiveQualityReason.RebufferDuration
            : AdaptiveQualityReason.None;
        if (downReason != AdaptiveQualityReason.None)
        {
            ClearUpgrade();
            var lower = levels.Find(level => level.Quality < sample.CurrentQuality);
            if (lower is null) return Hold(AdaptiveQualityReason.Lowest);
            // A special audio stream alone exceeding the connection is not grounds to lower video.
            var measuredRate = _limitedRate ?? (sample.PausedForCache ? sample.DownloadBitsPerSecond : (double?)null);
            var standardNeed = current.RequiredBandwidth(sample.StandardAudioBandwidth, speed, 1);
            if (sample.SelectedAudioBandwidth > sample.StandardAudioBandwidth && measuredRate is { } measured
                && double.IsFinite(measured) && measured >= standardNeed)
                return Hold(AdaptiveQualityReason.AudioBandwidthOnly);
            return new(AdaptiveQualityAction.Down, sample.CurrentQuality, lower.Quality, downReason, estimate,
                lower.RequiredBandwidth(sample.SelectedAudioBandwidth, speed, 1), sample.ForwardCacheSeconds);
        }

        var recovering = _lastDowngrade is not null;
        if (_lastDowngrade is { } downgraded && now - downgraded < AdaptiveQualityThresholds.RecoveryCooldown)
        {
            ClearUpgrade();
            return Hold(AdaptiveQualityReason.RecoveryCooldown);
        }

        var ratio = recovering ? AdaptiveQualityThresholds.RecoveryBandwidthRatio : AdaptiveQualityThresholds.InitialBandwidthRatio;
        var cacheSeconds = recovering ? AdaptiveQualityThresholds.RecoveryUpgradeCacheSeconds : AdaptiveQualityThresholds.InitialUpgradeCacheSeconds;
        var duration = recovering ? AdaptiveQualityThresholds.RecoveryUpgradeDuration : AdaptiveQualityThresholds.InitialUpgradeDuration;
        var target = estimate.Source == AdaptiveBandwidthSource.None ? null : Affordable(levels, estimate.BitsPerSecond, sample.SelectedAudioBandwidth, speed, ratio);
        if (sample.PausedForCache || sample.ForwardCacheSeconds < cacheSeconds || target is not { } upgrade || upgrade.Quality <= sample.CurrentQuality)
        {
            ClearUpgrade();
            return Hold(AdaptiveQualityReason.None);
        }

        if (_upgradeTarget != upgrade.Quality)
        {
            _upgradeTarget = upgrade.Quality;
            _upgradeSince = now;
        }

        _upgradeSince ??= now;
        if (now - _upgradeSince.Value < duration) return Hold(AdaptiveQualityReason.UpgradePending);
        return new(AdaptiveQualityAction.Up, sample.CurrentQuality, upgrade.Quality, AdaptiveQualityReason.Affordable, estimate,
            upgrade.RequiredBandwidth(sample.SelectedAudioBandwidth, speed, ratio), sample.ForwardCacheSeconds);
    }

    private void ObserveBandwidth(TimeSpan now, AdaptiveQualitySample sample)
    {
        if (!sample.NetworkLimited || !double.IsFinite(sample.DownloadBitsPerSecond) || sample.DownloadBitsPerSecond <= 0) return;
        _rates.Add((now, sample.DownloadBitsPerSecond));
        var sorted = _rates.Select(rate => rate.Rate).Order().ToArray();
        // Nearest-rank p25 keeps one burst from becoming evidence of sustained capacity.
        var index = Math.Max(0, (int)Math.Ceiling(sorted.Length * AdaptiveQualityThresholds.BandwidthPercentile) - 1);
        _currentEvidence = new(sorted[index], now, AdaptiveBandwidthSource.Session);
    }

    private void ObserveStablePlayback(TimeSpan now, AdaptiveQualitySample sample)
    {
        if (sample.PausedForCache)
        {
            _stableSince = null;
            return;
        }

        _stableSince ??= now;
        if (now - _stableSince.Value >= AdaptiveQualityThresholds.PreviousMediaStableTime
            && _currentEvidence.IsFresh(now) && _currentEvidence.ObservedAt >= _stableSince.Value)
            history.RecordStable(_currentEvidence);
    }

    private void ObserveBuffering(TimeSpan now, bool buffering)
    {
        _cachePauses.RemoveAll(started => now - started > AdaptiveQualityThresholds.RebufferCountWindow);
        _bufferIntervals.RemoveAll(interval => now - interval.End > AdaptiveQualityThresholds.RebufferDurationWindow);
        if (buffering && _bufferStarted is null)
        {
            _bufferStarted = now;
            _cachePauses.Add(now);
        }
        else if (!buffering && _bufferStarted is { } started)
        {
            _bufferIntervals.Add((started, now));
            _bufferStarted = null;
        }
    }

    private double BufferingSeconds(TimeSpan now)
    {
        var cutoff = now - AdaptiveQualityThresholds.RebufferDurationWindow;
        var seconds = _bufferIntervals.Sum(interval => (interval.End - (interval.Start > cutoff ? interval.Start : cutoff)).TotalSeconds);
        return seconds + (_bufferStarted is { } started ? (now - (started > cutoff ? started : cutoff)).TotalSeconds : 0);
    }

    private void ResetContinuity()
    {
        ClearUpgrade();
        _stableSince = null;
        _bufferStarted = null;
        _cachePauses.Clear();
        _bufferIntervals.Clear();
    }

    private void ClearUpgrade()
    {
        _upgradeSince = null;
        _upgradeTarget = 0;
    }

    private static double NormalizeSpeed(double speed) => double.IsFinite(speed) && speed > 0 ? speed : 1;

    private static List<AdaptiveQualityCandidate> Eligible(IEnumerable<AdaptiveQualityCandidate> candidates)
        => candidates.Where(candidate => candidate.IsEligible).DistinctBy(candidate => candidate.Quality)
            .OrderByDescending(candidate => candidate.Quality).ToList();

    private static AdaptiveQualityCandidate? Affordable(IEnumerable<AdaptiveQualityCandidate> levels, double bandwidth, int audioBandwidth, double speed, double ratio)
        => levels.FirstOrDefault(level => level is { VideoBandwidth: > 0 } candidate && bandwidth >= candidate.RequiredBandwidth(audioBandwidth, speed, ratio));
}
