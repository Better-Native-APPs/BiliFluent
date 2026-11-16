// Copyright (c) BiliFluent. All rights reserved.

using Richasy.MpvKernel.Dash;
using Richasy.MpvKernel.Player.Models;

namespace BiliCopilot.UI.Resolvers;

internal readonly record struct SelectedRelayCacheSample(bool Stalled, double? CacheSeconds, long ForwardBytes, double DownloadRate, bool Idle);

/// <summary>
/// External tracks have their own demuxer. A highest delivered offset is an upper bound, not a
/// continuous cache: only continuous, fresh delivery may seed a cache trend, never a seek/probe peak.
/// </summary>
internal sealed class SelectedRelayWatchdog
{
    private string? _key;
    private TimeSpan? _lastSample;
    private long? _offset;
    private long _deliveredBytes;
    private bool _canEstimate;
    private TimeSpan? _waitingSince;
    private double _waitingPosition;

    public void Reset()
    {
        _key = null;
        _lastSample = null;
        _offset = null;
        _deliveredBytes = 0;
        _canEstimate = false;
        _waitingSince = null;
    }

    public SelectedRelayCacheSample? Observe(TimeSpan now, DateTimeOffset utcNow, MpvSeamlessStreamState? stream,
        PlaybackCacheSample playback, double speed)
    {
        if (stream is not { IsExternal: true, FragmentIndex: { } index, Statistics: { } statistics })
        {
            Reset();
            return null;
        }

        var reset = _key != stream.RelayKey || now - (_lastSample ?? now) > PlaybackNetworkPolicy.SampleGapReset
            || playback.UserPaused || playback.Seeking;
        if (reset) Reset();
        _key = stream.RelayKey;
        _lastSample = now;
        var offset = statistics.ForwardUpstreamOffset;
        var delivered = statistics.UpstreamBytes + statistics.PreloadedBytes;
        var latestByte = statistics.LastDeliveredAt;
        if (statistics.LastByteReceivedAt is { } received && (latestByte is null || received > latestByte)) latestByte = received;
        var fresh = latestByte is { } last && utcNow - last <= PlaybackNetworkPolicy.SelectedRelayFreshness;
        var upperSeconds = offset is { } forward ? Math.Max(0, CompleteFragmentEnd(index, forward) - playback.Position) : 0;

        if (!reset && offset is { } next && _offset is { } previous && next > previous)
        {
            var continuous = next - previous <= Math.Max(0, delivered - _deliveredBytes);
            if (!continuous) _canEstimate = false;
            else if (fresh && upperSeconds <= PlaybackNetworkPolicy.PredictionCacheSeconds) _canEstimate = true;
        }

        _offset = offset;
        _deliveredBytes = delivered;
        double? cacheSeconds = _canEstimate ? upperSeconds : null;
        var nearEnd = playback.Duration > 0 && playback.Duration - playback.Position < PlaybackNetworkPolicy.SelectedRelayEndMargin.TotalSeconds;
        var quiet = !fresh && statistics.BytesPerSecond <= 0 && statistics.DeliveredBytesPerSecond <= 0;
        var starved = quiet && statistics.ActiveRequests > 0 && !nearEnd && !playback.UserPaused && !playback.Seeking
            && (cacheSeconds is null || cacheSeconds <= PlaybackNetworkPolicy.SelectedRelayLowBufferSeconds * speed);
        if (!starved)
        {
            _waitingSince = null;
        }
        else if (_waitingSince is null)
        {
            _waitingSince = now;
            _waitingPosition = playback.Position;
        }

        // Unknown/non-contiguous cache needs the longer timeout and an advancing timeline, not a byte peak.
        var timeout = cacheSeconds is null ? PlaybackNetworkPolicy.SlowWaitTimeout : PlaybackNetworkPolicy.StallTimeout;
        var stalled = _waitingSince is { } since && now - since >= timeout
            && (cacheSeconds is not null || playback.Position - _waitingPosition >= PlaybackNetworkPolicy.SelectedRelayStallAdvanceSeconds);
        return new(stalled, cacheSeconds, statistics.ForwardBytes, statistics.BytesPerSecond,
            cacheSeconds is null || statistics.ActiveRequests == 0);
    }

    private static double CompleteFragmentEnd(DashIndex index, long offset)
    {
        var end = index.Fragments[0].Time;
        foreach (var fragment in index.Fragments)
        {
            if (fragment.EndOffset > offset) break;
            end = fragment.Time + fragment.Duration;
        }

        return end;
    }
}
