// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using Richasy.BiliKernel.Models.Media;
using Richasy.MpvKernel.Player;
using Richasy.MpvKernel.Player.Models;

namespace BiliCopilot.UI.Resolvers;

internal abstract class MediaSourceResolverBase : IMpvMediaSourceResolver
{
    protected readonly object InitializationSync = new();

    // Addresses that were already used for the current media, so a reload moves on to the next one.
    private readonly HashSet<string> _triedStreamUrls = new(StringComparer.Ordinal);
    private CancellationToken _initializationCancellation;
    private long _initializationGeneration;
    private PlaybackStreamSelection? _streams;

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

        int CountUntried(IReadOnlyList<StreamCandidate> candidates, StreamCandidate? current)
            => candidates.Count(candidate => !ReferenceEquals(candidate, current) && !_triedStreamUrls.Contains(candidate.Url));
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
