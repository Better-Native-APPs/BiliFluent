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

    protected void InvalidateInitialization()
    {
        // Called while holding InitializationSync, together with the derived media reset.
        _initializationGeneration++;
        _triedStreamUrls.Clear();
        _ignoreDeadline = false;
        _videoCandidates = [];
        _audioCandidates = [];
        Volatile.Write(ref _streams, null);
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
            InvalidateInitialization();
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
