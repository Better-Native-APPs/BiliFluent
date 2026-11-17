// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Extensions;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Media;
using Richasy.MpvKernel.Core.Models;
using Richasy.MpvKernel.Player;
using Richasy.MpvKernel.Player.Models;

namespace BiliCopilot.UI.Resolvers;

internal sealed partial class PgcMediaSourceResolver(IPlayerService playerService, IBiliCookiesResolver cookiesResolver, ILogger<PgcMediaSourceResolver> logger) : MediaSourceResolverBase, IAdaptiveQualityResolver
{
    private MediaSnapshot _snapshot;
    private EpisodeInformation _episode;
    private Task? _initTask;
    private DashMediaInformation? _playbackInfo;
    private AdaptiveQualityDecision? _adaptiveStartDecision;

    public void Initialize(MediaSnapshot snapshot, EpisodeInformation episode)
    {
        lock (InitializationSync)
        {
            InvalidateInitialization();
            ResetPlayingSelection();
            _adaptiveStartDecision = null;
            _snapshot = snapshot;
            _episode = episode;
        }
    }

    protected override void ResetInitialization()
    {
        _playbackInfo = default;
        _initTask = default;
    }

    internal void ResetSnapshot(MediaSnapshot snapshot)
    {
        lock (InitializationSync)
        {
            _snapshot = snapshot;
        }
    }

    /// <summary>
    /// The play detail of an episode names no audio language, so the original audio is the only one
    /// and the choice is among its qualities.
    /// </summary>
    internal AudioSelections GetAudioSelections()
    {
        lock (InitializationSync)
        {
            return PlayingSelection?.AudioSelections ?? GetResolvedAudioSelections(new(GetPreferences()));
        }
    }

    private AudioSelections GetResolvedAudioSelections(OnDemandSelectionRequest request)
        => _playbackInfo is { } info
            ? new([new(AudioLanguageInformation.OriginalId)], AudioLanguageInformation.OriginalId,
                info.AudioQualities is { } qualities ? [.. qualities] : [], GetSelectedAudioQuality(request)?.Id)
            : AudioSelections.Empty;

    /// <summary>
    /// The quality that plays: the one chosen by hand when there is one, otherwise the automatic choice.
    /// Null until the media is loaded.
    /// </summary>
    internal PlayerFormatInformation? GetCurrentFormat()
        => PlayingSelection?.Format ?? ResolveFormat(VideoMediaSourceResolver.GetManualQuality(_snapshot));

    private PlayerFormatInformation? ResolveFormat(int? preferQuality)
        => VideoQualityPreference.Pick(GetPlayableFormats(), preferQuality ?? 0,
            SettingsToolkit.ReadLocalSetting(SettingNames.PreferQuality, PreferQualityType.Auto));

    private IEnumerable<PlayerFormatInformation> GetPlayableFormats()
    {
        if (_playbackInfo?.Formats is not { } formats)
        {
            return [];
        }

        var availableFormats = formats.ToList().ConvertAll(p => new SourceItemViewModel(p, default)).Where(p => p.IsEnabled).ToList();
        return VideoMediaSourceResolver.KeepFormatsWithStream(availableFormats.Select(p => p.Data), _playbackInfo.Videos);
    }

    IReadOnlyList<AdaptiveQualityCandidate> IAdaptiveQualityResolver.GetAdaptiveCandidates()
    {
        lock (InitializationSync) return AdaptiveQualitySelection.BuildCandidates(GetPlayableFormats(), GetVideoSegment);
    }

    AdaptiveQualityDecision? IAdaptiveQualityResolver.AdaptiveStartDecision => _adaptiveStartDecision;

    /// <summary>
    /// Whether no quality is chosen by hand, so that <see cref="GetCurrentFormat"/> is the automatic choice.
    /// </summary>
    internal bool IsAutoQuality() => PlayingSelection is { } playing ? playing.Preferences.VideoQuality is not > 0 : VideoMediaSourceResolver.GetManualQuality(_snapshot) <= 0;

    public override IMpvMediaSourceResolver Clone() => throw new NotImplementedException();

    public override async Task<MpvMediaSource> GetSourceAsync()
    {
        var request = CaptureInitialization();
        var volume = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.PlayerVolume, 100d);
        var speed = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.PlayerSpeed, 1d);
        var cookies = cookiesResolver.GetCookieString();
        const string referer = InternalHttpExtensions.VideoReferer;
        const string userAgent = InternalHttpExtensions.VideoUserAgent;
        var headers = new Dictionary<string, string>
        {
            { "Cookie", cookies },
            { "Referer", referer },
        };

        var options = new MpvPlayOptions
        {
            WindowHandle = WindowHandle,
            InitialVolume = volume,
            InitialSpeed = speed,
            UserAgent = userAgent,
            HttpHeaders = headers,
            EnableCookies = true,
            EnableYtdl = false,
        };

        await LoadMediaInfoAsync(request);
        lock (InitializationSync)
        {
            ThrowIfInitializationInvalid(request);
            var id = _snapshot.Episode?.Identifier.Id ?? _snapshot.Season?.Identifier.Id;
            var title = _snapshot.Episode?.Identifier.Title ?? _snapshot.Season?.Identifier.Title;
            options.MediaName = title;
            var upstreamHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase) { ["User-Agent"] = userAgent };
            var selection = CreateSelection(GetLoadSelection(GetPreferences()), upstreamHeaders);
            var (videoUrl, audioUrl) = StageSelection(selection);
            var fileUrl = string.Empty;
            if (!string.IsNullOrEmpty(videoUrl))
            {
                fileUrl = videoUrl;
                if (!string.IsNullOrEmpty(audioUrl))
                {
                    options.AudioTracks ??= [];
                    options.AudioTracks.Add(audioUrl);
                }
            }
            else if (!string.IsNullOrEmpty(audioUrl))
            {
                fileUrl = audioUrl;
            }

            return new MpvMediaSource(fileUrl, id, title, options)
            {
                // The player reads the streams through its relay, so a node can change while the media plays.
                Relay = fileUrl.Length > 0 ? CreateRelaySource(userAgent, headers, logger) : null,
            };
        }
    }

    public async Task<List<PlayerFormatInformation>?> GetSourcesAsync()
    {
        var request = CaptureInitialization();
        await LoadMediaInfoAsync(request);
        lock (InitializationSync)
        {
            ThrowIfInitializationInvalid(request);
            return [.. _playbackInfo!.Formats];
        }
    }

    internal Task LoadMediaInfoAsync() => LoadMediaInfoAsync(CaptureInitialization());

    private async Task LoadMediaInfoAsync((long Generation, CancellationToken CancellationToken) request)
    {
        Task loadTask;
        lock (InitializationSync)
        {
            ThrowIfInitializationInvalid(request);
            if (_snapshot is null || _episode is null)
            {
                throw new InvalidOperationException("必须先初始化媒体信息.");
            }

            if (_initTask is null || _initTask.IsFaulted || _initTask.IsCanceled)
            {
                // A failed request is not kept: the next source asks again, as a reload does.
                _initTask = LoadMediaInfoCoreAsync(_episode, request);
            }

            loadTask = _initTask;
        }

        await loadTask;
        lock (InitializationSync)
        {
            ThrowIfInitializationInvalid(request);
        }
    }

    private async Task LoadMediaInfoCoreAsync(EpisodeInformation episode, (long Generation, CancellationToken CancellationToken) request)
    {
        try
        {
            var cid = episode.GetExtensionIfNotNull<long>(EpisodeExtensionDataId.Cid);
            var seasonType = episode.GetExtensionIfNotNull<string>(EpisodeExtensionDataId.SeasonType);
            var info = await playerService.GetPgcPlayDetailAsync(cid.ToString(), episode.Identifier.Id, seasonType, request.CancellationToken);
            lock (InitializationSync)
            {
                ThrowIfInitializationInvalid(request);
                _playbackInfo = info;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (InitializationSync)
            {
                ThrowIfInitializationInvalid(request);
            }

            logger.LogError("Playback | play detail request failed kind=pgc error={Error}", ex.GetType().Name);
            throw;
        }
    }

    internal override async Task<OnDemandPlaybackSelection?> DescribeSelectionAsync(OnDemandSelectionRequest selectionRequest)
    {
        var request = CaptureInitialization();
        await LoadMediaInfoAsync(request);
        lock (InitializationSync)
        {
            ThrowIfInitializationInvalid(request);
            return CreateSelection(selectionRequest, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cookie"] = cookiesResolver.GetCookieString(),
                ["Referer"] = InternalHttpExtensions.VideoReferer,
                ["User-Agent"] = InternalHttpExtensions.VideoUserAgent,
            });
        }
    }

    private OnDemandPlaybackSelection CreateSelection(OnDemandSelectionRequest request, IReadOnlyDictionary<string, string> headers)
    {
        var audioSegments = _playbackInfo.Audios;
        var maxAudioQuality = audioSegments?.Max(p => Convert.ToInt32(p.Id));
        // Automatic audio keeps its existing ordering and never participates in quality adaptation.
        var audioQuality = GetSelectedAudioQuality(request);
        var aSeg = audioQuality?.Segment
            ?? audioSegments?.FirstOrDefault(p => p.Id == maxAudioQuality.ToString());
        var selectedFormat = ResolveFormat(request.ResolvedVideoQuality ?? request.Preferences.VideoQuality);
        if (AdaptiveQualitySelection.IsEnabled(request, SettingsToolkit.ReadLocalSetting(SettingNames.PreferQuality, PreferQualityType.Auto)))
        {
            var formats = GetPlayableFormats().ToList();
            var candidates = AdaptiveQualitySelection.BuildCandidates(formats, GetVideoSegment);
            var policy = new AdaptiveQualityPolicy(AdaptiveBandwidthHistory.Session);
            var decision = policy.SelectStart(TimeSpan.FromMilliseconds(Environment.TickCount64), candidates, aSeg?.Bandwidth ?? 0,
                SettingsToolkit.ReadLocalSetting(SettingNames.PlayerSpeed, 1d));
            _adaptiveStartDecision = decision with { FromQuality = PlayingSelection?.Format.Quality ?? 0 };
            selectedFormat = AdaptiveQualitySelection.PickStartFormat(formats, decision);
        }

        if (selectedFormat is null)
        {
            throw new InvalidCastException("无法获取到有效的播放地址");
        }

        var vSeg = GetVideoSegment(selectedFormat.Quality);
        return DescribeSelection(selectedFormat, GetResolvedAudioSelections(request), request.Preferences,
            vSeg, aSeg, audioQuality?.Kind ?? AudioQualityKind.Standard, headers);
    }

    private DashSegmentInformation? GetVideoSegment(int selectedQuality)
    {
        var videoSegments = _playbackInfo?.Videos;
        var preferCodec = AppToolkit.GetPreferCodecId();
        var quality = selectedQuality.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return videoSegments?.FirstOrDefault(p => p.Id == quality && p.Codecs.Contains(preferCodec, StringComparison.Ordinal))
            ?? videoSegments?.FirstOrDefault(p => p.Id == quality);
    }

    private OnDemandSelectionPreferences GetPreferences()
        => new(VideoMediaSourceResolver.GetManualQuality(_snapshot), _snapshot.PreferAudioQualityId, _snapshot.PreferAudioLanguageId,
            _snapshot.IsAutomaticAudioStandardOnly);

    private AudioQualityInformation? GetSelectedAudioQuality(OnDemandSelectionRequest request)
        => AudioSelections.PickQuality(_playbackInfo?.AudioQualities, request.ResolvedAudioQualityId ?? request.Preferences.AudioQualityId,
            request.Preferences.StandardAudioOnly);
}
