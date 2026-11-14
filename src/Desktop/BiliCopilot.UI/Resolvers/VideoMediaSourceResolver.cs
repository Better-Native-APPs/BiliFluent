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

internal sealed partial class VideoMediaSourceResolver(IPlayerService playerService, IBiliCookiesResolver cookiesResolver, ILogger<VideoMediaSourceResolver> logger) : MediaSourceResolverBase
{
    private const string DubbingTrackPrefix = "ai:";
    private MediaSnapshot _snapshot;
    private VideoPart _part;
    private Task? _initTask;
    private DashMediaInformation? _playbackInfo;

    // The dubbing language the loaded (or loading) play detail was requested with; null is the original audio.
    private string? _loadedLanguage;

    // Tracks of the original audio, kept for the list while a dubbing language is loaded.
    private IList<AudioTrackInformation>? _originalAudioTracks;

    public void Initialize(MediaSnapshot snapshot, VideoPart part)
    {
        lock (InitializationSync)
        {
            InvalidateInitialization();
            _snapshot = snapshot;
            _part = part;
            _originalAudioTracks = default;
        }
    }

    protected override void ResetInitialization()
    {
        _playbackInfo = default;
        _initTask = default;
        _loadedLanguage = default;
    }

    internal void ResetSnapshot(MediaSnapshot snapshot)
    {
        lock (InitializationSync)
        {
            _snapshot = snapshot;
        }
    }

    /// <summary>
    /// Takes over the audio track chosen in <paramref name="snapshot"/>. Another dubbing language, or going back
    /// to the original audio, needs a new play detail request, so the loaded one is dropped.
    /// </summary>
    internal void ResetAudioTrack(MediaSnapshot snapshot)
    {
        lock (InitializationSync)
        {
            _snapshot = snapshot;
            if (_initTask is null)
            {
                return;
            }

            // A failed request is dropped as well, so choosing the track again retries it.
            if (_initTask.IsFaulted || _initTask.IsCanceled || GetDubbingLanguage(snapshot.PreferAudioTrackId) != _loadedLanguage)
            {
                InvalidateInitialization();
            }
        }
    }

    /// <summary>
    /// The audio tracks of the loaded media and the ID of the one that plays. Empty until the media is loaded.
    /// </summary>
    internal (List<AudioTrackInformation> Tracks, string? CurrentId) GetAudioTracks()
    {
        lock (InitializationSync)
        {
            if (_playbackInfo?.AudioTracks is not { Count: > 0 } loaded)
            {
                return ([], default);
            }

            // With a dubbing language loaded, the standard streams in the response carry the dubbing.
            // The list keeps showing the tracks of the original audio; picking one requests them again.
            List<AudioTrackInformation> tracks = _loadedLanguage is not null && _originalAudioTracks is not null
                ? [.. _originalAudioTracks.Where(p => !p.IsAI), .. loaded.Where(p => p.IsAI)]
                : [.. loaded];
            return (tracks, GetSelectedAudioTrack()?.Id ?? GetDefaultAudioTrackId());
        }
    }

    internal PlayerFormatInformation? GetCurrentFormat()
    {
        var preferFormatSetting = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.PreferQuality, PreferQualityType.Auto);
        var availableFormats = _playbackInfo.Formats.ToList().ConvertAll(p => new SourceItemViewModel(p, default));
        if (_snapshot.Video.Publisher?.User?.Id != this.Get<AccountViewModel>().MyProfile.User.Id)
        {
            availableFormats = [.. availableFormats.Where(p => p.IsEnabled)];
        }

        SourceItemViewModel? selectedFormat = default;
        if (_snapshot.PreferQuality > 0)
        {
            selectedFormat = availableFormats.Find(p => p.Data.Quality == _snapshot.PreferQuality);
        }

        if (selectedFormat == null)
        {
            if (preferFormatSetting == PreferQualityType.Auto)
            {
                var lastSelectedFormat = SettingsToolkit.ReadLocalSetting(SettingNames.LastSelectedVideoQuality, 0);
                selectedFormat = availableFormats.Find(p => p.Data.Quality == lastSelectedFormat);
            }
            else if (preferFormatSetting == PreferQualityType.UHD)
            {
                selectedFormat = availableFormats.Find(p => p.Data.Quality == 120);
            }
            else if (preferFormatSetting == PreferQualityType.HD)
            {
                var hdFormats = availableFormats.Where(p => p.Data.Quality == 116 || p.Data.Quality == 80).ToList();
                selectedFormat = hdFormats.OrderByDescending(p => p.Data.Quality).FirstOrDefault();
            }
        }

        if (selectedFormat is null)
        {
            // Auto without a remembered choice stays at 1080p60 (qn 116) or below.
            selectedFormat = availableFormats
                .Where(p => preferFormatSetting != PreferQualityType.Auto || _snapshot.PreferQuality > 0 || p.Data.Quality <= 116)
                .OrderByDescending(p => p.Data.Quality)
                .FirstOrDefault();
        }

        return selectedFormat?.Data;
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
            var (videoUrl, audioUrl) = GetPlayUrls();
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

            InjectDebugReplayFailure(ref fileUrl, options);
            return new MpvMediaSource(fileUrl, _snapshot.Video.Identifier.Id, _snapshot.Video.Identifier.Title, options);
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

            if (_initTask is null)
            {
                _loadedLanguage = GetDubbingLanguage(_snapshot.PreferAudioTrackId);
                _initTask = LoadMediaInfoCoreAsync(_snapshot, _part, _loadedLanguage, request);
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
            var info = await playerService.GetVideoPlayDetailAsync(snapshot.Video.Identifier, Convert.ToInt64(part.Identifier.Id), request.CancellationToken, curLanguage);
            lock (InitializationSync)
            {
                ThrowIfInitializationInvalid(request);
                _playbackInfo = info;
                if (curLanguage is null)
                {
                    _originalAudioTracks = info?.AudioTracks;
                }
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

            logger.LogError(ex, "获取视频播放信息失败");
            throw;
        }
    }

    private (string? video, string? audio) GetPlayUrls()
    {
        var videoSegments = _playbackInfo.Videos;
        var audioSegments = _playbackInfo.Audios;
        var selectedFormat = GetCurrentFormat();
        if (selectedFormat is null)
        {
            throw new InvalidCastException("无法获取到有效的播放地址");
        }

        var maxAudioQuality = audioSegments?.Max(p => Convert.ToInt32(p.Id));
        var preferCodec = AppToolkit.GetPreferCodecId();
        var preferCodecId = preferCodec switch { "hev" => 12, "av01" => 13, _ => 7 };
        var quality = selectedFormat.Quality.ToString();
        var vSeg = videoSegments?.FirstOrDefault(p => p.Id == quality && (p.CodecId == preferCodecId || p.Codecs.Contains(preferCodec, StringComparison.Ordinal)))
            ?? videoSegments?.FirstOrDefault(p => p.Id == quality && (p.CodecId == 7 || p.Codecs.Contains("avc", StringComparison.Ordinal)))
            ?? videoSegments?.FirstOrDefault(p => p.Id == quality && (p.CodecId == 12 || p.Codecs.Contains("hev", StringComparison.Ordinal)))
            ?? videoSegments?.FirstOrDefault(p => p.Id == quality && (p.CodecId == 13 || p.Codecs.Contains("av01", StringComparison.Ordinal)))
            ?? videoSegments?.FirstOrDefault(p => p.Id == quality);
        // The track the user picked, or the highest standard quality when nothing (available) is picked.
        var aSeg = GetSelectedAudioTrack()?.Segment
            ?? audioSegments?.FirstOrDefault(p => p.Id == maxAudioQuality.ToString());

        var videoUrl = vSeg?.BaseUrl;
        var audioUrl = aSeg?.BaseUrl;

        if (SettingsToolkit.ReadLocalSetting(SettingNames.PlayWithoutP2P, false))
        {
            if (vSeg?.BackupUrls is not null)
            {
                string[] videoUrls = [vSeg?.BaseUrl, .. vSeg?.BackupUrls];
                videoUrl = Array.Find(videoUrls, p => !AppToolkit.IsP2PUrl(p)) ?? vSeg?.BaseUrl;
            }

            if (aSeg?.BackupUrls is not null)
            {
                string[] audioUrls = [aSeg?.BaseUrl, .. aSeg?.BackupUrls];
                audioUrl = Array.Find(audioUrls, p => !AppToolkit.IsP2PUrl(p)) ?? aSeg?.BaseUrl;
            }
        }

        SettingsToolkit.WriteLocalSetting(SettingNames.LastSelectedVideoQuality, selectedFormat.Quality);
        return (videoUrl, audioUrl);
    }

    // An "ai:{lang}" track ID names the dubbing language to request; any other ID plays the original audio.
    private static string? GetDubbingLanguage(string? trackId)
        => trackId?.StartsWith(DubbingTrackPrefix, StringComparison.Ordinal) == true && trackId.Length > DubbingTrackPrefix.Length
            ? trackId[DubbingTrackPrefix.Length..]
            : default;

    // The picked track, when the loaded media has it and it comes with an address.
    private AudioTrackInformation? GetSelectedAudioTrack()
    {
        var id = _snapshot.PreferAudioTrackId;
        if (string.IsNullOrEmpty(id) || _playbackInfo is not { } info)
        {
            return default;
        }

        // A response that names another language did not serve the requested dubbing. The two codes are
        // compared loosely ("en" matches "en-US"), so a different spelling of one language is not a failure.
        var language = GetDubbingLanguage(id);
        if (language is not null && !string.IsNullOrEmpty(info.CurrentLanguage)
            && !info.CurrentLanguage.StartsWith(language, StringComparison.OrdinalIgnoreCase)
            && !language.StartsWith(info.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return default;
        }

        return info.AudioTracks?.FirstOrDefault(p => p.Id == id && !string.IsNullOrEmpty(p.Segment?.BaseUrl));
    }

    private string? GetDefaultAudioTrackId()
        => _playbackInfo?.Audios is { Count: > 0 } audios ? $"std:{audios.Max(p => Convert.ToInt32(p.Id))}" : default;
}
