// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Resolvers;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.Extensions.Logging;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Core.Enums;
using Richasy.MpvKernel.Core.Models;
using Richasy.MpvKernel.Player;
using Richasy.MpvKernel.Player.Models;
using Richasy.MpvKernel.Relay;
using System.Globalization;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// Watches the node a media is read from and, without telling the user, moves to another one
/// when <see cref="CdnFailoverPolicy"/> says so.
/// </summary>
public sealed partial class PlayerViewModel
{
    private readonly CdnFailoverPolicy _failover = new();
    private MpvClient? _networkClient;
    private NetworkSampler? _networkSampler;
    private Task _networkSamplerDrain = Task.CompletedTask;

    // Cuts off the events of an earlier media, including those already queued for the UI thread.
    private long _networkEpoch;
    private long _networkLoads;

    // Written by the mpv event thread.
    private long _forwardCacheBytes;
    private long _rawInputRate;
    private MpvCacheStateEventArgs? _cacheState;

    private volatile NetworkDiagnostics _networkDiagnostics = NetworkDiagnostics.Empty;
    private int _networkSwitches;

    // Addresses the relay left by itself for this media; they need no decision of the policy.
    private int _relayFailovers;
    private bool _userPausedSample;
    private MpvError? _deferredLoadError;

    // Where and how an automatic reload was to continue, kept until a file has loaded again.
    private (double? Position, bool Paused, double? Speed)? _reloadState;
    private bool _hasLoadedFile;
    private SubtitleItemViewModel? _subtitleToRestore;
    private long _lastResumeRefresh;

    /// <summary>
    /// The download cannot keep up with playback on any node tried for this media, which points
    /// at the connection rather than at a node. Raised on the UI thread, at most once per media.
    /// It is the signal for lowering the quality automatically; nothing consumes it yet.
    /// </summary>
    internal event EventHandler<PlaybackBandwidthLimitedEventArgs>? BandwidthLimited;

    private static TimeSpan NetworkNow => TimeSpan.FromMilliseconds(Environment.TickCount64);

    // Starts with the configured client of a media; the sampler is the only part that uses the native handle.
    private void StartNetworkMonitor()
    {
        if (!CanUseOnDemandNetwork || IsPlaybackClosing || Client is not { IsDisposed: false } client)
        {
            return;
        }

        DetachNetworkMonitor();
        _networkClient = client;
        client.ReachFileLoading += OnNetworkFileLoading;
        client.ReachFileLoaded += OnNetworkFileLoaded;
        client.FileFailed += OnNetworkFileFailed;
        client.ReachFileEnd += OnNetworkFileEnded;
        client.SeekStarted += OnNetworkSeekStarted;
        client.DataNotify += OnNetworkDataNotify;
        _networkSampler ??= new NetworkSampler(client, logger, sample => PostNetworkEvent(() => OnNetworkSample(sample)));
    }

    // Synchronous, so that nothing of the previous media reaches the policy of the next one.
    private void ResetNetworkFailover()
    {
        DetachNetworkMonitor();
        _failover.Reset();
        _deferredLoadError = null;
        _reloadState = null;
        _hasLoadedFile = false;
        _subtitleToRestore = null;
        _userPausedSample = false;
        _lastResumeRefresh = 0;
        Volatile.Write(ref _networkSwitches, 0);
        Volatile.Write(ref _relayFailovers, 0);
        Volatile.Write(ref _forwardCacheBytes, 0);
        Volatile.Write(ref _rawInputRate, 0);
        Volatile.Write(ref _cacheState, null);
        _networkDiagnostics = NetworkDiagnostics.Empty;
    }

    private Task SuspendNetworkMonitorAsync()
    {
        DetachNetworkMonitor();
        var sampler = _networkSampler;
        _networkSampler = null;
        if (sampler is not null)
        {
            // Retain every retired sampler until it has stopped using the native handle.
            _networkSamplerDrain = OwnWork(Task.WhenAll(_networkSamplerDrain, sampler.DisposeAsync().AsTask()));
        }

        return _networkSamplerDrain;
    }

    private void DetachNetworkMonitor()
    {
        Interlocked.Increment(ref _networkEpoch);
        if (_networkClient is not { } client)
        {
            return;
        }

        client.ReachFileLoading -= OnNetworkFileLoading;
        client.ReachFileLoaded -= OnNetworkFileLoaded;
        client.FileFailed -= OnNetworkFileFailed;
        client.ReachFileEnd -= OnNetworkFileEnded;
        client.SeekStarted -= OnNetworkSeekStarted;
        client.DataNotify -= OnNetworkDataNotify;
        _networkClient = null;
    }

    // Runs the handler on the UI thread, unless another media has taken over meanwhile.
    private void PostNetworkEvent(Action handler)
    {
        var epoch = Volatile.Read(ref _networkEpoch);
        var version = Volatile.Read(ref _mediaVersion);
        queue.TryEnqueue(() =>
        {
            if (CanUseOnDemandNetwork && epoch == Volatile.Read(ref _networkEpoch) && IsCurrentPlayback(version))
            {
                handler();
            }
        });
    }

    private void OnNetworkFileLoading(object? sender, EventArgs e)
    {
        // The cache of the previous file says nothing about this one.
        Volatile.Write(ref _forwardCacheBytes, 0);
        Volatile.Write(ref _rawInputRate, 0);
        Volatile.Write(ref _cacheState, null);
        PostNetworkEvent(() =>
        {
            _networkLoads++;
            _deferredLoadError = null;
            _failover.OnFileLoading(NetworkNow);
        });
    }

    private void OnNetworkFileLoaded(object? sender, EventArgs e)
        => PostNetworkEvent(() =>
        {
            _deferredLoadError = null;
            _reloadState = null;
            _hasLoadedFile = true;
            _failover.OnFileLoaded(NetworkNow);
            RestoreSubtitleAfterReload();
            _ = OwnWork(ConfirmAudioTrackAsync(_networkLoads, Volatile.Read(ref _networkEpoch), _mediaVersion, _activeMediaToken));
        });

    private void OnNetworkFileFailed(object? sender, MpvError e)
    {
        // Only what an address can be blamed for: it cannot be opened, or it does not return media.
        // A decoder or an output that fails would fail on every node.
        if (e is not (MpvError.LoadingFailed or MpvError.UnknownFormat))
        {
            return;
        }

        PostNetworkEvent(() =>
        {
            if (GetStreamAvailability() is { } availability)
            {
                ApplyFailoverDecision(_failover.OnFileFailed(NetworkNow, availability));
            }
        });
    }

    private void OnNetworkFileEnded(object? sender, EventArgs e)
        => PostNetworkEvent(() =>
        {
            _failover.OnFileEnded();
            if (!_failover.IsAwaitingLoad)
            {
                // The load is over and nothing is being tried for it: its errors are shown after all.
                ShowDeferredLoadError();
            }
        });

    private void OnNetworkSeekStarted(object? sender, EventArgs e)
        => PostNetworkEvent(() => _failover.OnSeek(NetworkNow));

    private void OnNetworkDataNotify(object? sender, MpvClientNotifyEventArgs e)
    {
        if (e.Id == MpvClientEventId.PlaybackRestart)
        {
            PostNetworkEvent(() => _failover.OnPlaybackRestart(NetworkNow));
        }
    }

    private void OnNetworkSample(NetworkSample raw)
    {
        if (!CanUseOnDemandNetwork) return;
        _userPausedSample = raw.UserPaused;
        var streams = _sourceResolver?.CurrentStreams;
        var cache = Volatile.Read(ref _cacheState);
        var forwardBytes = Volatile.Read(ref _forwardCacheBytes);
        var speed = Player?.PlaybackRate is > 0 and var rate ? rate : 1d;

        var mainBandwidth = GetMainBandwidth(streams);

        // The relay counts the bytes of the main file as they arrive from the node, which is the
        // rate mpv can only estimate from what reaches its cache.
        var relayed = streams is null ? null : Player?.GetRelayStatistics(streams.Video is not null ? MpvRelayStreamKind.Video : MpvRelayStreamKind.Audio);
        var sample = new PlaybackCacheSample(
            raw.UserPaused,
            raw.PausedForCache,
            raw.Seeking,
            raw.CacheIdle,
            raw.CacheSeconds,
            forwardBytes,
            relayed?.BytesPerSecond ?? raw.DownloadRate,
            mainBandwidth / 8d * speed,
            raw.Position,
            raw.AudioPosition,
            cache?.AudioCacheSeconds,
            Player?.Duration ?? 0);
        var decision = streams is null
            ? CdnFailoverDecision.None
            : _failover.OnSample(NetworkNow, sample, GetStreamAvailability(streams));
        _networkDiagnostics = new(
            streams?.Video?.Category ?? CdnHost.NoCategory,
            streams?.Audio?.Category ?? CdnHost.NoCategory,
            streams?.Quality,
            streams?.VideoBandwidth ?? 0,
            streams?.AudioBandwidth ?? 0,
            mainBandwidth,
            Volatile.Read(ref _rawInputRate),
            forwardBytes,
            _failover.DownloadRate,
            cache?.VideoCacheSeconds,
            cache?.AudioCacheSeconds,
            Volatile.Read(ref _networkSwitches) + Volatile.Read(ref _relayFailovers));
        ApplyFailoverDecision(decision, sample);
    }

    // mpv measures the cache and the download of the main file only: the video stream, or the audio
    // stream of a media without video. An audio stream added with audio-file has a demuxer of its own.
    private static int GetMainBandwidth(PlaybackStreamSelection? streams)
        => streams is null ? 0 : streams.Video is not null ? streams.VideoBandwidth : streams.AudioBandwidth;

    private StreamAvailability? GetStreamAvailability()
        => CanUseOnDemandNetwork && _sourceResolver?.CurrentStreams is { } streams ? GetStreamAvailability(streams) : null;

    private static StreamAvailability GetStreamAvailability(PlaybackStreamSelection streams)
        => new(
            streams.Video is not null,
            streams.Audio is not null,
            streams.VideoRemaining,
            streams.AudioRemaining,
            streams.Deadline - DateTimeOffset.UtcNow);

    // mpv keeps playing without sound when the audio address cannot be opened.
    private async Task ConfirmAudioTrackAsync(long load, long epoch, long version, CancellationToken token)
    {
        if (!CanUseOnDemandNetwork || !CanSelectAudioTrack) return;
        try
        {
            if (await HasAudioTrackAsync() != false)
            {
                return;
            }

            // Looked at twice, so a track list that is still being filled cannot cost a reload.
            await Task.Delay(PlaybackNetworkPolicy.AudioTrackConfirmDelay, token);
            if (load != _networkLoads || epoch != Volatile.Read(ref _networkEpoch) || !IsCurrentPlayback(version)
                || await HasAudioTrackAsync() != false || load != _networkLoads || !IsCurrentPlayback(version)
                || GetStreamAvailability() is not { } availability)
            {
                return;
            }

            ApplyFailoverDecision(_failover.OnAudioMissing(NetworkNow, availability));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning("Playback | audio track check failed error={Error}", ex.GetType().Name);
        }

        // Null when it cannot be told, or when the source has no separate audio stream to miss.
        async Task<bool?> HasAudioTrackAsync()
        {
            if (_sourceResolver?.CurrentStreams is not { Video: not null, Audio: not null }
                || Client is not { IsDisposed: false } client)
            {
                return null;
            }

            var tracks = await client.GetTracksAsync();
            return tracks.IsSuccess ? tracks.Value.Exists(track => track.Type == MpvTrackType.Audio) : null;
        }
    }

    private void ApplyFailoverDecision(CdnFailoverDecision decision, PlaybackCacheSample sample = default)
    {
        if (!CanUseOnDemandNetwork || decision.Action == CdnFailoverAction.None)
        {
            return;
        }

        var player = Player;
        var resolver = _sourceResolver;
        var from = resolver?.CurrentStreams;
        var reason = DescribeFailoverReason(decision.Reason);
        if (decision.Action == CdnFailoverAction.ReportBandwidthLimited)
        {
            var download = (_failover.DownloadRate ?? 0) * 8 / 1000;
            var required = sample.RequiredRate * 8 / 1000;
            if (logger.IsEnabled(LogLevel.Information))
            {
                var downloadText = download.ToString("0", CultureInfo.InvariantCulture);
                var requiredText = required.ToString("0", CultureInfo.InvariantCulture);
                logger.LogInformation("Playback | bandwidth-limited dl-kbps={DownloadKbps} need-kbps={RequiredKbps} qn={Quality} cdn-switches={Switches}",
                    downloadText, requiredText, from?.Quality ?? 0, decision.Attempt);
            }

            BandwidthLimited?.Invoke(this, new(from?.Quality ?? 0, download, required));
            return;
        }

        if (decision.Action == CdnFailoverAction.GiveUp)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Playback | cdn-switch gave up reason={Reason} attempts={Attempts}", reason, decision.Attempt);
            }

            if (decision.Reason is CdnFailoverReason.OpenFailed or CdnFailoverReason.OpenTimeout)
            {
                // The load is over even when mpv reported no error of its own.
                _deferredLoadError ??= MpvError.LoadingFailed;
            }

            ShowDeferredLoadError();
            return;
        }

        if (player is null || resolver is null || from is null)
        {
            _failover.Abandon();
            ShowDeferredLoadError();
            return;
        }

        Volatile.Write(ref _networkSwitches, decision.Attempt);
        var version = _mediaVersion;
        var rejected = false;

        // What is observed while a file plays can be answered inside the relay: mpv keeps its file,
        // its position and its cache. Any other reason has no loaded file to keep.
        if (_hasLoadedFile && IsObservedWhilePlaying(decision.Reason))
        {
            if (decision.Action == CdnFailoverAction.RefreshPlayUrl)
            {
                if (IsRelayingEvery(player, from))
                {
                    _ = ObserveNetworkReloadAsync(OwnWork(RefreshForFailoverAsync(player, resolver, from, decision, reason, version, _activeMediaToken)));
                    return;
                }
            }
            else if (player.IsRelaying(ToRelayKind(decision.Stream)))
            {
                if (TrySwitchNodeInPlace(player, resolver, from, decision, reason))
                {
                    return;
                }

                // The address was given up by the attempt; the reload moves on to the next one.
                rejected = true;
            }
        }

        ReloadForFailover(player, resolver, from, decision, reason, version, rejected, refreshed: false);
    }

    // Moves the stream to its next address inside the relay. The response mpv is reading goes on
    // from that address at the byte it has reached, so nothing is loaded and nothing is dropped.
    // False when no untried address is left or the relay does not know it; the caller reloads.
    private bool TrySwitchNodeInPlace(MpvPlayer player, MediaSourceResolverBase resolver, PlaybackStreamSelection from, CdnFailoverDecision decision, string reason)
    {
        if (!CanUseOnDemandNetwork) return false;
        var kind = ToRelayKind(decision.Stream);
        var left = player.GetRelayStatistics(kind);
        if (resolver.MoveToNextStream(decision.Stream) is not { } next || !player.TrySwitchRelayNode(kind, next.Url))
        {
            return false;
        }

        _deferredLoadError = null;
        _failover.OnSwitchedInPlace(NetworkNow);
        if (logger.IsEnabled(LogLevel.Information))
        {
            var stream = decision.Stream == PlaybackStreamKind.Video ? "video" : "audio";
            var fromCategory = DescribeStreams(from, decision.Stream);
            var rate = ((left?.BytesPerSecond ?? 0) * 8 / 1000).ToString("0", CultureInfo.InvariantCulture);
            var firstByte = (left?.FirstByteTime?.TotalMilliseconds ?? 0).ToString("0", CultureInfo.InvariantCulture);
            logger.LogInformation("Playback | cdn-switch reason={Reason} from={From} to={To} attempt={Attempt} stream={Stream} mode=relay left-kbps={LeftKbps} left-ttfb-ms={LeftFirstByteMs}",
                reason, fromCategory, next.Category, decision.Attempt, stream, rate, firstByte);
        }

        return true;
    }

    // Requests the play URLs again and hands them to the relay; the media is loaded again only
    // when the relay cannot take them.
    private async Task RefreshForFailoverAsync(MpvPlayer player, MediaSourceResolverBase resolver, PlaybackStreamSelection from,
        CdnFailoverDecision decision, string reason, long version, CancellationToken token)
    {
        if (!CanUseOnDemandNetwork) return;
        var (updated, refreshed) = await TryRefreshRelayInPlaceAsync(player, resolver, version, token);
        if (!IsCurrentPlayback(version) || !ReferenceEquals(player, Player) || !ReferenceEquals(resolver, _sourceResolver))
        {
            _failover.Abandon();
            return;
        }

        if (!updated)
        {
            ReloadForFailover(player, resolver, from, decision, reason, version, rejected: false, refreshed);
            return;
        }

        _deferredLoadError = null;
        _failover.OnSwitchedInPlace(NetworkNow);
        if (logger.IsEnabled(LogLevel.Information))
        {
            var fromCategory = DescribeStreams(from, null);
            var toCategory = DescribeStreams(resolver.CurrentStreams, null);
            logger.LogInformation("Playback | cdn-switch reason={Reason} from={From} to={To} attempt={Attempt} stream={Stream} mode=relay",
                reason, fromCategory, toCategory, decision.Attempt, "all");
        }
    }

    // Requests the play URLs again and hands the new addresses to the relay, under the gate of
    // the media so that no replay changes the source meanwhile. mpv keeps its file and its cache.
    // Updated is false when the streams are not relayed, the request failed, or the new addresses
    // do not serve the same files; Refreshed tells whether the play URLs were requested again.
    private async Task<(bool Updated, bool Refreshed)> TryRefreshRelayInPlaceAsync(MpvPlayer player, MediaSourceResolverBase resolver, long version, CancellationToken token)
    {
        if (!CanUseOnDemandNetwork) return (false, false);
        var entered = false;
        var refreshed = false;
        try
        {
            await _initializationGate.WaitAsync(token);
            entered = true;
            if (!IsCurrentPlayback(version) || !ReferenceEquals(player, Player) || !ReferenceEquals(resolver, _sourceResolver)
                || resolver.CurrentStreams is not { } streams || !IsRelayingEvery(player, streams))
            {
                return (false, false);
            }

            resolver.RefreshPlayUrl();
            var source = await resolver.GetSourceAsync();
            refreshed = true;
            return (IsCurrentPlayback(version) && await player.TryUpdateRelayAsync(source, token), true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return (false, refreshed);
        }
        catch (Exception ex)
        {
            // The message of a failed request may name an address; its type has to do.
            logger.LogWarning("Playback | relay address update failed error={Error}", ex.GetType().Name);
            return (false, refreshed);
        }
        finally
        {
            if (entered)
            {
                _initializationGate.Release();
            }
        }
    }

    // Loads the media again from the next address, or from play URLs that are requested again.
    private void ReloadForFailover(MpvPlayer player, MediaSourceResolverBase resolver, PlaybackStreamSelection from,
        CdnFailoverDecision decision, string reason, long version, bool rejected, bool refreshed)
    {
        if (!CanUseOnDemandNetwork) return;
        // A reload that did not get as far as a loaded file continues from where the first one started.
        // A media that never loaded has no position, pause or speed of its own to keep.
        (double? Position, bool Paused, double? Speed) state = _reloadState
            ?? (_hasLoadedFile ? (player.Position, _userPausedSample, player.PlaybackRate) : (null, false, null));
        _reloadState = state;
        var refresh = decision.Action == CdnFailoverAction.RefreshPlayUrl;
        _ = ObserveNetworkReloadAsync(RunWithoutPlayerInputAsync(
            async () =>
            {
                if (!IsCurrentPlayback(version) || !ReferenceEquals(player, Player) || !ReferenceEquals(resolver, _sourceResolver))
                {
                    _failover.Abandon();
                    return;
                }

                var (position, keepPaused, speed) = state;
                if (position is null)
                {
                    // A media that never loaded starts where it was meant to start, at the speed of its source.
                    position = CanUsePlaybackHistory && _historyResolver is { } history ? await history.GetStartPositionAsync() : null;
                }
                else
                {
                    // Input was drained above, so these are the position and the speed without a held key.
                    var current = await player.Client.GetCurrentPositionAsync();
                    if (current.IsSuccess && double.IsFinite(current.Value) && current.Value > 0) position = current.Value;
                    var rate = await player.Client.GetSpeedAsync();
                    if (rate.IsSuccess && double.IsFinite(rate.Value) && rate.Value > 0) speed = rate.Value;
                    if (speed is not > 0) speed = null;
                    if (!IsCurrentPlayback(version))
                    {
                        _failover.Abandon();
                        return;
                    }
                }

                if (refresh)
                {
                    if (!refreshed)
                    {
                        resolver.RefreshPlayUrl();
                    }
                }
                else if (!rejected)
                {
                    resolver.RejectCurrentStream(decision.Stream);
                }

                var issued = await ReloadForNetworkAsync(player, position, !keepPaused, speed);
                var to = resolver.CurrentStreams;
                if (!issued || to is null)
                {
                    // No source could be served, so nothing was loaded; the error display takes over.
                    _failover.Abandon();
                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation("Playback | cdn-switch failed reason={Reason} attempt={Attempt}", reason, decision.Attempt);
                    }

                    ShowDeferredLoadError();
                    return;
                }

                if (logger.IsEnabled(LogLevel.Information))
                {
                    var stream = refresh ? "all" : decision.Stream == PlaybackStreamKind.Video ? "video" : "audio";
                    var fromCategory = DescribeStreams(from, refresh ? null : decision.Stream);
                    var toCategory = DescribeStreams(to, refresh ? null : decision.Stream);
                    logger.LogInformation("Playback | cdn-switch reason={Reason} from={From} to={To} attempt={Attempt} stream={Stream}",
                        reason, fromCategory, toCategory, decision.Attempt, stream);
                }
            },
            nativeReplay: true));
    }

    // The relay left an address by itself: the record of the addresses follows, so that the next
    // decision and the logs speak of the node that is really in use.
    private void OnRelayUpstreamFailed(object? sender, MpvRelayUpstreamFailedEventArgs e)
        => PostNetworkEvent(() =>
        {
            if (!CanUseOnDemandNetwork || !ReferenceEquals(sender, Player) || Player is not { } player || _sourceResolver is not { } resolver
                || resolver.CurrentStreams is not { } from)
            {
                return;
            }

            // Every address of an answer that has expired fails alike; that is no failure of a node.
            var expiring = from.Deadline - DateTimeOffset.UtcNow < PlaybackNetworkPolicy.MinimumUrlLifetime;
            var kind = e.Kind == MpvRelayStreamKind.Video ? PlaybackStreamKind.Video : PlaybackStreamKind.Audio;
            if (!resolver.NoteRelayFailover(kind, e.Failure.Failed.Url, e.Failure.Current.Url, blame: !expiring))
            {
                return;
            }

            var attempt = Interlocked.Increment(ref _relayFailovers) + Volatile.Read(ref _networkSwitches);
            if (logger.IsEnabled(LogLevel.Information))
            {
                // The labels of the relay are the kinds of node, as everywhere in this log.
                logger.LogInformation("Playback | cdn-switch reason={Reason} from={From} to={To} attempt={Attempt} stream={Stream} mode=relay",
                    DescribeRelayFailure(e.Failure.Reason), e.Failure.Failed.Label, e.Failure.Current.Label, attempt,
                    kind == PlaybackStreamKind.Video ? "video" : "audio");
            }

            var now = Environment.TickCount64;
            if (expiring && _hasLoadedFile && !IsConnecting && IsRelayingEvery(player, from)
                && (_lastResumeRefresh == 0 || now - _lastResumeRefresh >= PlaybackNetworkPolicy.ResumeRefreshInterval.TotalMilliseconds))
            {
                // The addresses are requested again right away, instead of waiting for playback to stall.
                _lastResumeRefresh = now;
                _ = ObserveNetworkReloadAsync(OwnWork(RefreshRelayAsync(player, resolver, from, _mediaVersion, _activeMediaToken, "relay-expired", reloadOnFailure: false)));
            }
        });

    // Hands the relay play URLs that were requested again because the old ones expire.
    private async Task RefreshRelayAsync(MpvPlayer player, MediaSourceResolverBase resolver, PlaybackStreamSelection from, long version,
        CancellationToken token, string reason, bool reloadOnFailure)
    {
        if (!CanUseOnDemandNetwork) return;
        var (updated, refreshed) = await TryRefreshRelayInPlaceAsync(player, resolver, version, token);
        if (updated)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                var fromCategory = DescribeStreams(from, null);
                var toCategory = DescribeStreams(resolver.CurrentStreams, null);
                logger.LogInformation("Playback | playurl-refresh reason={Reason} from={From} to={To} mode=relay", reason, fromCategory, toCategory);
            }

            return;
        }

        if (reloadOnFailure && IsCurrentPlayback(version) && ReferenceEquals(player, Player))
        {
            // The relay could not take the new addresses: the media is loaded at the position it has reached.
            var speed = player.PlaybackRate > 0 ? player.PlaybackRate : (double?)null;
            await ReloadExpiredSourceAsync(player, resolver, from, version, player.Position, speed, refreshed);
        }
    }

    // Loads the media again at the position. The subtitle comes back with the loaded file, so a
    // node that hangs does not keep the media gate while the subtitle waits for it.
    // Returns false when no source could be served, so nothing was loaded.
    private async Task<bool> ReloadForNetworkAsync(MpvPlayer player, double? position, bool resume, double? speed)
    {
        if (!CanUseOnDemandNetwork) return false;
        ResetChapters();
        InitializeVideoChapters();
        ResetCommandDanmakuPlayback();
        _subtitleToRestore = IsSubtitleEnabled ? Subtitles.FirstOrDefault(item => item.IsSelected) : null;
        return await player.ReplayAsync(position, resume, speed);
    }

    private void RestoreSubtitleAfterReload()
    {
        if (_subtitleToRestore is not { } subtitle)
        {
            return;
        }

        _subtitleToRestore = null;
        if (CanUseSubtitles && IsSubtitleEnabled && subtitle.IsSelected && Subtitles.Any(item => ReferenceEquals(item, subtitle)))
        {
            _ = OwnWork(SelectSubtitleAsync(subtitle));
        }
    }

    // A resume with signed URLs that have expired, or are about to, would only run into errors:
    // the play URLs are requested again. A media that plays through the relay keeps its file and
    // resumes at once while the relay takes the new addresses; otherwise the media is loaded at
    // the position instead, and that load is what resumes.
    private bool TryReloadExpiredSourceOnResume(out Task reload)
    {
        reload = Task.CompletedTask;
        var player = Player;
        var resolver = _sourceResolver;
        var now = Environment.TickCount64;
        if (!CanUseOnDemandNetwork || player is null || !_hasLoadedFile || resolver?.CurrentStreams is not { Deadline: { } deadline } from
            || deadline - DateTimeOffset.UtcNow >= PlaybackNetworkPolicy.MinimumUrlLifetime
            || IsPlaybackClosing || IsConnecting
            || (_lastResumeRefresh != 0 && now - _lastResumeRefresh < PlaybackNetworkPolicy.ResumeRefreshInterval.TotalMilliseconds))
        {
            return false;
        }

        _lastResumeRefresh = now;
        var version = _mediaVersion;
        if (IsRelayingEvery(player, from))
        {
            _ = ObserveNetworkReloadAsync(OwnWork(RefreshRelayAsync(player, resolver, from, version, _activeMediaToken, "resume-expired", reloadOnFailure: true)));
            return false;
        }

        var position = player.Position;
        var speed = player.PlaybackRate > 0 ? player.PlaybackRate : (double?)null;
        reload = ObserveNetworkReloadAsync(ReloadExpiredSourceAsync(player, resolver, from, version, position, speed, refreshed: false));
        return true;
    }

    // Loads the media again at the position from play URLs that are requested again, unless they already were.
    private Task ReloadExpiredSourceAsync(MpvPlayer player, MediaSourceResolverBase resolver, PlaybackStreamSelection from, long version,
        double position, double? speed, bool refreshed)
        => !CanUseOnDemandNetwork ? Task.CompletedTask : RunWithoutPlayerInputAsync(
            async () =>
            {
                if (!IsCurrentPlayback(version) || !ReferenceEquals(player, Player) || !ReferenceEquals(resolver, _sourceResolver))
                {
                    return;
                }

                if (!refreshed)
                {
                    resolver.RefreshPlayUrl();
                }

                if (!await ReloadForNetworkAsync(player, position, resume: true, speed))
                {
                    // The request failed and nothing was loaded: resume what is there.
                    if (IsCurrentPlayback(version) && Client is { IsDisposed: false } client)
                    {
                        await client.ResumeAsync();
                    }

                    return;
                }

                if (logger.IsEnabled(LogLevel.Information))
                {
                    var fromCategory = DescribeStreams(from, null);
                    var toCategory = DescribeStreams(resolver.CurrentStreams, null);
                    logger.LogInformation("Playback | playurl-refresh reason=resume-expired from={From} to={To}", fromCategory, toCategory);
                }
            },
            nativeReplay: true);

    private async Task ObserveNetworkReloadAsync(Task reload)
    {
        try
        {
            // The reload is owned work of this view model; this only keeps its failure from going unobserved.
#pragma warning disable VSTHRD003
            await reload;
#pragma warning restore VSTHRD003
        }
        catch (Exception ex)
        {
            _failover.Abandon();
            logger.LogWarning("Playback | automatic reload failed error={Error}", ex.GetType().Name);
        }
    }

    // Errors of a load whose outcome the policy answers for are held back until that outcome is known.
    private bool TryDeferLoadError(MpvError error)
    {
        if (!CanUseOnDemandNetwork || !_failover.IsAwaitingLoad)
        {
            return false;
        }

        _deferredLoadError = error;
        return true;
    }

    private void ShowDeferredLoadError()
    {
        if (_deferredLoadError is not { } error)
        {
            return;
        }

        _deferredLoadError = null;

        // Nothing answers for the load any more, so this takes the regular path of a playback error.
        OnClientErrorOccurred(Client, error);
    }

    // Read by the stutter diagnostics on their worker thread; the snapshot is immutable.
    private NetworkDiagnostics DescribeNetworkForDiagnostics() => _networkDiagnostics;

    // Node kinds only: a host name or a URL never reaches the log.
    private static string DescribeStreams(PlaybackStreamSelection? streams, PlaybackStreamKind? kind)
        => kind switch
        {
            PlaybackStreamKind.Video => streams?.Video?.Category ?? CdnHost.NoCategory,
            PlaybackStreamKind.Audio => streams?.Audio?.Category ?? CdnHost.NoCategory,
            _ => (streams?.Video?.Category ?? CdnHost.NoCategory) + "/" + (streams?.Audio?.Category ?? CdnHost.NoCategory),
        };

    private static string DescribeFailoverReason(CdnFailoverReason reason)
        => reason switch
        {
            CdnFailoverReason.OpenFailed => "open-failed",
            CdnFailoverReason.OpenTimeout => "open-timeout",
            CdnFailoverReason.AudioMissing => "audio-missing",
            CdnFailoverReason.Stall => "stall",
            CdnFailoverReason.Rebuffer => "rebuffer",
            CdnFailoverReason.Predicted => "predicted",
            CdnFailoverReason.AudioStall => "audio-stall",
            _ => "unknown",
        };

    // The reasons that are only observed while a loaded file plays.
    private static bool IsObservedWhilePlaying(CdnFailoverReason reason)
        => reason is CdnFailoverReason.Stall or CdnFailoverReason.Rebuffer or CdnFailoverReason.Predicted or CdnFailoverReason.AudioStall;

    private static MpvRelayStreamKind ToRelayKind(PlaybackStreamKind kind)
        => kind == PlaybackStreamKind.Video ? MpvRelayStreamKind.Video : MpvRelayStreamKind.Audio;

    // Whether every stream of the loaded source is read through the relay.
    private static bool IsRelayingEvery(MpvPlayer player, PlaybackStreamSelection streams)
        => (streams.Video is not null || streams.Audio is not null)
            && (streams.Video is null || player.IsRelaying(MpvRelayStreamKind.Video))
            && (streams.Audio is null || player.IsRelaying(MpvRelayStreamKind.Audio));

    // The relay validates the certificate of a node exactly as mpv is told to: not in Debug, always in Release.
    private LoopbackMediaRelayOptions? CreateRelayOptions()
    {
        if (!CanUseOnDemandNetwork) return null;
#if DEBUG
        return new() { VerifyTls = false };
#else
        return new() { VerifyTls = true };
#endif
    }

    private static string DescribeRelayFailure(RelayFailureReason reason)
        => reason switch
        {
            RelayFailureReason.Connect => "relay-connect",
            RelayFailureReason.Status => "relay-status",
            RelayFailureReason.Unsupported => "relay-unsupported",
            RelayFailureReason.Length => "relay-length",
            RelayFailureReason.Read => "relay-read",
            RelayFailureReason.Timeout => "relay-timeout",
            _ => "relay-unknown",
        };

    /// <summary>
    /// What the stutter diagnostics log about the network; none of it identifies a host or a URL.
    /// </summary>
    private sealed record NetworkDiagnostics(
        string VideoCdn,
        string AudioCdn,
        int? Quality,
        int VideoBandwidth,
        int AudioBandwidth,
        int MainBandwidth,
        long RawInputRate,
        long ForwardBytes,
        double? DownloadRate,
        double? VideoCacheSeconds,
        double? AudioCacheSeconds,
        int Switches)
    {
        public static NetworkDiagnostics Empty { get; } = new(CdnHost.NoCategory, CdnHost.NoCategory, null, 0, 0, 0, 0, 0, null, null, null, 0);
    }

    // What the sampler reads from mpv on its own thread.
    private readonly record struct NetworkSample(
        bool UserPaused,
        bool PausedForCache,
        bool Seeking,
        bool CacheIdle,
        double CacheSeconds,
        double DownloadRate,
        double Position,
        double? AudioPosition);

    /// <summary>
    /// Reads the cache state of mpv once per <see cref="PlaybackNetworkPolicy.SampleInterval"/>,
    /// off the UI thread.
    /// </summary>
    private sealed class NetworkSampler : IAsyncDisposable
    {
        private readonly MpvClient _client;
        private readonly ILogger _logger;
        private readonly Action<NetworkSample> _publish;
        private readonly PeriodicTimer _timer = new(PlaybackNetworkPolicy.SampleInterval);
        private readonly Task _worker;
        private volatile bool _stopped;

        public NetworkSampler(MpvClient client, ILogger samplerLogger, Action<NetworkSample> publish)
        {
            _client = client;
            _logger = samplerLogger;
            _publish = publish;
            _worker = Task.Run(SampleAsync);
        }

        public async ValueTask DisposeAsync()
        {
            _stopped = true;
            _timer.Dispose();

            // This sampler owns the worker; native teardown must join it, not just stop the timer.
#pragma warning disable VSTHRD003
            await _worker.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }

        private async Task SampleAsync()
        {
            try
            {
                while (await _timer.WaitForNextTickAsync().ConfigureAwait(false))
                {
                    if (_stopped || _client.IsDisposed)
                    {
                        return;
                    }

                    var sample = new NetworkSample(
                        ReadFlag("pause"),
                        ReadFlag("paused-for-cache"),
                        ReadFlag("seeking"),
                        ReadFlag("demuxer-cache-idle"),
                        ReadDouble("demuxer-cache-duration") ?? 0,
                        _client.GetProperty("cache-speed", out long speed) == MpvError.Success ? speed : 0,
                        ReadDouble("time-pos") ?? 0,
                        ReadDouble("audio-pts"));
                    if (_stopped)
                    {
                        return;
                    }

                    _publish(sample);
                }
            }
            catch (Exception ex)
            {
                // Without samples nothing is switched; playback itself is not affected.
                _logger.LogWarning("Playback | network sampling stopped error={Error}", ex.GetType().Name);
            }
        }

        private bool ReadFlag(string property)
            => _client.GetProperty(property, out bool value) == MpvError.Success && value;

        private double? ReadDouble(string property)
            => _client.GetProperty(property, out double value) == MpvError.Success && double.IsFinite(value) ? value : null;
    }
}

/// <summary>
/// The download rate that could not keep up with playback, and what playback needed.
/// </summary>
internal sealed class PlaybackBandwidthLimitedEventArgs(int quality, double downloadKbps, double requiredKbps) : EventArgs
{
    /// <summary>
    /// The id of the video quality that was playing.
    /// </summary>
    public int Quality { get; } = quality;

    /// <summary>
    /// The download rate of the main file, in kilobits per second.
    /// </summary>
    public double DownloadKbps { get; } = downloadKbps;

    /// <summary>
    /// The rate playback takes data from the main file at, in kilobits per second.
    /// </summary>
    public double RequiredKbps { get; } = requiredKbps;
}
