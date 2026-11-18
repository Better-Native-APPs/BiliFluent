// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Resolvers;
using BiliCopilot.UI.SponsorBlock.Playback;
using Microsoft.Extensions.Logging;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Player;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private IReadOnlyList<SponsorCacheInterval> _sponsorCacheIntervals = [];
    private ImmutableArray<SponsorSeekStatus> _sponsorCacheSeekClaims = [];
    private long _sponsorCacheProjectionVersion = -1;
    private MpvClient? _sponsorCacheClient;
    private SponsorCacheLimits? _sponsorCacheBaseline;
    private SponsorCacheLimits? _sponsorCacheApplied;
    private SponsorCacheBudget? _sponsorCacheDesired;
    private NetworkSample? _sponsorCacheLastSample;
    private TimeSpan _sponsorCacheLastUpdate;
    private bool _sponsorCacheBoundary = true;
    private bool _sponsorCacheFailed;
    private (int Segments, int Capped, long Extra)? _sponsorCacheLastCap;
    private Task _sponsorCacheRuntimeWork = Task.CompletedTask;
    // Managed serialization only, like the VM's media gate; no wait handle is allocated.
#pragma warning disable CA2213
    private readonly SemaphoreSlim _sponsorCacheGate = new(1, 1);
#pragma warning restore CA2213

    private bool CanAccountSponsorCache => !_sponsorCacheBoundary && !_sponsorCacheFailed
        && CanUseOnDemandNetwork && CanUseSponsorContent && !IsLiveMedia && !IsPlaybackClosing
        && _sponsorCacheProjectionVersion == _mediaVersion && _sponsorCacheBaseline.HasValue
        && ReferenceEquals(_sponsorCacheClient, Client);

    partial void ConfigureSponsorCacheClient(MpvClient client, bool managed)
    {
        _sponsorCacheClient = managed ? client : null;
        _sponsorCacheBaseline = _sponsorCacheApplied = null;
        _sponsorCacheDesired = null;
        _sponsorCacheLastSample = null;
        _sponsorCacheFailed = false;
        _sponsorCacheBoundary = true;
        SetSponsorCacheIntervals([]);
        if (!managed) return;
        if (client.GetProperty("options/demuxer-max-bytes", out long bytes) != MpvError.Success
            || client.GetProperty("options/demuxer-readahead-secs", out double readahead) != MpvError.Success
            || client.GetProperty("options/cache-secs", out double seconds) != MpvError.Success
            || bytes < 0 || !double.IsFinite(readahead) || readahead < 0 || !double.IsFinite(seconds) || seconds < 0)
        {
            _sponsorCacheFailed = true;
            logger.LogWarning("Playback | sponsor-cache unavailable error=limits-unreadable");
            return;
        }

        _sponsorCacheBaseline = _sponsorCacheApplied = new(bytes, readahead, seconds);
        _sponsorCacheLastUpdate = NetworkNow - SponsorCachePolicy.MinimumUpdateInterval;
    }

    partial void UpdateSponsorCacheProjection(SponsorControllerResult result)
    {
        _sponsorCacheProjectionVersion = _mediaVersion;
        _sponsorCacheSeekClaims = result.SeekClaims;
        var intervals = SponsorCachePolicy.Merge(result.AutomaticSeekIntervals.Select(interval => new SponsorCacheInterval(interval.Start, interval.End)));
        SetSponsorCacheIntervals(intervals);
        if (_sponsorCacheLastSample is { } sample) OfferSponsorCacheBudget(sample);
    }

    private void SetSponsorCacheIntervals(IReadOnlyList<SponsorCacheInterval> intervals)
    {
        if (!SponsorCachePolicy.ProjectionChanged(_sponsorCacheIntervals, intervals)) return;
        _sponsorCacheIntervals = intervals;
        _sponsorCacheLastCap = null;
        _failover.OnCacheAccountingChanged();
        OnAdaptiveSeek(NetworkNow);
    }

    partial void ResumeSponsorCache()
    {
        _sponsorCacheBoundary = false;
        _sponsorCacheLastSample = null;
    }

    partial void RetireSponsorCache()
    {
        _sponsorCacheBoundary = true;
        _sponsorCacheLastSample = null;
        _sponsorCacheSeekClaims = [];
        SetSponsorCacheIntervals([]);
        if (_sponsorCacheBaseline is { } baseline) _sponsorCacheDesired = new(baseline, 0, 0, 0, 0);
    }

    partial void DrainSponsorCache(ref Task cleanup)
    {
        if (_sponsorCacheBoundary && _sponsorCacheClient is { IsDisposed: false } client && _sponsorCacheBaseline is { } baseline)
            cleanup = OwnWork(RestoreSponsorCacheAsync(client, baseline));
    }

    partial void TransformSponsorCacheSeconds(double position, ref double seconds)
    {
        if (CanAccountSponsorCache) seconds = SponsorCachePolicy.WatchableSeconds(position, seconds, _sponsorCacheIntervals);
    }

    partial void AccountSponsorCache(NetworkSample raw, ref PlaybackCacheSample sample, ref double skippedSeconds, ref double extraMegabytes)
    {
        _sponsorCacheLastSample = raw;
        var intervals = CanAccountSponsorCache ? _sponsorCacheIntervals : [];
        var position = raw.PositionKnown ? raw.Position : double.NaN;
        skippedSeconds = SponsorCachePolicy.SkippedAheadSeconds(position, sample.CacheSeconds, intervals);
        sample = sample with
        {
            CacheSeconds = SponsorCachePolicy.WatchableSeconds(position, sample.CacheSeconds, intervals),
            AudioCacheSeconds = sample.AudioCacheSeconds is { } audio
                ? SponsorCachePolicy.WatchableSeconds(raw.AudioPosition ?? position, audio, intervals) : null,
        };
        OfferSponsorCacheBudget(raw);
        extraMegabytes = CurrentSponsorCacheExtraBytes / (double)SponsorCachePolicy.MiB;
    }

    private long CurrentSponsorCacheExtraBytes => _sponsorCacheApplied is { } applied && _sponsorCacheBaseline is { } baseline
        ? Math.Max(0, applied.MaxBytes - baseline.MaxBytes) : 0;

    private void OfferSponsorCacheBudget(NetworkSample raw)
    {
        if (_sponsorCacheBaseline is not { } baseline || _sponsorCacheClient is not { IsDisposed: false }) return;
        var streams = OnDemandResolver?.CurrentStreams;
        var bandwidth = (long)(streams?.VideoBandwidth ?? 0) + (streams?.AudioBandwidth ?? 0);
        var enabled = SponsorCachePolicy.IsEnabled(CanAccountSponsorCache && _hasLoadedFile && !_onDemandReplayNeedsLoad,
            !ReferenceEquals(_sponsorCacheClient, Client), _sponsorCacheIntervals.Count > 0);
        var budget = SponsorCachePolicy.Calculate(baseline, bandwidth, raw.PositionKnown ? raw.Position : double.NaN,
            _sponsorCacheIntervals, enabled);
        _sponsorCacheDesired = budget;
        if (budget.CappedSegments > 0)
        {
            var cap = (budget.Segments, budget.CappedSegments, budget.ExtraBytes);
            if (_sponsorCacheLastCap != cap)
            {
                _sponsorCacheLastCap = cap;
                LogSponsorCacheBudget("capped", budget);
            }
        }

        if (_sponsorCacheRuntimeWork.IsCompleted && _sponsorCacheApplied is { } applied
            && SponsorCachePolicy.ShouldUpdate(applied, budget.Limits, baseline, NetworkNow - _sponsorCacheLastUpdate))
            _sponsorCacheRuntimeWork = OwnWork(ApplySponsorCacheBudgetAsync());
    }

    private async Task ApplySponsorCacheBudgetAsync()
    {
        await _sponsorCacheGate.WaitAsync();
        try
        {
            if (_sponsorCacheClient is not { IsDisposed: false } client || _sponsorCacheBaseline is not { } baseline
                || _sponsorCacheApplied is not { } applied || _sponsorCacheDesired is not { } desired) return;
            var budget = _sponsorCacheBoundary || !CanAccountSponsorCache ? new(baseline, 0, 0, 0, 0) : desired;
            if (!SponsorCachePolicy.ShouldUpdate(applied, budget.Limits, baseline, NetworkNow - _sponsorCacheLastUpdate)) return;
            try
            {
                await client.UpdateCacheLimitsAsync(budget.Limits.MaxBytes, budget.Limits.ReadAheadSeconds, budget.Limits.CacheSeconds);
                _sponsorCacheApplied = budget.Limits;
                _sponsorCacheLastUpdate = NetworkNow;
                LogSponsorCacheBudget(budget.ExtraBytes == 0 ? "restore" : "apply", budget);
            }
            catch (Exception ex)
            {
                _sponsorCacheFailed = true;
                logger.LogWarning("Playback | sponsor-cache update failed error={Error}", ex.GetType().Name);
                if (!client.IsDisposed) await RestoreSponsorCacheLimitsAsync(client, baseline);
            }
        }
        finally { _sponsorCacheGate.Release(); }
    }

    private async Task RestoreSponsorCacheAsync(MpvClient client, SponsorCacheLimits baseline)
    {
        await _sponsorCacheGate.WaitAsync();
        try
        {
            if (ReferenceEquals(_sponsorCacheClient, client) && _sponsorCacheBaseline == baseline && !client.IsDisposed
                && _sponsorCacheApplied != baseline) await RestoreSponsorCacheLimitsAsync(client, baseline);
        }
        finally { _sponsorCacheGate.Release(); }
    }

    private async Task RestoreSponsorCacheLimitsAsync(MpvClient client, SponsorCacheLimits baseline)
    {
        try
        {
            // Boundary and failure recovery bypass playback debounce. The native boundary
            // still joins this restoration before the next load or core disposal.
            if (client.IsDisposed) return;
            await client.UpdateCacheLimitsAsync(baseline.MaxBytes, baseline.ReadAheadSeconds, baseline.CacheSeconds);
            _sponsorCacheApplied = baseline;
            _sponsorCacheLastUpdate = NetworkNow;
            LogSponsorCacheBudget("restore", new(baseline, 0, 0, 0, 0));
        }
        catch (Exception ex)
        {
            // Failed native restoration stays visible; never report the baseline as applied.
            logger.LogWarning("Playback | sponsor-cache restore failed error={Error}", ex.GetType().Name);
        }
    }

    private void LogSponsorCacheBudget(string action, SponsorCacheBudget budget)
    {
        if (!logger.IsEnabled(LogLevel.Information)) return;
        logger.LogInformation("Playback | sponsor-cache action={Action} extra-mb={ExtraMb} limit-mb={LimitMb} segments={Segments} skip-secs={SkipSecs}",
            action, (budget.ExtraBytes / (double)SponsorCachePolicy.MiB).ToString("0.###", CultureInfo.InvariantCulture),
            (budget.Limits.MaxBytes / (double)SponsorCachePolicy.MiB).ToString("0.###", CultureInfo.InvariantCulture),
            action == "capped" ? budget.CappedSegments : budget.Segments,
            (action == "capped" ? budget.CappedSkipSeconds : budget.SkipSeconds).ToString("0.###", CultureInfo.InvariantCulture));
    }

    partial void DeferSponsorCacheAutomaticSelection(double position, double speed, ref bool switching)
    {
        switching |= CanAccountSponsorCache && SponsorCachePolicy.ShouldDeferSwitch(position, _sponsorCacheIntervals, speed);
    }

    partial void PrepareSponsorCacheSwitchWait(MpvPlayer player, CancellationToken token, ref Task wait)
    {
        if (CanAccountSponsorCache) wait = WaitForSponsorSkipAsync(player, token);
    }

    private async Task WaitForSponsorSkipAsync(MpvPlayer player, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var initial = await ReadPlaybackObservationAsync(player).WaitAsync(SponsorCachePolicy.MaximumSwitchWait, token);
            if (initial is not { Playing: true, Paused: false } observation
                || SponsorCachePolicy.SwitchSkipEnd(observation.Position, _sponsorCacheIntervals, observation.Speed) is not { } end) return;
            await SponsorCachePolicy.WaitForSkipAsync(end, async () =>
            {
                var actual = await ReadPlaybackObservationAsync(player);
                var pending = _sponsorCacheSeekClaims.Any(claim => claim.Effect.RequestId is null && claim.Effect.To >= end
                    && claim.Phase is SponsorOperationPhase.Pending or SponsorOperationPhase.SucceededUnobserved or SponsorOperationPhase.Unknown);
                var expected = CanAccountSponsorCache && _sponsorCacheIntervals.Any(interval => interval.EndSeconds >= end && interval.StartSeconds < end);
                return new(actual?.Position ?? double.NaN, actual is { Playing: true, Seeking: false, Buffering: false }, expected || pending);
            }, () => clock.Elapsed, (delay, cancellation) => Task.Delay(delay, cancellation), token);
        }
        catch (TimeoutException)
        {
            // The initial read shares the same deadline; an expired wait resumes normal selection.
        }
    }
}
