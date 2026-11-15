// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Resolvers;

internal enum CdnFailoverAction
{
    /// <summary>Nothing to do.</summary>
    None,

    /// <summary>Reload at the current position from the next address of <see cref="CdnFailoverDecision.Stream"/>.</summary>
    SwitchNode,

    /// <summary>Request the play URLs again, then reload at the current position.</summary>
    RefreshPlayUrl,

    /// <summary>Automatic handling is over for this media; the regular error display takes over.</summary>
    GiveUp,

    /// <summary>
    /// Another node would not help: the download cannot keep up on any node tried, which points
    /// at the connection rather than at a node. Nothing is reloaded; a lower quality is the remedy.
    /// </summary>
    ReportBandwidthLimited,
}

internal enum CdnFailoverReason
{
    /// <summary>The file could not be opened.</summary>
    OpenFailed,

    /// <summary>Opening did not finish in time.</summary>
    OpenTimeout,

    /// <summary>The file was loaded without its separate audio stream.</summary>
    AudioMissing,

    /// <summary>Playback waited for data while the forward cache did not grow.</summary>
    Stall,

    /// <summary>Playback paused for the cache again and again.</summary>
    Rebuffer,

    /// <summary>The download is slower than playback and the cache is about to run dry.</summary>
    Predicted,

    /// <summary>The picture moves on while the separate audio stream delivers nothing.</summary>
    AudioStall,
}

/// <summary>
/// What to do about a failing node.
/// </summary>
/// <param name="Action">The step to take.</param>
/// <param name="Stream">The stream whose address changes, for <see cref="CdnFailoverAction.SwitchNode"/>.</param>
/// <param name="Reason">What was observed.</param>
/// <param name="Attempt">The number of this automatic step within the media, starting at 1.</param>
internal readonly record struct CdnFailoverDecision(
    CdnFailoverAction Action,
    PlaybackStreamKind Stream,
    CdnFailoverReason Reason,
    int Attempt)
{
    public static CdnFailoverDecision None => default;
}

/// <summary>
/// One reading of the playback and cache state.
/// </summary>
/// <param name="UserPaused">The user has paused playback.</param>
/// <param name="PausedForCache">mpv has paused playback until the cache has refilled.</param>
/// <param name="Seeking">mpv is seeking, or restarting playback after a seek or a load.</param>
/// <param name="CacheIdle">
/// mpv is not reading: the readahead is full or the end of the file is buffered (demuxer-cache-idle).
/// </param>
/// <param name="CacheSeconds">Seconds of media buffered ahead (demuxer-cache-duration).</param>
/// <param name="ForwardBytes">Bytes buffered ahead (fw-bytes).</param>
/// <param name="DownloadRate">Bytes per second the main file is read at (cache-speed).</param>
/// <param name="RequiredRate">
/// Bytes per second playback takes from the main file at the current speed; 0 when unknown.
/// </param>
/// <param name="Position">The playback position in seconds (time-pos).</param>
/// <param name="AudioPosition">The position of the audio that is being played (audio-pts); null when there is none.</param>
/// <param name="AudioCacheSeconds">
/// Seconds of audio buffered ahead; null when mpv does not report the cache of the audio stream.
/// </param>
/// <param name="Duration">The duration of the media in seconds; 0 when unknown.</param>
internal readonly record struct PlaybackCacheSample(
    bool UserPaused,
    bool PausedForCache,
    bool Seeking,
    bool CacheIdle,
    double CacheSeconds,
    long ForwardBytes,
    double DownloadRate,
    double RequiredRate,
    double Position,
    double? AudioPosition,
    double? AudioCacheSeconds,
    double Duration);

/// <summary>
/// What is left to fall back on for the source that is loaded.
/// </summary>
/// <param name="HasVideo">The source has a video stream.</param>
/// <param name="HasAudio">The source has a separate audio stream.</param>
/// <param name="VideoRemaining">Video addresses that were not tried for this media yet.</param>
/// <param name="AudioRemaining">Audio addresses that were not tried for this media yet.</param>
/// <param name="UrlLifetime">Time left before the signed URLs stop working; null when unknown.</param>
internal readonly record struct StreamAvailability(
    bool HasVideo,
    bool HasAudio,
    int VideoRemaining,
    int AudioRemaining,
    TimeSpan? UrlLifetime);

/// <summary>
/// Decides when the node a media is read from has failed, and whether to move to another address,
/// to request the play URLs again or to stop trying. It only sees events, samples and the time it
/// is given, and touches no player and no UI. One instance serves one player; it is not thread-safe.
/// </summary>
/// <remarks>
/// The time passed to it may come from any monotonic clock. Loading, loaded and ended events must
/// arrive in the order mpv raised them.
/// </remarks>
internal sealed class CdnFailoverPolicy
{
    private readonly List<TimeSpan> _cachePauses = [];
    private readonly List<(TimeSpan Time, double CacheSeconds)> _cacheTrend = [];
    private readonly List<(TimeSpan Time, double Rate)> _downloadRates = [];
    private Phase _phase;
    private int _switches;
    private int _predictedSwitches;
    private bool _predictionOver;
    private bool _nodeKeptUp;
    private TimeSpan? _noPredictionBefore;
    private (TimeSpan Time, double Position, double AudioPosition)? _audioMark;
    private TimeSpan? _noAudioCheckBefore;
    private int _refreshes;
    private bool _candidatesUsedUp;
    private PlaybackStreamKind? _lastSwitched;
    private TimeSpan? _waitingSince;
    private TimeSpan? _waitStarted;
    private double _waitingCacheSeconds;
    private long _waitingForwardBytes;
    private TimeSpan? _lastSample;
    private TimeSpan? _resumedAt;
    private bool _pausedForCache;

    private enum Phase
    {
        // No load of this media has been seen, or the file has ended.
        Idle,

        // A load has started and has neither finished nor failed.
        Opening,

        // The file is loaded.
        Loaded,

        // A decision is being carried out; the next load starts a new observation.
        Switching,

        // Automatic handling is over for this media.
        Finished,
    }

    /// <summary>
    /// The automatic steps taken for this media so far.
    /// </summary>
    public int Attempts => _switches + _refreshes;

    /// <summary>
    /// Bytes per second the main file was read at, averaged over
    /// <see cref="PlaybackNetworkPolicy.PredictionWindow"/>; null before the first sample of a file.
    /// </summary>
    public double? DownloadRate => _downloadRates.Count > 0 ? _downloadRates.Average(sample => sample.Rate) : null;

    /// <summary>
    /// A load is under way whose outcome this policy answers for: errors reported meanwhile
    /// belong to it, and are not to be shown before the outcome is known.
    /// </summary>
    public bool IsAwaitingLoad => _phase is Phase.Opening or Phase.Switching;

    /// <summary>
    /// Forgets everything; call when another media starts.
    /// </summary>
    public void Reset()
    {
        _phase = Phase.Idle;
        _switches = 0;
        _refreshes = 0;
        _candidatesUsedUp = false;
        _lastSwitched = null;
        _predictedSwitches = 0;
        _predictionOver = false;
        _nodeKeptUp = false;
        _noPredictionBefore = null;
        _noAudioCheckBefore = null;
        _downloadRates.Clear();
        ResetClocks();
    }

    /// <summary>
    /// mpv started to load a file.
    /// </summary>
    public void OnFileLoading(TimeSpan now)
    {
        if (_phase == Phase.Finished)
        {
            return;
        }

        _phase = Phase.Opening;
        _downloadRates.Clear();
        ResetClocks();
        _waitStarted = now;
        StartWaiting(now, default);
    }

    /// <summary>
    /// mpv opened the file; the first frame is still to come.
    /// </summary>
    public void OnFileLoaded(TimeSpan now)
    {
        if (_phase != Phase.Opening)
        {
            return;
        }

        _phase = Phase.Loaded;
        _cacheTrend.Clear();
        SettleAudio(now);
        _waitStarted = now;
        StartWaiting(now, default);
    }

    /// <summary>
    /// The file ended because it could not be read from its address.
    /// </summary>
    public CdnFailoverDecision OnFileFailed(TimeSpan now, StreamAvailability availability)
        => _phase == Phase.Opening ? Decide(CdnFailoverReason.OpenFailed, null, availability, now) : CdnFailoverDecision.None;

    /// <summary>
    /// The file ended, for whatever reason. Call after <see cref="OnFileFailed"/>.
    /// </summary>
    public void OnFileEnded()
    {
        if (_phase is Phase.Opening or Phase.Loaded)
        {
            // A load that was replaced or stopped is not a failure of the node.
            _phase = Phase.Idle;
            ResetClocks();
        }
    }

    /// <summary>
    /// The loaded file lacks the separate audio stream it was given.
    /// </summary>
    public CdnFailoverDecision OnAudioMissing(TimeSpan now, StreamAvailability availability)
        => _phase == Phase.Loaded
            ? Decide(CdnFailoverReason.AudioMissing, PlaybackStreamKind.Audio, availability, now)
            : CdnFailoverDecision.None;

    /// <summary>
    /// A seek started. It drops the forward cache, so what follows is no sign of a failing node.
    /// </summary>
    public void OnSeek(TimeSpan now)
    {
        _waitingSince = null;
        _waitStarted = null;
        _cacheTrend.Clear();
        SettleAudio(now);
        ResetRebuffering();
    }

    /// <summary>
    /// Playback restarted after a load or a seek: data has arrived, and the cache starts over.
    /// </summary>
    public void OnPlaybackRestart(TimeSpan now)
    {
        _waitingSince = null;
        _waitStarted = null;
        _cacheTrend.Clear();
        SettleAudio(now);
    }

    /// <summary>
    /// The decision could not be carried out; automatic handling ends for this media.
    /// </summary>
    public void Abandon()
    {
        if (_phase == Phase.Switching)
        {
            _phase = Phase.Finished;
        }
    }

    /// <summary>
    /// Takes a reading of the cache, about once per <see cref="PlaybackNetworkPolicy.SampleInterval"/>.
    /// </summary>
    public CdnFailoverDecision OnSample(TimeSpan now, PlaybackCacheSample sample, StreamAvailability availability)
    {
        if (_phase is not (Phase.Opening or Phase.Loaded))
        {
            return CdnFailoverDecision.None;
        }

        var gap = now - (_lastSample ?? now);
        _lastSample = now;
        if (gap > PlaybackNetworkPolicy.SampleGapReset)
        {
            _downloadRates.Clear();
        }

        _downloadRates.Add((now, Math.Max(0, sample.DownloadRate)));
        _downloadRates.RemoveAll(rate => now - rate.Time > PlaybackNetworkPolicy.PredictionWindow);
        // A paused player still opens the file, so a pause does not excuse an address that will not open.
        if (gap > PlaybackNetworkPolicy.SampleGapReset || (sample.UserPaused && _phase == Phase.Loaded))
        {
            // Time the user spent paused, or the system spent asleep, counts for nothing.
            ResetClocks();
            _lastSample = now;
            return CdnFailoverDecision.None;
        }

        // mpv pauses for the cache of the main file only, so that is the stream at fault.
        var main = availability.HasVideo ? PlaybackStreamKind.Video : PlaybackStreamKind.Audio;
        if (_phase == Phase.Loaded && IsRebuffering(now, sample.PausedForCache))
        {
            return Decide(CdnFailoverReason.Rebuffer, main, availability, now);
        }

        // A filled cache that stops growing is never waiting, so it cannot look like a stall.
        var waiting = _phase == Phase.Opening || sample.Seeking || sample.PausedForCache;
        if (!waiting)
        {
            _waitingSince = null;
            _waitStarted = null;
            return IsAudioStalled(now, sample, availability)
                ? Decide(CdnFailoverReason.AudioStall, PlaybackStreamKind.Audio, availability, now)
                : Predict(now, sample, availability);
        }

        _cacheTrend.Clear();
        _audioMark = null;
        // Opening and restarting need both streams; a pause for the cache is about the main file.
        PlaybackStreamKind? suspect = _phase == Phase.Loaded && sample.PausedForCache && !sample.Seeking ? main : null;
        var reason = _phase == Phase.Opening ? CdnFailoverReason.OpenTimeout : CdnFailoverReason.Stall;
        _waitStarted ??= now;
        if (now - _waitStarted.Value >= PlaybackNetworkPolicy.SlowWaitTimeout)
        {
            return Decide(reason, suspect, availability, now);
        }

        if (_waitingSince is not { } since || HasGrown(sample))
        {
            StartWaiting(now, sample);
            return CdnFailoverDecision.None;
        }

        // A dropped cache lowers the mark the growth is measured from, without restarting the wait.
        _waitingCacheSeconds = Math.Min(_waitingCacheSeconds, sample.CacheSeconds);
        _waitingForwardBytes = Math.Min(_waitingForwardBytes, sample.ForwardBytes);
        return now - since < PlaybackNetworkPolicy.StallTimeout
            ? CdnFailoverDecision.None
            : Decide(reason, suspect, availability, now);
    }

    // Playback is running. True once the picture has moved on for a while with no audio buffered
    // and none played: mpv only pauses for the cache of the main file, so a dry audio stream goes
    // unnoticed there. Without the cache of the audio stream nothing is concluded: silence with
    // audio still buffered is a matter of the audio output, which no other node can cure.
    private bool IsAudioStalled(TimeSpan now, PlaybackCacheSample sample, StreamAvailability availability)
    {
        var nearEnd = sample.Duration > 0 && sample.Duration - sample.Position < PlaybackNetworkPolicy.AudioStallEndMargin.TotalSeconds;
        if (_phase != Phase.Loaded || !availability.HasVideo || !availability.HasAudio
            || sample.AudioCacheSeconds is not <= 0 || sample.AudioPosition is not { } audio
            || now < _noAudioCheckBefore || nearEnd)
        {
            _audioMark = null;
            return false;
        }

        if (_audioMark is not { } mark || Math.Abs(audio - mark.AudioPosition) > PlaybackNetworkPolicy.AudioStallTolerance
            || sample.Position < mark.Position)
        {
            _audioMark = (now, sample.Position, audio);
            return false;
        }

        return now - mark.Time >= PlaybackNetworkPolicy.AudioStallTimeout
            && sample.Position - mark.Position >= PlaybackNetworkPolicy.AudioStallAdvanceSeconds;
    }

    private void SettleAudio(TimeSpan now)
    {
        _audioMark = null;
        _noAudioCheckBefore = now + PlaybackNetworkPolicy.AudioStallSettle;
    }

    // Playback is running. Decides whether the cache is being used up faster than it is filled,
    // so that the node is left before playback has to stop.
    private CdnFailoverDecision Predict(TimeSpan now, PlaybackCacheSample sample, StreamAvailability availability)
    {
        if (sample.CacheSeconds >= PlaybackNetworkPolicy.PredictionCacheSeconds)
        {
            // Reached while playing: a pause fills the cache on any node and proves nothing.
            _nodeKeptUp = true;
        }

        if (_predictionOver || sample.CacheIdle)
        {
            // A full readahead, or a buffered end of file, stops the download: that is no slow node.
            _cacheTrend.Clear();
            return CdnFailoverDecision.None;
        }

        _cacheTrend.Add((now, sample.CacheSeconds));
        _cacheTrend.RemoveAll(point => now - point.Time > PlaybackNetworkPolicy.PredictionWindow + PlaybackNetworkPolicy.SampleInterval);
        var first = _cacheTrend[0];
        var elapsed = (now - first.Time).TotalSeconds;

        // The window also keeps the first seconds after a start, a seek or a pause out of the verdict.
        if (elapsed < PlaybackNetworkPolicy.PredictionWindow.TotalSeconds
            || now < _noPredictionBefore
            || sample.CacheSeconds >= PlaybackNetworkPolicy.PredictionCacheSeconds)
        {
            return CdnFailoverDecision.None;
        }

        // Falling over both halves of the window, not a dip that is already recovering.
        var middle = _cacheTrend[_cacheTrend.Count / 2];
        if (first.CacheSeconds <= middle.CacheSeconds || middle.CacheSeconds <= sample.CacheSeconds)
        {
            return CdnFailoverDecision.None;
        }

        var lossPerSecond = (first.CacheSeconds - sample.CacheSeconds) / elapsed;
        if (sample.CacheSeconds / lossPerSecond > PlaybackNetworkPolicy.PredictionDepletion.TotalSeconds)
        {
            return CdnFailoverDecision.None;
        }

        if (sample.RequiredRate <= 0 || DownloadRate is not { } rate
            || rate >= PlaybackNetworkPolicy.PredictionRateRatio * sample.RequiredRate)
        {
            return CdnFailoverDecision.None;
        }

        // The measurements describe the main file, so that is the stream to move.
        var stream = availability.HasVideo ? PlaybackStreamKind.Video : PlaybackStreamKind.Audio;
        var fellBehindAgain = _predictedSwitches > 0 && !_nodeKeptUp;
        if (fellBehindAgain || _predictedSwitches >= PlaybackNetworkPolicy.MaxPredictedSwitchesPerMedia
            || _candidatesUsedUp || _switches >= PlaybackNetworkPolicy.MaxSwitchesPerMedia
            || Remaining(stream, availability) == 0)
        {
            // The node it was moved to is no faster, or there is none left to move to.
            _predictionOver = true;
            return new(CdnFailoverAction.ReportBandwidthLimited, stream, CdnFailoverReason.Predicted, Attempts);
        }

        _predictedSwitches++;
        _nodeKeptUp = false;
        return Switch(stream, CdnFailoverReason.Predicted, now);
    }

    private bool HasGrown(PlaybackCacheSample sample)
        => sample.ForwardBytes >= _waitingForwardBytes + PlaybackNetworkPolicy.CacheGrowthBytes
            || sample.CacheSeconds >= _waitingCacheSeconds + PlaybackNetworkPolicy.CacheGrowthSeconds;

    // Sets the mark the growth of the cache is measured from; the wait as a whole goes on.
    private void StartWaiting(TimeSpan now, PlaybackCacheSample sample)
    {
        _waitingSince = now;
        _waitingCacheSeconds = sample.CacheSeconds;
        _waitingForwardBytes = sample.ForwardBytes;
    }

    // True once pauses for the cache keep following each other soon after playback resumed.
    private bool IsRebuffering(TimeSpan now, bool pausedForCache)
    {
        var wasPaused = _pausedForCache;
        _pausedForCache = pausedForCache;
        if (pausedForCache == wasPaused)
        {
            return false;
        }

        if (!pausedForCache)
        {
            _resumedAt = now;
            return false;
        }

        if (_resumedAt is not { } resumed || now - resumed > PlaybackNetworkPolicy.RebufferRelapse)
        {
            // Playback held out long enough since the last pause: a new series starts here.
            _cachePauses.Clear();
        }

        _cachePauses.Add(now);
        _cachePauses.RemoveAll(started => now - started > PlaybackNetworkPolicy.RebufferWindow);
        return _cachePauses.Count >= PlaybackNetworkPolicy.RebufferCount;
    }

    private void ResetRebuffering()
    {
        _cachePauses.Clear();
        _resumedAt = null;
        _pausedForCache = false;
    }

    private void ResetClocks()
    {
        _waitingSince = null;
        _waitStarted = null;
        _lastSample = null;
        _audioMark = null;
        _cacheTrend.Clear();
        ResetRebuffering();
    }

    private CdnFailoverDecision Decide(CdnFailoverReason reason, PlaybackStreamKind? suspect, StreamAvailability availability, TimeSpan now)
    {
        if (_phase is Phase.Switching or Phase.Finished)
        {
            return CdnFailoverDecision.None;
        }

        var canRefresh = _refreshes < PlaybackNetworkPolicy.MaxPlayUrlRefreshesPerMedia;
        var expiring = availability.UrlLifetime is { } lifetime && lifetime < PlaybackNetworkPolicy.MinimumUrlLifetime;
        if (!_candidatesUsedUp && expiring && canRefresh)
        {
            // Every address of an expiring answer expires with it; this is no failure of a node.
            return Refresh(reason, now);
        }

        if (!_candidatesUsedUp && _switches < PlaybackNetworkPolicy.MaxSwitchesPerMedia
            && PickStream(suspect, availability) is { } stream)
        {
            return Switch(stream, reason, now);
        }

        if (!_candidatesUsedUp && canRefresh)
        {
            // The last resort: whatever happens to the fresh addresses is left to the error display.
            _candidatesUsedUp = true;
            return Refresh(reason, now);
        }

        _phase = Phase.Finished;
        ResetClocks();
        return new(CdnFailoverAction.GiveUp, suspect ?? PlaybackStreamKind.Video, reason, Attempts);
    }

    private CdnFailoverDecision Switch(PlaybackStreamKind stream, CdnFailoverReason reason, TimeSpan now)
    {
        _switches++;
        _lastSwitched = stream;
        return Begin(CdnFailoverAction.SwitchNode, stream, reason, now);
    }

    private CdnFailoverDecision Refresh(CdnFailoverReason reason, TimeSpan now)
    {
        _refreshes++;
        _lastSwitched = null;
        return Begin(CdnFailoverAction.RefreshPlayUrl, PlaybackStreamKind.Video, reason, now);
    }

    private CdnFailoverDecision Begin(CdnFailoverAction action, PlaybackStreamKind stream, CdnFailoverReason reason, TimeSpan now)
    {
        _phase = Phase.Switching;
        ResetClocks();

        // The reload empties the cache; the new node is judged only once it has had time to fill it.
        _noPredictionBefore = now + PlaybackNetworkPolicy.PredictionSettle;
        return new(action, stream, reason, Attempts);
    }

    // The stream to move to another address, or null when no untried address is left for it.
    private PlaybackStreamKind? PickStream(PlaybackStreamKind? suspect, StreamAvailability availability)
    {
        if (suspect is { } known)
        {
            return Remaining(known, availability) > 0 ? known : null;
        }

        // Nothing tells the two streams apart: video first, then audio, in turns.
        var first = !availability.HasVideo || (availability.HasAudio && _lastSwitched == PlaybackStreamKind.Video)
            ? PlaybackStreamKind.Audio
            : PlaybackStreamKind.Video;
        var second = first == PlaybackStreamKind.Video ? PlaybackStreamKind.Audio : PlaybackStreamKind.Video;
        return Remaining(first, availability) > 0 ? first
            : Remaining(second, availability) > 0 ? second
            : null;
    }

    private static int Remaining(PlaybackStreamKind kind, StreamAvailability availability)
        => kind == PlaybackStreamKind.Video
            ? availability.HasVideo ? availability.VideoRemaining : 0
            : availability.HasAudio ? availability.AudioRemaining : 0;
}
