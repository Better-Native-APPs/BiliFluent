// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Models.Media;
using Richasy.MpvKernel.Player;
using Richasy.MpvKernel.Player.Models;
using Richasy.MpvKernel.Relay;

namespace BiliCopilot.UI.Resolvers;

internal abstract class MediaSourceResolverBase : IMpvMediaSourceResolver
{
    protected readonly object InitializationSync = new();

    // Addresses that were already used for the current media, so a reload moves on to the next one.
    private readonly HashSet<string> _triedStreamUrls = new(StringComparer.Ordinal);
    private CancellationToken _initializationCancellation;
    private long _initializationGeneration;
    private PlaybackStreamSelection? _streams;
    private OnDemandPlaybackSelection? _playingSelection;
    private OnDemandPlaybackSelection? _loadingSelection;
    private OnDemandSelectionRequest? _reloadSelection;
    private PlaybackStreamSelection? _beforeLoadStreams;
    private IReadOnlyList<StreamCandidate> _beforeLoadVideos = [];
    private IReadOnlyList<StreamCandidate> _beforeLoadAudios = [];

    // Every address of the two streams of the source that was served last, in the order they are tried.
    private IReadOnlyList<StreamCandidate> _videoCandidates = [];
    private IReadOnlyList<StreamCandidate> _audioCandidates = [];

    // The deadline of a fresh answer already looked expired: the local clock cannot be trusted with it.
    private bool _ignoreDeadline;

    public IntPtr WindowHandle { get; set; }

    /// <summary>
    /// The addresses of the source that was served last; null until one is served for the
    /// current play detail. Safe to read on any thread.
    /// </summary>
    internal PlaybackStreamSelection? CurrentStreams => Volatile.Read(ref _streams);

    internal OnDemandPlaybackSelection? PlayingSelection => Volatile.Read(ref _playingSelection);

    internal virtual Task<OnDemandPlaybackSelection?> DescribeSelectionAsync(OnDemandSelectionRequest request)
        => Task.FromResult<OnDemandPlaybackSelection?>(null);

    internal void SetReloadSelection(OnDemandSelectionRequest request)
    {
        lock (InitializationSync) _reloadSelection = request;
    }

    protected OnDemandSelectionRequest GetLoadSelection(OnDemandSelectionPreferences initialPreferences)
        => _reloadSelection ?? _playingSelection?.ForReload() ?? new(initialPreferences);

    protected void ResetPlayingSelection()
    {
        _playingSelection = null;
        _loadingSelection = null;
        _reloadSelection = null;
        _beforeLoadStreams = null;
        _beforeLoadVideos = [];
        _beforeLoadAudios = [];
    }

    protected OnDemandPlaybackSelection DescribeSelection(PlayerFormatInformation format, AudioSelections audioSelections,
        OnDemandSelectionPreferences preferences, DashSegmentInformation? video, DashSegmentInformation? audio,
        AudioQualityKind audioKind, IReadOnlyDictionary<string, string> headers)
    {
        var now = DateTimeOffset.UtcNow;
        var excludeP2P = SettingsToolkit.ReadLocalSetting(SettingNames.PlayWithoutP2P, false);
        SeamlessStreamDescription? Describe(DashSegmentInformation? segment, PlayerFormatInformation? videoFormat, AudioQualityKind kind, string? title)
            => SeamlessStreamDescription.Create(segment, videoFormat, kind, title, headers, excludeP2P, AppToolkit.IsP2PUrl,
                CdnHostHealth.Session, now, _triedStreamUrls.Contains);
        return new(format, audioSelections, preferences,
            Describe(video, format, AudioQualityKind.Standard, format.Description),
            Describe(audio, null, audioKind, audioSelections.QualityId));
    }

    protected (string? Video, string? Audio) StageSelection(OnDemandPlaybackSelection selection)
    {
        _beforeLoadStreams = _streams;
        _beforeLoadVideos = _videoCandidates;
        _beforeLoadAudios = _audioCandidates;
        _loadingSelection = selection;
        SetSelectionStreams(selection);
        return (_streams?.Video?.Url, _streams?.Audio?.Url);
    }

    internal bool CompleteSelectionLoad()
    {
        lock (InitializationSync)
        {
            if (_loadingSelection is not { } selection) return false;
            Volatile.Write(ref _playingSelection, selection);
            _loadingSelection = null;
            _reloadSelection = null;
            _beforeLoadStreams = null;
            _beforeLoadVideos = [];
            _beforeLoadAudios = [];
            return true;
        }
    }

    internal void AbandonSelectionLoad()
    {
        lock (InitializationSync)
        {
            _reloadSelection = null;
            if (_loadingSelection is null) return;
            _loadingSelection = null;
            Volatile.Write(ref _streams, _beforeLoadStreams);
            _videoCandidates = _beforeLoadVideos;
            _audioCandidates = _beforeLoadAudios;
            _beforeLoadStreams = null;
            _beforeLoadVideos = [];
            _beforeLoadAudios = [];
        }
    }

    internal void CommitSelectedStream(PlaybackStreamKind kind, OnDemandPlaybackSelection selection, RelayStreamStatistics? statistics)
    {
        lock (InitializationSync)
        {
            var current = _playingSelection;
            if (current is null) return;
            var next = kind == PlaybackStreamKind.Video
                ? selection with { Video = selection.Video?.WithCurrentUrl(statistics?.Current.Url) }
                : selection with { Audio = selection.Audio?.WithCurrentUrl(statistics?.Current.Url) };
            var committed = current.WithStream(kind, next);
            Volatile.Write(ref _playingSelection, committed);
            SetSelectionStreams(committed);
        }
    }

    internal void CommitSelectionPreferences(OnDemandSelectionPreferences preferences)
    {
        lock (InitializationSync)
        {
            if (_playingSelection is { } selection) Volatile.Write(ref _playingSelection, selection with { Preferences = preferences });
        }
    }

    private void SetSelectionStreams(OnDemandPlaybackSelection selection)
    {
        _videoCandidates = selection.Video?.Candidates ?? [];
        _audioCandidates = selection.Audio?.Candidates ?? [];
        var video = selection.Video?.CurrentCandidate;
        var audio = selection.Audio?.CurrentCandidate;
        var now = DateTimeOffset.UtcNow;
        var videoDeadline = CdnHost.GetDeadline(video?.Url);
        var audioDeadline = CdnHost.GetDeadline(audio?.Url);
        var deadline = videoDeadline is null || audioDeadline < videoDeadline ? audioDeadline : videoDeadline;
        if (_streams is null && deadline - now < PlaybackNetworkPolicy.MinimumUrlLifetime) _ignoreDeadline = true;
        Volatile.Write(ref _streams, new(video, audio, CountUntried(_videoCandidates, video), CountUntried(_audioCandidates, audio),
            selection.Format.Quality, selection.Video?.Segment.Bandwidth ?? 0, selection.Audio?.Segment.Bandwidth ?? 0,
            _ignoreDeadline ? null : deadline));
    }

    /// <summary>
    /// Set before initializing/replacing media. The caller owns, cancels and drains its CTS.
    /// This resolver never disposes the caller's cancellation source.
    /// </summary>
    public void SetInitializationCancellation(CancellationToken cancellationToken)
    {
        lock (InitializationSync)
        {
            _initializationCancellation = cancellationToken;
            InvalidateInitialization();
        }
    }

    protected void InvalidateInitialization(bool keepPlayingStreams = false)
    {
        // Called while holding InitializationSync, together with the derived media reset.
        _initializationGeneration++;
        _triedStreamUrls.Clear();
        _ignoreDeadline = false;
        if (!keepPlayingStreams)
        {
            _videoCandidates = [];
            _audioCandidates = [];
            Volatile.Write(ref _streams, null);
        }
        ResetInitialization();
    }

    /// <summary>
    /// Gives up the address the given stream is read from: the next source uses another one, and
    /// its host is ordered last for a while in every player of this app session.
    /// </summary>
    internal void RejectCurrentStream(PlaybackStreamKind kind)
    {
        lock (InitializationSync)
        {
            var candidate = kind == PlaybackStreamKind.Video ? _streams?.Video : _streams?.Audio;
            if (candidate is null)
            {
                return;
            }

            _triedStreamUrls.Add(candidate.Url);
            CdnHostHealth.Session.ReportFailure(candidate.HostKey, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Gives up the address the given stream is read from, as <see cref="RejectCurrentStream"/> does,
    /// and makes the next address that was not tried yet the current one, without serving a new
    /// source: for a stream that the relay moves to that address while the media keeps playing.
    /// Null when no untried address is left; the current one is given up all the same.
    /// </summary>
    internal StreamCandidate? MoveToNextStream(PlaybackStreamKind kind)
    {
        lock (InitializationSync)
        {
            var candidate = kind == PlaybackStreamKind.Video ? _streams?.Video : _streams?.Audio;
            if (candidate is null)
            {
                return null;
            }

            _triedStreamUrls.Add(candidate.Url);
            CdnHostHealth.Session.ReportFailure(candidate.HostKey, DateTimeOffset.UtcNow);
            var next = GetCandidates(kind).FirstOrDefault(item => !_triedStreamUrls.Contains(item.Url));
            if (next is not null)
            {
                SetCurrentStream(kind, next);
            }

            return next;
        }
    }

    /// <summary>
    /// Records that the relay gave up an address by itself and reads the stream from another one.
    /// False when the addresses are not those of the source that was served last.
    /// </summary>
    /// <param name="kind">The stream.</param>
    /// <param name="failedUrl">The address that failed.</param>
    /// <param name="currentUrl">The address the relay reads from now.</param>
    /// <param name="blame">
    /// Whether the node is at fault: the address is not picked again for this media, and its host
    /// is ordered last for a while. Not so for URLs that have expired, which fail on every node.
    /// </param>
    internal bool NoteRelayFailover(PlaybackStreamKind kind, string failedUrl, string currentUrl, bool blame)
    {
        lock (InitializationSync)
        {
            var candidates = GetCandidates(kind);
            var failed = candidates.FirstOrDefault(item => item.Url == failedUrl);
            var current = candidates.FirstOrDefault(item => item.Url == currentUrl);
            if (failed is null || current is null || Volatile.Read(ref _streams) is null)
            {
                return false;
            }

            if (blame)
            {
                _triedStreamUrls.Add(failed.Url);
                CdnHostHealth.Session.ReportFailure(failed.HostKey, DateTimeOffset.UtcNow);
            }

            SetCurrentStream(kind, current);
            return true;
        }
    }

    /// <summary>
    /// Drops the loaded play detail, so the next source requests the play URLs again.
    /// The media, the quality and the audio choices stay as they are.
    /// </summary>
    internal void RefreshPlayUrl()
    {
        lock (InitializationSync)
        {
            InvalidateInitialization(keepPlayingStreams: true);
        }
    }

    /// <summary>
    /// Picks the address of each stream, only among the signed URLs of the API, and records the
    /// choice. Call while holding <see cref="InitializationSync"/>.
    /// </summary>
    protected (string? Video, string? Audio) SelectStreams(DashSegmentInformation? video, DashSegmentInformation? audio, int quality)
    {
        var now = DateTimeOffset.UtcNow;
        var excludeP2P = SettingsToolkit.ReadLocalSetting(SettingNames.PlayWithoutP2P, false);
        var videos = StreamCandidates.Build(video?.BaseUrl, video?.BackupUrls, excludeP2P, AppToolkit.IsP2PUrl, CdnHostHealth.Session, now);
        var audios = StreamCandidates.Build(audio?.BaseUrl, audio?.BackupUrls, excludeP2P, AppToolkit.IsP2PUrl, CdnHostHealth.Session, now);
        var videoCandidate = Pick(videos);
        var audioCandidate = Pick(audios);
        var videoDeadline = CdnHost.GetDeadline(videoCandidate?.Url);
        var audioDeadline = CdnHost.GetDeadline(audioCandidate?.Url);
        var deadline = videoDeadline is null || audioDeadline < videoDeadline ? audioDeadline : videoDeadline;
        if (Volatile.Read(ref _streams) is null && deadline - now < PlaybackNetworkPolicy.MinimumUrlLifetime)
        {
            _ignoreDeadline = true;
        }

        deadline = _ignoreDeadline ? null : deadline;
        _videoCandidates = videos;
        _audioCandidates = audios;
        Volatile.Write(ref _streams, new(
            videoCandidate,
            audioCandidate,
            CountUntried(videos, videoCandidate),
            CountUntried(audios, audioCandidate),
            quality,
            video?.Bandwidth ?? 0,
            audio?.Bandwidth ?? 0,
            deadline));
        return (videoCandidate?.Url, audioCandidate?.Url);

        // With every address tried, the first one is used again rather than none.
        StreamCandidate? Pick(IReadOnlyList<StreamCandidate> candidates)
            => candidates.FirstOrDefault(candidate => !_triedStreamUrls.Contains(candidate.Url)) ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// Describes the streams <see cref="SelectStreams"/> picked for the relay of the player: every
    /// address of each stream, starting with the picked one, and the headers the player would
    /// send if it read them itself. Null when the relay cannot read one of the addresses; the
    /// source is then played directly. Call while holding <see cref="InitializationSync"/>.
    /// </summary>
    protected MpvRelaySource? CreateRelaySource(string userAgent, IReadOnlyDictionary<string, string> headers, ILogger logger)
    {
        if (Volatile.Read(ref _streams) is not { } streams || (streams.Video is null && streams.Audio is null))
        {
            return null;
        }

        var video = Describe(_videoCandidates, streams.Video);
        var audio = Describe(_audioCandidates, streams.Audio);
        if ((streams.Video is not null && video is null) || (streams.Audio is not null && audio is null))
        {
            logger.LogInformation("Playback | relay off for this source reason=unsupported-address");
            return null;
        }

        var upstreamHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase) { ["User-Agent"] = userAgent };
        return new() { Video = video, Audio = audio, Headers = upstreamHeaders };

        static MpvRelayAddresses? Describe(IReadOnlyList<StreamCandidate> candidates, StreamCandidate? current)
        {
            var upstreams = new List<RelayUpstream>(candidates.Count);
            var index = -1;
            foreach (var candidate in candidates)
            {
                if (!RelayUpstream.IsSupported(candidate.Url))
                {
                    return null;
                }

                if (ReferenceEquals(candidate, current))
                {
                    index = upstreams.Count;
                }

                // The label is the kind of node: it is all the relay may log about an address.
                upstreams.Add(new(candidate.Url, candidate.Category));
            }

            return index < 0 ? null : new(upstreams, index);
        }
    }

    private IReadOnlyList<StreamCandidate> GetCandidates(PlaybackStreamKind kind)
        => kind == PlaybackStreamKind.Video ? _videoCandidates : _audioCandidates;

    private int CountUntried(IReadOnlyList<StreamCandidate> candidates, StreamCandidate? current)
        => candidates.Count(candidate => !ReferenceEquals(candidate, current) && !_triedStreamUrls.Contains(candidate.Url));

    // Makes another address of a stream the one in use. Call while holding InitializationSync.
    private void SetCurrentStream(PlaybackStreamKind kind, StreamCandidate candidate)
    {
        if (Volatile.Read(ref _streams) is not { } streams)
        {
            return;
        }

        var video = kind == PlaybackStreamKind.Video ? candidate : streams.Video;
        var audio = kind == PlaybackStreamKind.Audio ? candidate : streams.Audio;
        Volatile.Write(ref _streams, streams with
        {
            Video = video,
            Audio = audio,
            VideoRemaining = CountUntried(_videoCandidates, video),
            AudioRemaining = CountUntried(_audioCandidates, audio),
        });
        if (_playingSelection is { } selection)
        {
            Volatile.Write(ref _playingSelection, kind == PlaybackStreamKind.Video
                ? selection with { Video = selection.Video?.WithCurrentUrl(candidate.Url) }
                : selection with { Audio = selection.Audio?.WithCurrentUrl(candidate.Url) });
        }
    }

    protected abstract void ResetInitialization();

    protected (long Generation, CancellationToken CancellationToken) CaptureInitialization()
    {
        lock (InitializationSync)
        {
            _initializationCancellation.ThrowIfCancellationRequested();
            return (_initializationGeneration, _initializationCancellation);
        }
    }

    protected void ThrowIfInitializationInvalid((long Generation, CancellationToken CancellationToken) request)
    {
        // Check and commit under the same InitializationSync lock.
        request.CancellationToken.ThrowIfCancellationRequested();
        if (request.Generation != _initializationGeneration)
        {
            throw new OperationCanceledException("The media source request was superseded.", request.CancellationToken);
        }
    }

    public abstract Task<MpvMediaSource> GetSourceAsync();

    public abstract IMpvMediaSourceResolver Clone();
}
