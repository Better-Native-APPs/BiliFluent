// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Resolvers;

/// <summary>A half-open interval that natural playback will skip by seeking.</summary>
internal readonly record struct SponsorCacheInterval(double StartSeconds, double EndSeconds);

/// <summary>The three runtime options that bound forward demuxer reading.</summary>
internal readonly record struct SponsorCacheLimits(long MaxBytes, double ReadAheadSeconds, double CacheSeconds);

/// <summary>A bounded allowance for media that will not be watched.</summary>
internal readonly record struct SponsorCacheBudget(SponsorCacheLimits Limits, long ExtraBytes, double SkipSeconds, int Segments, int CappedSegments,
    double CappedSkipSeconds = 0);

internal enum SponsorCacheSwitchWaitDecision { Wait, Proceed, Cancelled }
internal readonly record struct SponsorCacheSwitchObservation(double Position, bool Settled, bool SkipExpectedOrPending);

/// <summary>Pure accounting shared by playback and the headless libmpv check.</summary>
internal static class SponsorCachePolicy
{
    public const long MiB = 1024 * 1024;
    public const double BitrateMargin = 1.25;
    public const long MaximumExtraBytes = 256 * MiB;
    public const int MaximumBaseMultiples = 2;
    public const long MinimumChangeBytes = MiB;
    public static readonly TimeSpan MinimumUpdateInterval = TimeSpan.FromSeconds(1);
    public const double SwitchLookAheadSeconds = 8;
    public static readonly TimeSpan MaximumSwitchWait = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan SwitchPollInterval = TimeSpan.FromMilliseconds(100);

    public static bool IsEnabled(bool onDemand, bool customConfig, bool assistantAvailable)
        => onDemand && !customConfig && assistantAvailable;

    public static SponsorCacheBudget Calculate(SponsorCacheLimits baseline, long bandwidthBitsPerSecond,
        double positionSeconds, IReadOnlyList<SponsorCacheInterval> intervals, bool enabled)
    {
        if (!enabled || baseline.MaxBytes <= 0 || bandwidthBitsPerSecond <= 0 || !double.IsFinite(positionSeconds))
            return new(baseline, 0, 0, 0, 0);

        var bytesPerSecond = bandwidthBitsPerSecond / 8d;
        // For a network cache, mpv uses the larger of cache-secs and readahead-secs.
        var secondsLimit = Math.Max(baseline.ReadAheadSeconds, baseline.CacheSeconds);
        var coverage = Math.Min(baseline.MaxBytes / bytesPerSecond, secondsLimit);
        var end = positionSeconds + coverage;
        var cap = (long)Math.Min(MaximumExtraBytes, (double)baseline.MaxBytes * MaximumBaseMultiples);
        long extra = 0;
        double skipped = 0, cappedSeconds = 0;
        int segments = 0, capped = 0;
        foreach (var interval in intervals)
        {
            if (interval.EndSeconds <= positionSeconds) continue;
            if (interval.StartSeconds > end) break;
            var seconds = interval.EndSeconds - Math.Max(positionSeconds, interval.StartSeconds);
            var estimate = seconds * bytesPerSecond * BitrateMargin;
            if (estimate > cap)
            {
                capped++;
                cappedSeconds += seconds;
                continue;
            }

            var accepted = Math.Min((long)Math.Ceiling(estimate), cap - extra);
            var acceptedSeconds = accepted < estimate ? accepted / (bytesPerSecond * BitrateMargin) : seconds;
            extra += accepted;
            skipped += acceptedSeconds;
            end += acceptedSeconds;
            segments++;
            if (accepted < estimate)
            {
                capped++;
                cappedSeconds += seconds - acceptedSeconds;
                break;
            }
        }

        return new(new(baseline.MaxBytes + extra, baseline.ReadAheadSeconds + skipped, baseline.CacheSeconds + skipped),
            extra, skipped, segments, capped, cappedSeconds);
    }

    public static IReadOnlyList<SponsorCacheInterval> Merge(IEnumerable<SponsorCacheInterval> intervals)
    {
        var merged = new List<SponsorCacheInterval>();
        foreach (var interval in intervals.Where(item => double.IsFinite(item.StartSeconds) && double.IsFinite(item.EndSeconds)
            && item.EndSeconds > Math.Max(0, item.StartSeconds)).OrderBy(item => item.StartSeconds).ThenBy(item => item.EndSeconds))
        {
            var next = interval with { StartSeconds = Math.Max(0, interval.StartSeconds) };
            if (merged.Count > 0 && next.StartSeconds <= merged[^1].EndSeconds)
                merged[^1] = merged[^1] with { EndSeconds = Math.Max(merged[^1].EndSeconds, next.EndSeconds) };
            else merged.Add(next);
        }

        return merged.AsReadOnly();
    }

    public static double SkippedAheadSeconds(double positionSeconds, double rawForwardSeconds, IReadOnlyList<SponsorCacheInterval> intervals)
    {
        if (!double.IsFinite(positionSeconds) || !double.IsFinite(rawForwardSeconds) || rawForwardSeconds <= 0) return 0;
        var end = positionSeconds + rawForwardSeconds;
        double skipped = 0;
        foreach (var interval in intervals)
        {
            if (interval.StartSeconds >= end) break;
            skipped += Math.Max(0, Math.Min(end, interval.EndSeconds) - Math.Max(positionSeconds, interval.StartSeconds));
        }

        return Math.Min(rawForwardSeconds, skipped);
    }

    public static double WatchableSeconds(double positionSeconds, double rawForwardSeconds, IReadOnlyList<SponsorCacheInterval> intervals)
        => Math.Max(0, rawForwardSeconds - SkippedAheadSeconds(positionSeconds, rawForwardSeconds, intervals));

    public static bool ProjectionChanged(IReadOnlyList<SponsorCacheInterval> current, IReadOnlyList<SponsorCacheInterval> next)
        => !current.SequenceEqual(next);

    public static double? SwitchSkipEnd(double positionSeconds, IReadOnlyList<SponsorCacheInterval> intervals, double playbackRate = 1)
    {
        if (!double.IsFinite(positionSeconds) || !double.IsFinite(playbackRate) || playbackRate <= 0) return null;
        foreach (var interval in intervals)
            if (interval.EndSeconds > positionSeconds && interval.StartSeconds <= positionSeconds + SwitchLookAheadSeconds * playbackRate)
                return interval.EndSeconds;
        return null;
    }

    public static bool ShouldDeferSwitch(double positionSeconds, IReadOnlyList<SponsorCacheInterval> intervals, double playbackRate = 1)
        => SwitchSkipEnd(positionSeconds, intervals, playbackRate).HasValue;

    public static SponsorCacheSwitchWaitDecision DecideSwitchWait(double skipEnd, SponsorCacheSwitchObservation observation,
        TimeSpan elapsed, bool cancelled)
        => cancelled ? SponsorCacheSwitchWaitDecision.Cancelled
            : elapsed >= MaximumSwitchWait || (observation.Settled && observation.Position >= skipEnd)
                || !observation.SkipExpectedOrPending ? SponsorCacheSwitchWaitDecision.Proceed : SponsorCacheSwitchWaitDecision.Wait;

    public static async Task WaitForSkipAsync(double skipEnd, Func<Task<SponsorCacheSwitchObservation>> read,
        Func<TimeSpan> elapsed, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (elapsed() >= MaximumSwitchWait) return;
            var observation = await read();
            var decision = DecideSwitchWait(skipEnd, observation, elapsed(), cancellationToken.IsCancellationRequested);
            if (decision == SponsorCacheSwitchWaitDecision.Cancelled) cancellationToken.ThrowIfCancellationRequested();
            if (decision == SponsorCacheSwitchWaitDecision.Proceed) return;
            await delay(SwitchPollInterval, cancellationToken);
        }
    }

    public static bool ShouldUpdate(SponsorCacheLimits applied, SponsorCacheLimits target, SponsorCacheLimits baseline, TimeSpan sinceLastUpdate)
        => applied != target && sinceLastUpdate >= MinimumUpdateInterval
            && (target == baseline || Math.Abs(target.MaxBytes - applied.MaxBytes) >= MinimumChangeBytes);
}
