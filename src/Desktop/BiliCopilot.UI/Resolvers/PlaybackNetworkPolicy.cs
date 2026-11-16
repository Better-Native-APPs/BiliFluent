// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Resolvers;

/// <summary>
/// Every threshold of buffering, stream node ordering and automatic node switching.
/// </summary>
internal static class PlaybackNetworkPolicy
{
    /// <summary>
    /// Seconds of media mpv buffers before it resumes after the cache ran dry (cache-pause-wait).
    /// The mpv default of one second resumes too early on a slow node, which then plays a second
    /// and stalls again, over and over. Starting and seeking are not delayed, because
    /// cache-pause-initial stays off. Live streams keep the mpv default to stay close to real time.
    /// </summary>
    public const double CachePauseWaitSeconds = 3;

    /// <summary>
    /// How long a host that failed in this app session is ordered behind the other candidates.
    /// </summary>
    public static readonly TimeSpan HostFailureCooldown = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Automatic node switches allowed for one media, whatever their reason.
    /// </summary>
    public const int MaxSwitchesPerMedia = 4;

    /// <summary>
    /// Of those, the switches made ahead of a stall because the download is too slow.
    /// </summary>
    public const int MaxPredictedSwitchesPerMedia = 2;

    /// <summary>
    /// Automatic play URL requests allowed for one media, for used up candidates or expiring URLs.
    /// </summary>
    public const int MaxPlayUrlRefreshesPerMedia = 1;

    /// <summary>
    /// Waiting for data this long without the forward cache growing means the node has stalled.
    /// It covers opening, the restart after a seek, and a pause for the cache.
    /// </summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Waiting for data this long in one go means the node is far too slow, even while the forward
    /// cache grows a little: with <see cref="CachePauseWaitSeconds"/> to buffer, it delivers less
    /// than a tenth of what playback needs.
    /// </summary>
    public static readonly TimeSpan SlowWaitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The forward cache counts as growing once it gained this many bytes (fw-bytes).
    /// </summary>
    public const long CacheGrowthBytes = 64 * 1024;

    /// <summary>
    /// The forward cache counts as growing once it gained this many seconds (demuxer-cache-duration).
    /// </summary>
    public const double CacheGrowthSeconds = 0.5;

    /// <summary>
    /// Pauses for the cache are counted within this window.
    /// </summary>
    public static readonly TimeSpan RebufferWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// This many pauses for the cache in <see cref="RebufferWindow"/>, each one soon after playback
    /// resumed from the previous one, mean the node cannot keep up.
    /// </summary>
    public const int RebufferCount = 3;

    /// <summary>
    /// A pause for the cache continues the series when it starts within this time after playback resumed.
    /// </summary>
    public static readonly TimeSpan RebufferRelapse = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The span the download rate is averaged over and the trend of the cache is taken from.
    /// Nothing is predicted before playback has run this long since a start, a seek or a pause.
    /// </summary>
    public static readonly TimeSpan PredictionWindow = TimeSpan.FromSeconds(8);

    /// <summary>
    /// A stall is only predicted while less than this many seconds are buffered ahead. A node that
    /// reaches this level after a predicted switch has shown that it can keep up.
    /// </summary>
    public const double PredictionCacheSeconds = 15;

    /// <summary>
    /// A stall is predicted when the cache, falling as it did over <see cref="PredictionWindow"/>,
    /// runs dry within this time.
    /// </summary>
    public static readonly TimeSpan PredictionDepletion = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A stall is only predicted while the download rate is below this share of the rate playback
    /// takes data at (the bandwidth of the stream times the playback speed).
    /// </summary>
    public const double PredictionRateRatio = 0.8;

    /// <summary>
    /// After an automatic switch that loaded the media again, nothing is predicted for this long:
    /// the reload has emptied the cache. A switch inside the relay keeps the cache, so it is judged
    /// again as soon as <see cref="PredictionWindow"/> has passed.
    /// </summary>
    public static readonly TimeSpan PredictionSettle = TimeSpan.FromSeconds(20);

    /// <summary>
    /// After a switch inside the relay, the separate audio stream is not checked for this long.
    /// It covers the time the relay itself takes to leave an address that does not answer
    /// (its connect and read timeouts), so one dead address is not answered with two switches.
    /// </summary>
    public static readonly TimeSpan InPlaceSwitchSettle = TimeSpan.FromSeconds(6);

    /// <summary>
    /// The separate audio stream has stalled once, with no audio buffered ahead, the audio position
    /// stands still this long while the playback position moves on.
    /// </summary>
    public static readonly TimeSpan AudioStallTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How far the playback position must have moved during <see cref="AudioStallTimeout"/>, in seconds.
    /// </summary>
    public const double AudioStallAdvanceSeconds = 1;

    /// <summary>
    /// The audio position stands still while it changes by less than this many seconds.
    /// </summary>
    public const double AudioStallTolerance = 0.05;

    /// <summary>
    /// The audio is not checked for this long after a start, a seek or an audio switch.
    /// </summary>
    public static readonly TimeSpan AudioStallSettle = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The audio is not checked this close to the end, where it may end before the picture.
    /// </summary>
    public static readonly TimeSpan AudioStallEndMargin = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A signed URL with less than this left before its deadline is requested again instead of reused.
    /// </summary>
    public static readonly TimeSpan MinimumUrlLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Resuming with expiring URLs requests them again at most once in this time, so an answer
    /// that is itself about to expire cannot cause one reload per resume.
    /// </summary>
    public static readonly TimeSpan ResumeRefreshInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Interval of the cache samples that feed the stall detection.
    /// </summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A longer gap between two samples (system sleep, a blocked process) restarts every clock,
    /// so the time spent away is not taken for a stall.
    /// </summary>
    public static readonly TimeSpan SampleGapReset = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A missing audio track is confirmed after this delay before the audio node is switched.
    /// </summary>
    public static readonly TimeSpan AudioTrackConfirmDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// How long a reload holds the media gate while it waits for the file to restore the subtitle.
    /// It is longer than <see cref="StallTimeout"/>, so by then the switch away from a hanging node
    /// is already waiting for the gate; the subtitle is restored by whichever file loads next.
    /// </summary>
    public static readonly TimeSpan SubtitleRestoreWait = TimeSpan.FromSeconds(15);
}
