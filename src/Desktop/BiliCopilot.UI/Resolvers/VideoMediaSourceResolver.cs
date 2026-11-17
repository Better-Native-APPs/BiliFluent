// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Extensions;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Media;
using Richasy.MpvKernel.Core.Models;
using Richasy.MpvKernel.Player;
using Richasy.MpvKernel.Player.Models;

namespace BiliCopilot.UI.Resolvers;

internal sealed partial class VideoMediaSourceResolver(IPlayerService playerService, IBiliCookiesResolver cookiesResolver, ILogger<VideoMediaSourceResolver> logger) : MediaSourceResolverBase, IAdaptiveQualityResolver
{
    private MediaSnapshot _snapshot;
    private VideoPart _part;
    private Task? _initTask;
    private DashMediaInformation? _playbackInfo;
    private AdaptiveQualityDecision? _adaptiveStartDecision;

    // The dubbing language the loaded play detail actually serves; null is the original audio.
    private string? _servedLanguage;

    public void Initialize(MediaSnapshot snapshot, VideoPart part)
    {
        lock (InitializationSync)
        {
            InvalidateInitialization();
            ResetPlayingSelection();
            _adaptiveStartDecision = null;
            _snapshot = snapshot;
            _part = part;
        }
    }

    protected override void ResetInitialization()
    {
        _playbackInfo = default;
        _initTask = default;
        _servedLanguage = default;
    }

    internal void ResetSnapshot(MediaSnapshot snapshot)
    {
        lock (InitializationSync)
        {
            _snapshot = snapshot;
        }
    }

    /// <summary>
    /// Takes over the audio choices of <paramref name="snapshot"/>. Another language needs a new play detail
    /// request, so the loaded one is dropped; another quality of the same language is already loaded.
    /// </summary>
    internal void ResetAudioSelection(MediaSnapshot snapshot, bool reloadLanguage)
    {
        lock (InitializationSync)
        {
            _snapshot = snapshot;
            if (_initTask is null)
            {
                return;
            }

            // A failed request is dropped as well, so choosing again retries it.
            if (_initTask.IsFaulted || _initTask.IsCanceled || reloadLanguage)
            {
                InvalidateInitialization(keepPlayingStreams: true);
            }
        }
    }

    internal AudioSelections GetAudioSelections()
    {
        lock (InitializationSync)
        {
            return PlayingSelection?.AudioSelections ?? GetResolvedAudioSelections(new(GetPreferences()));
        }
    }

    private AudioSelections GetResolvedAudioSelections(OnDemandSelectionRequest request)
        => _playbackInfo is { } info
            ? new(info.AudioLanguages is { } languages ? [.. languages] : [],
                _servedLanguage ?? AudioLanguageInformation.OriginalId,
                info.AudioQualities is { } qualities ? [.. qualities] : [],
                GetSelectedAudioQuality(request)?.Id, info.AudioLanguageGroupTitle)
            : AudioSelections.Empty;

    /// <summary>
    /// The quality that plays: the one chosen by hand when there is one, otherwise the automatic choice.
    /// Null until the media is loaded.
    /// </summary>
    internal PlayerFormatInformation? GetCurrentFormat()
        => PlayingSelection?.Format ?? ResolveFormat(GetManualQuality(_snapshot));

    private PlayerFormatInformation? ResolveFormat(int? preferQuality)
        => VideoQualityPreference.Pick(GetPlayableFormats(), preferQuality ?? 0,
            SettingsToolkit.ReadLocalSetting(SettingNames.PreferQuality, PreferQualityType.Auto));

    private IEnumerable<PlayerFormatInformation> GetPlayableFormats()
    {
        if (_playbackInfo?.Formats is not { } formats)
        {
            return [];
        }

        var availableFormats = formats.ToList().ConvertAll(p => new SourceItemViewModel(p, default));
        if (_snapshot.Video.Publisher?.User?.Id != this.Get<AccountViewModel>().MyProfile.User.Id)
        {
            availableFormats = [.. availableFormats.Where(p => p.IsEnabled)];
        }

        return KeepFormatsWithStream(availableFormats.Select(p => p.Data), _playbackInfo.Videos);
    }

    IReadOnlyList<AdaptiveQualityCandidate> IAdaptiveQualityResolver.GetAdaptiveCandidates()
    {
        lock (InitializationSync) return AdaptiveQualitySelection.BuildCandidates(GetPlayableFormats(), GetVideoSegment);
    }

    AdaptiveQualityDecision? IAdaptiveQualityResolver.AdaptiveStartDecision
    {
        get { lock (InitializationSync) return _adaptiveStartDecision; }
    }

    /// <summary>
    /// Whether no quality is chosen by hand, so that <see cref="GetCurrentFormat"/> is the automatic choice.
    /// </summary>
    internal bool IsAutoQuality() => PlayingSelection is { } playing ? playing.Preferences.VideoQuality is not > 0 : GetManualQuality(_snapshot) <= 0;

    // The quality chosen by hand: the one of this playback, or the one remembered from an earlier media.
    internal static int GetManualQuality(MediaSnapshot snapshot)
        => snapshot.PreferQuality is int quality and > 0 ? quality : SettingsToolkit.ReadLocalSetting(SettingNames.ManualVideoQuality, 0);

    // The format list names every quality of the media, also those this response carries no stream for.
    // Only a quality with a stream can play; a response without any stream list is left as it is.
    internal static IEnumerable<PlayerFormatInformation> KeepFormatsWithStream(IEnumerable<PlayerFormatInformation> formats, IList<DashSegmentInformation>? streams)
    {
        if (streams is not { Count: > 0 })
        {
            return formats;
        }

        var ids = streams.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var withStream = formats.Where(p => ids.Contains(p.Quality.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
        return withStream.Count > 0 ? withStream : formats;
    }

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
            options.MediaName = _snapshot.Video.Identifier.Title;
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

            var streamUrl = fileUrl;
            InjectDebugReplayFailure(ref fileUrl, options);
            return new MpvMediaSource(fileUrl, _snapshot.Video.Identifier.Id, _snapshot.Video.Identifier.Title, options)
            {
                // The player reads the streams through its relay, so a node can change while the media plays.
                Relay = fileUrl.Length > 0 && string.Equals(fileUrl, streamUrl, StringComparison.Ordinal) ? CreateRelaySource(userAgent, headers, logger) : null,
            };
        }
    }

    // Implemented only in Debug builds; Release drops this declaration together with its call.
    partial void InjectDebugReplayFailure(ref string location, MpvPlayOptions options);

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
            if (_snapshot is null || _part is null)
            {
                throw new InvalidOperationException("必须先初始化媒体信息.");
            }

            if (_initTask is null || _initTask.IsFaulted || _initTask.IsCanceled)
            {
                // A failed request is not kept: the next source asks again, as a reload does.
                _initTask = LoadMediaInfoCoreAsync(_snapshot, _part, GetRequestedLanguage(_snapshot.PreferAudioLanguageId), request);
            }

            loadTask = _initTask;
        }

        await loadTask;
        lock (InitializationSync)
        {
            ThrowIfInitializationInvalid(request);
        }
    }

    private async Task LoadMediaInfoCoreAsync(MediaSnapshot snapshot, VideoPart part, string? curLanguage, (long Generation, CancellationToken CancellationToken) request)
    {
        try
        {
            var cid = Convert.ToInt64(part.Identifier.Id);
            var info = await playerService.GetVideoPlayDetailAsync(snapshot.Video.Identifier, cid, request.CancellationToken, curLanguage);
            var servedLanguage = curLanguage;
            if (curLanguage is not null && !AudioLanguageInformation.MatchesCode(curLanguage, info?.CurrentLanguage))
            {
                // The response names the language it serves. An empty name is the original audio: the
                // language is not offered here, or not to this account. Any other name is a third
                // language, so the original audio is requested instead of playing something unasked for.
                servedLanguage = default;
                if (!string.IsNullOrEmpty(info?.CurrentLanguage))
                {
                    info = await playerService.GetVideoPlayDetailAsync(snapshot.Video.Identifier, cid, request.CancellationToken);
                }
            }

            lock (InitializationSync)
            {
                ThrowIfInitializationInvalid(request);
                _playbackInfo = info;
                _servedLanguage = servedLanguage;
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

            logger.LogError("Playback | play detail request failed kind=video error={Error}", ex.GetType().Name);
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
            AdaptiveQualityDiagnostics.LogDecision(logger, _adaptiveStartDecision.Value);
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
        var preferCodecId = preferCodec switch { "hev" => 12, "av01" => 13, _ => 7 };
        var quality = selectedQuality.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return videoSegments?.FirstOrDefault(p => p.Id == quality && (p.CodecId == preferCodecId || p.Codecs.Contains(preferCodec, StringComparison.Ordinal)))
            ?? videoSegments?.FirstOrDefault(p => p.Id == quality && (p.CodecId == 7 || p.Codecs.Contains("avc", StringComparison.Ordinal)))
            ?? videoSegments?.FirstOrDefault(p => p.Id == quality && (p.CodecId == 12 || p.Codecs.Contains("hev", StringComparison.Ordinal)))
            ?? videoSegments?.FirstOrDefault(p => p.Id == quality && (p.CodecId == 13 || p.Codecs.Contains("av01", StringComparison.Ordinal)))
            ?? videoSegments?.FirstOrDefault(p => p.Id == quality);
    }

    // The language code to request with cur_language; the original audio is requested without one.
    private static string? GetRequestedLanguage(string? languageId)
        => string.IsNullOrWhiteSpace(languageId) || languageId == AudioLanguageInformation.OriginalId ? default : languageId;

    private OnDemandSelectionPreferences GetPreferences()
        => new(GetManualQuality(_snapshot), _snapshot.PreferAudioQualityId, _snapshot.PreferAudioLanguageId, _snapshot.IsAutomaticAudioStandardOnly);

    private AudioQualityInformation? GetSelectedAudioQuality(OnDemandSelectionRequest request)
        => AudioSelections.PickQuality(_playbackInfo?.AudioQualities, request.ResolvedAudioQualityId ?? request.Preferences.AudioQualityId,
            request.Preferences.StandardAudioOnly);
}
