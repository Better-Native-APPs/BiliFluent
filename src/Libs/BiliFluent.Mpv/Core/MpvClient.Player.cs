// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.
// Modified for BiliFluent: native calls go through HanumanInstitute.LibMpv, results use MpvResult.

using System.Globalization;
using Microsoft.Extensions.Logging;
using static Richasy.MpvKernel.Core.Enums.MpvClientProperties;

namespace Richasy.MpvKernel.Core;

public sealed partial class MpvClient
{
    private static readonly bool SubtitleDiagnosticsEnabled = Environment.GetEnvironmentVariable("BILIFLUENT_SUBTITLE_DIAGNOSTICS") == "1";

    private LogLevel SubtitleDiagnosticLevel => SubtitleDiagnosticsEnabled ? LogLevel.Information : LogLevel.Debug;

    /// <summary>
    /// 播放指定路径的文件.
    /// </summary>
    /// <returns><see cref="Task"/>.</returns>
    public async Task PlayAsync(string filePath, MpvPlayOptions? options = null)
    {
        if (!Uri.IsWellFormedUriString(filePath, UriKind.Absolute))
        {
            filePath = filePath.Replace("\\", "/");
        }

        var errorCode = MpvError.Success;
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        List<string> commandArgs = [];
        List<string> commandOptions = [];
        if (extension.StartsWith(".iso"))
        {
            var isBd = filePath.Contains("BD", StringComparison.Ordinal);
            var preferLoader = options?.InitExtraLoader;
            if (!string.IsNullOrEmpty(preferLoader))
            {
                isBd = string.Equals(preferLoader, "bd", StringComparison.OrdinalIgnoreCase);
            }

            commandArgs = ["loadfile", isBd ? "bd://" : "dvd://", "replace", "0"];
            await Task.Run(() => errorCode = _handle.SetOptionString(isBd ? "bluray-device" : "dvd-device", filePath));
            ThrowIfFailed(errorCode, "Mpv | set bluray/dvd device failed");
        }
        else
        {
            commandArgs = ["loadfile", $"\"{filePath.Replace("\"", "\\\"")}\"", "replace", "0"];
        }

        if (options != null)
        {
            if (options.WindowHandle != null)
            {
                await Task.Run(() => errorCode = _handle.SetOption("wid", (long)options.WindowHandle.Value.ToInt64()));
                ThrowIfFailed(errorCode, "Mpv | set wid failed");
            }

            if (options.EnableYtdl != null)
            {
                await Task.Run(() => errorCode = _handle.SetOptionString("ytdl", options.EnableYtdl.Value ? "yes" : "no"));
                ThrowIfFailed(errorCode, "Mpv | set ytdl failed");
            }

            if (options.EnableCookies != null)
            {
                await Task.Run(() => errorCode = _handle.SetOptionString("cookies", options.EnableCookies.Value ? "yes" : "no"));
                ThrowIfFailed(errorCode, "Mpv | set cookies failed");
            }

            if (!string.IsNullOrEmpty(options.UserAgent))
            {
                await Task.Run(() => errorCode = _handle.SetOptionString("user-agent", options.UserAgent));
                ThrowIfFailed(errorCode, "Mpv | set user-agent failed");
            }

            if (options.HttpHeaders != null)
            {
                var headers = options.HttpHeaders.Select(kvp => $"{kvp.Key}: {kvp.Value}").ToArray();
                var headerStr = string.Join("\n", headers);
                await Task.Run(() => errorCode = _handle.SetOptionString("http-header-fields", headerStr));
            }

            if (options.StartPosition != null)
            {
                commandOptions.Add($"start={Math.Round(options.StartPosition.Value)}");
            }

            if (options.InitialVolume != null)
            {
                commandOptions.Add($"volume={Math.Round(options.InitialVolume.Value)}");
            }

            if (options.InitialSpeed != null)
            {
                commandOptions.Add($"speed={Math.Round(options.InitialSpeed.Value, 2)}");
            }

            //if (!string.IsNullOrEmpty(options.MediaName))
            //{
            //    commandOptions.Add($"force-media-title=\"{options.MediaName}\"");
            //}

            if (options.Subtitles?.Count > 0)
            {
                if (options.Subtitles.Count == 1)
                {
                    commandOptions.Add($"sub-file=\"{options.Subtitles[0]}\"");
                }
                else
                {
                    foreach (var item in options.Subtitles)
                    {
                        commandOptions.Add($"sub-files-append={item}");
                    }
                }
            }

            if (options.AudioTracks?.Count > 0)
            {
                if (options.AudioTracks.Count == 1)
                {
                    commandOptions.Add($"audio-file=\"{options.AudioTracks[0]}\"");
                }
                else
                {
                    foreach (var item in options.AudioTracks)
                    {
                        commandOptions.Add($"audio-files-append=\"{item}\"");
                    }
                }
            }
        }

        if (commandOptions.Count > 0)
        {
            var optionStr = string.Join(',', commandOptions);
            commandArgs.Add(optionStr);
        }

        var cmd = string.Join(' ', commandArgs);
        await Task.Run(() =>
        {
            TraceSubtitleState("loadfile", "before");
            errorCode = _handle.CommandString(cmd);
            TraceSubtitleState("loadfile", "after", errorCode.ToString());
        });
        ThrowIfFailed(errorCode, "Mpv | loadfile failed");
    }

    /// <summary>
    /// 使播放器暂停.
    /// </summary>
    /// <returns><see cref="Task"/>.</returns>
    public async Task<MpvResult> PauseAsync()
    {
        var errorCode = MpvError.Success;
        await Task.Run(() => errorCode = _handle.SetProperty(Pause, true));
        return WrapAsResult(errorCode, "Mpv | set pause failed");
    }

    /// <summary>
    /// 使播放器恢复播放.
    /// </summary>
    /// <returns><see cref="Task"/>.</returns>
    public async Task<MpvResult> ResumeAsync()
    {
        var errorCode = MpvError.Success;
        await Task.Run(() => errorCode = _handle.SetProperty(Pause, false));
        return WrapAsResult(errorCode, "Mpv | set pause failed");
    }

    /// <summary>
    /// 停止播放.
    /// </summary>
    /// <returns><see cref="Task"/>.</returns>
    public async Task<MpvResult> StopAsync()
    {
        var errorCode = MpvError.Success;
        await Task.Run(() => errorCode = _handle.SetProperty(Stop, true));
        return WrapAsResult(errorCode, "Mpv | set stop failed");
    }

    /// <summary>
    /// 获取当前是否为暂停状态.
    /// </summary>
    /// <returns>是否暂停.</returns>
    public async Task<MpvResult<MpvPlayerState>> GetPlayerStateAsync()
    {
        var errorCode = MpvError.Success;
        bool result = default;
        await Task.Run(() => errorCode = _handle.GetProperty(CoreIdle, out result));
        var stateResult = WrapAsResult(errorCode, "Mpv | get core-idle failed");
        if (stateResult.IsFailed)
        {
            return stateResult;
        }

        var isCoreIdle = result;
        if (!isCoreIdle)
        {
            return MpvPlayerState.Playing;
        }

        await Task.Run(() => errorCode = _handle.GetProperty(PausedForCache, out result));
        stateResult = WrapAsResult(errorCode, "Mpv | get paused-for-cache failed");
        if (stateResult.IsFailed)
        {
            return stateResult;
        }

        var isBuffering = result;
        if (isBuffering)
        {
            return MpvPlayerState.Buffering;
        }

        await Task.Run(() => errorCode = _handle.GetProperty(Seeking, out result));
        stateResult = WrapAsResult(errorCode, "Mpv | get seeking failed");
        if (stateResult.IsFailed)
        {
            return stateResult;
        }

        var isSeeking = result;
        if (isSeeking)
        {
            return MpvPlayerState.Seeking;
        }

        await Task.Run(() => errorCode = _handle.GetProperty(EofReached, out result));
        stateResult = WrapAsResult(errorCode, "Mpv | get eof-reached failed");
        if (stateResult.IsFailed)
        {
            return stateResult;
        }

        var isEnd = result;
        if (isEnd)
        {
            return MpvPlayerState.End;
        }

        await Task.Run(() => errorCode = _handle.GetProperty(IdleActive, out result));
        stateResult = WrapAsResult(errorCode, "Mpv | get idle-active failed");
        if (stateResult.IsFailed)
        {
            return stateResult;
        }

        var isIdle = result;
        if (isIdle)
        {
            return MpvPlayerState.Idle;
        }

        return MpvPlayerState.Paused;
    }

    /// <summary>
    /// 获取当前播放位置.
    /// </summary>
    /// <returns>播放位置（秒）.</returns>
    public async Task<MpvResult<double>> GetCurrentPositionAsync()
    {
        var errorCode = MpvError.Success;
        double result = default;
        await Task.Run(() => errorCode = _handle.GetProperty(TimePosition, out result));
        var positionResult = WrapAsResult(errorCode, "Mpv | get time-pos failed");
        if (positionResult.IsFailed)
        {
            return positionResult;
        }

        return result;
    }

    /// <summary>
    /// 设置当前播放位置.
    /// </summary>
    /// <returns><see cref="Task"/>.</returns>
    public async Task<MpvResult> SetCurrentPositionAsync(double position)
    {
        var errorCode = MpvError.Success;
        await Task.Run(() => errorCode = _handle.SetProperty(TimePosition, position));
        return WrapAsResult(errorCode, "Mpv | set time-pos failed");
    }

    /// <summary>
    /// 获取当前播放文件的时长.
    /// </summary>
    /// <returns>时长（秒）</returns>
    public async Task<MpvResult<double>> GetDurationAsync()
    {
        var errorCode = MpvError.Success;
        double result = default;
        await Task.Run(() => errorCode = _handle.GetProperty(Duration, out result));
        var durationResult = WrapAsResult(errorCode, "Mpv | get duration failed");
        if (durationResult.IsFailed)
        {
            return durationResult;
        }

        return result;
    }

    /// <summary>
    /// 获取当前播放文件的音量.
    /// </summary>
    /// <returns></returns>
    public async Task<MpvResult<double>> GetVolumeAsync()
    {
        var errorCode = MpvError.Success;
        double result = default;
        await Task.Run(() => errorCode = _handle.GetProperty(Volume, out result));
        var volumeResult = WrapAsResult(errorCode, "Mpv | get volume failed");
        if (volumeResult.IsFailed)
        {
            return volumeResult;
        }

        return result;
    }

    /// <summary>
    /// 设置音量.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the volume value is less than 0 or greater than 100.</exception>
    public async Task<MpvResult> SetVolumeAsync(double volume)
    {
        if (volume is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be between 0 and 100.");
        }

        var errorCode = MpvError.Success;
        await Task.Run(() => errorCode = _handle.SetProperty(Volume, volume));
        return WrapAsResult(errorCode, "Mpv | set volume failed");
    }

    /// <summary>
    /// 获取当前播放速度.
    /// </summary>
    /// <returns>播放速度.</returns>
    public async Task<MpvResult<double>> GetSpeedAsync()
    {
        var errorCode = MpvError.Success;
        double result = default;
        await Task.Run(() => errorCode = _handle.GetProperty(Speed, out result));
        var speedResult = WrapAsResult(errorCode, "Mpv | get speed failed");
        if (speedResult.IsFailed)
        {
            return speedResult;
        }

        return result;
    }

    /// <summary>
    /// 设置播放速度.
    /// </summary>
    /// <param name="speed">播放速度.</param>
    /// <returns><see cref="Task"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public async Task<MpvResult> SetSpeedAsync(double speed)
    {
        if (speed is < 0.01 or > 100)
        {
            return MpvResult.Fail("Speed must be between 0.01 and 100.");
        }

        var errorCode = MpvError.Success;
        await Task.Run(() => errorCode = _handle.SetProperty(Speed, speed));
        return WrapAsResult(errorCode, "Mpv | set speed failed");
    }

    /// <summary>
    /// 设置字幕轨道.
    /// </summary>
    /// <param name="trackId">字幕ID.</param>
    /// <param name="cancellationToken">当前媒体的取消信号.</param>
    /// <returns><see cref="Task"/>.</returns>
    public async Task<MpvResult> SetSubtitleTrackAsync(int? trackId, CancellationToken cancellationToken = default)
    {
        var selection = trackId?.ToString(CultureInfo.InvariantCulture) ?? "no";
        return await Task.Run(() =>
        {
            var result = WrapAsResult(SetPropertyString("sid", selection), "Mpv | set subtitle track failed");
            return result.IsSuccess && GetPropertyString("sid") != selection
                ? MpvResult.Fail("Mpv | subtitle track selection did not take effect")
                : result;
        }, cancellationToken);
    }

    /// <summary>
    /// 设置外挂字幕轨道.
    /// </summary>
    /// <param name="externalUrl">字幕路径.</param>
    /// <param name="flag">行为标记.</param>
    /// <param name="cancellationToken">当前媒体的取消信号.</param>
    /// <returns><see cref="Task"/>.</returns>
    public async Task<MpvResult> SetExternalSubtitleTrackAsync(string externalUrl, string flag = "select", CancellationToken cancellationToken = default)
    {
        try
        {
            var subTask = Task.Run(() => LoadExternalSubtitleTrack(externalUrl, flag), cancellationToken);
            var (result, trackId) = await subTask.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            if (result.IsFailed || trackId is null)
            {
                return result;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await SetSubtitleTrackAsync(trackId, cancellationToken);
        }
        catch (TimeoutException)
        {
            if (_logger.IsEnabled(SubtitleDiagnosticLevel))
            {
                _logger.Log(SubtitleDiagnosticLevel, "Subtitle load timed out: file={File}; the pending sub-add cannot select sid.",
                    SubtitleDiagnosticText(externalUrl));
            }

            return MpvResult.Fail("Mpv | set external subtitle track failed: operation timed out");
        }
    }

    private (MpvResult Result, int? TrackId) LoadExternalSubtitleTrack(string externalUrl, string flag)
    {
        try
        {
            var separator = flag.IndexOf('+');
            var selection = separator < 0 ? flag : flag[..separator];
            var selectAfterLoad = selection is "select" or "cached";
            if (selection == "cached")
            {
                var cached = FindExternalSubtitleTrack(externalUrl);
                if (cached.Result.IsFailed || cached.TrackId is not null)
                {
                    return cached;
                }
            }

            // A synchronous native command cannot be retracted by WaitAsync. Load without
            // selection so a command completing after timeout/cancellation cannot change sid.
            var loadFlag = selectAfterLoad ? "auto" + flag[selection.Length..] : flag;
            var result = WrapAsResult(Command("sub-add", externalUrl, loadFlag), "Mpv | set external subtitle track failed");
            if (result.IsFailed || !selectAfterLoad)
            {
                return (result, null);
            }

            var added = FindExternalSubtitleTrack(externalUrl);
            return added.Result.IsSuccess && added.TrackId is null
                ? (MpvResult.Fail("Mpv | added external subtitle track was not found"), null)
                : added;
        }
        catch (Exception ex)
        {
            // Exception messages from file/native IO can contain private absolute paths.
            return (MpvResult.Fail("Mpv | set external subtitle track failed: " + ex.GetType().Name), null);
        }
    }

    private (MpvResult Result, int? TrackId) FindExternalSubtitleTrack(string externalUrl)
    {
        if (IsDisposed)
        {
            return (WrapAsResult(MpvError.Uninitialized, "Mpv | get external subtitle track failed"), null);
        }

        var result = WrapAsResult(_handle.GetPropertyNode("track-list", out var tracks), "Mpv | get external subtitle track failed");
        if (result.IsFailed)
        {
            return (result, null);
        }

        if (tracks is not List<object?> trackList)
        {
            return (MpvResult.Fail("Mpv | get external subtitle track failed: invalid node format"), null);
        }

        int? newestId = null;
        var filename = externalUrl.Replace('\\', '/');
        foreach (var item in trackList)
        {
            if (item is Dictionary<string, object?> track
                && track.GetString("type") == "sub"
                && string.Equals(track.GetString("external-filename")?.Replace('\\', '/'), filename, StringComparison.OrdinalIgnoreCase)
                && track.GetInteger("id") is long id
                && (newestId is null || id > newestId))
            {
                newestId = Convert.ToInt32(id);
            }
        }

        return (MpvResult.Ok(), newestId);
    }

    private void TraceSubtitleState(string operation, string phase, string? result = null)
    {
        var level = SubtitleDiagnosticLevel;
        if (!_logger.IsEnabled(level) || IsDisposed) return;

        var error = _handle.GetPropertyNode("track-list", out var value);
        var tracks = new List<string>();
        if (value is List<object?> trackList)
        {
            foreach (var item in trackList)
            {
                if (item is not Dictionary<string, object?> track || track.GetString("type") != "sub") continue;
                tracks.Add(string.Create(CultureInfo.InvariantCulture,
                    $"id={track.GetInteger("id")}; title={SubtitleDiagnosticText(track.GetString("title"))}; selected={track.GetFlag("selected")}; file={SubtitleDiagnosticText(track.GetString("external-filename"))}"));
            }
        }

        _logger.Log(level,
            "Subtitle state: {Operation} {Phase}; result={Result}; sid={Sid}; secondary-sid={SecondarySid}; sub-visibility={Visibility}; secondary-sub-visibility={SecondaryVisibility}; time={Position}; track-list-result={TrackResult}; tracks=[{Tracks}]",
            operation, phase, result ?? "pending", _handle.GetPropertyString("sid"), _handle.GetPropertyString("secondary-sid"),
            _handle.GetPropertyString("sub-visibility"), _handle.GetPropertyString("secondary-sub-visibility"),
            _handle.GetPropertyString("time-pos"), error, string.Join(" | ", tracks));
    }

    private static string SubtitleDiagnosticText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "<none>";
        var query = value.IndexOfAny(['?', '#']);
        if (query >= 0) value = value[..query];
        return Path.GetFileName(value.Replace('\\', '/')).Replace('\r', ' ').Replace('\n', ' ');
    }

    /// <summary>
    /// 获取文件的轨道列表.
    /// </summary>
    /// <returns>轨道信息.</returns>
    public async Task<MpvResult<List<MpvTrackInfo>>> GetTracksAsync()
    {
        var errorCode = MpvError.Success;
        object? result = null;
        await Task.Run(() => errorCode = _handle.GetPropertyNode("track-list", out result));
        if (errorCode != MpvError.Success)
        {
            return MpvResult.Fail($"Mpv | get subtitle tracks failed: {errorCode}");
        }

        if (result is not List<object?> trackList)
        {
            return MpvResult.Fail("Mpv | get subtitle tracks failed: invalid node format");
        }

        var resultList = new List<MpvTrackInfo>();
        foreach (var item in trackList)
        {
            if (item is not Dictionary<string, object?> trackMeta)
            {
                continue;
            }

            var track = new MpvTrackInfo();
            track.Type = trackMeta.GetString("type") switch
            {
                "audio" => MpvTrackType.Audio,
                "video" => MpvTrackType.Video,
                "sub" => MpvTrackType.Subtitle,
                _ => MpvTrackType.Unknown,
            };
            track.Id = trackMeta.GetInteger("id") is long id ? Convert.ToInt32(id) : -1;
            var title = trackMeta.GetString("title");
            var lang = trackMeta.GetString("lang");
            var codecDesc = trackMeta.GetString("codec-desc");
            var decoder = trackMeta.GetString("decoder-desc");
            var audioChannels = trackMeta.GetInteger("audio-channels");
            track.Title = track.Type switch
            {
                MpvTrackType.Audio => string.IsNullOrEmpty(lang) ? title ?? codecDesc : $"{title} {codecDesc}".Trim(),
                MpvTrackType.Subtitle => string.IsNullOrEmpty(lang) ? title ?? decoder ?? codecDesc : $"{title} {decoder} {codecDesc}".Trim(),
                _ => title,
            };

            if (track.Type == MpvTrackType.Audio && audioChannels.HasValue)
            {
                track.Title = $"{track.Title} ({audioChannels} ch)".Trim();
            }

            track.Language = lang;
            track.Current = trackMeta.GetFlag("selected");
            resultList.Add(track);
        }

        return resultList;
    }

    /// <summary>
    /// 设置音频通道布局.
    /// </summary>
    public async Task<MpvResult> SetAudioChannelLayoutAsync(AudioChannelLayoutType layout, string[]? customLayouts = default)
    {
        var errorCode = MpvError.Success;
        var layoutStr = layout switch
        {
            AudioChannelLayoutType.Stereo => "stereo",
            AudioChannelLayoutType.Auto => "auto",
            AudioChannelLayoutType.Mono => "mono",
            AudioChannelLayoutType.Custom when customLayouts != null => string.Join(',', customLayouts),
            _ => throw new ArgumentOutOfRangeException(nameof(layout), "Unsupported audio channel layout type."),
        };

        await Task.Run(() => errorCode = _handle.SetOptionString("audio-channels", layoutStr));
        return WrapAsResult(errorCode, "Mpv | set audio channel layout failed");
    }

    /// <summary>
    /// 获取章节列表.
    /// </summary>
    /// <returns>章节列表</returns>
    public async Task<MpvResult<List<MpvChapterInfo>>> GetChaptersAsync()
    {
        var errorCode = MpvError.Success;
        object? result = null;
        await Task.Run(() => errorCode = _handle.GetPropertyNode("chapter-list", out result));
        if (errorCode != MpvError.Success)
        {
            return MpvResult.Fail($"Mpv | get chapter list failed: {errorCode}");
        }

        if (result is not List<object?> chapterList)
        {
            return MpvResult.Fail("Mpv | get chapter list failed: invalid node format");
        }

        var resultList = new List<MpvChapterInfo>();
        foreach (var item in chapterList)
        {
            if (item is not Dictionary<string, object?> chapterMeta || chapterMeta.Count == 0)
            {
                continue;
            }

            resultList.Add(new MpvChapterInfo
            {
                Title = chapterMeta.GetString("title") ?? string.Empty,
                Time = chapterMeta.GetDouble("time") ?? 0,
            });
        }

        return resultList;
    }

    /// <summary>
    /// 设置字幕位置.
    /// </summary>
    /// <param name="percentage">垂直方向百分比.</param>
    /// <returns><see cref="Task"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public async Task SetSubtitlePositionAsync(int percentage)
    {
        if (percentage is < 0 or > 150)
        {
            throw new ArgumentOutOfRangeException(nameof(percentage), "Subtitle position must be between 0 and 150.");
        }

        var errorCode = MpvError.Success;
        await Task.Run(() => errorCode = _handle.SetOption("sub-pos", (long)percentage));
        ThrowIfFailed(errorCode, "Mpv | set subtitle position failed");
    }

    /// <summary>
    /// 设置文本字幕字号
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public async Task SetSubtitleFontSizeAsync(int fontSize)
    {
        if (fontSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(fontSize), "Subtitle font size must greater than 0.");
        }

        var errorCode = MpvError.Success;
        await Task.Run(() => errorCode = _handle.SetOption("sub-font-size", (long)fontSize));
        ThrowIfFailed(errorCode, "Mpv | set subtitle font size failed");
    }

    /// <summary>
    /// 设置字幕字体.
    /// </summary>
    /// <exception cref="ArgumentNullException"></exception>
    public async Task SetSubtitleFontFamilyAsync(string fontFamily)
    {
        if (string.IsNullOrEmpty(fontFamily))
        {
            throw new ArgumentNullException(nameof(fontFamily), "Subtitle font family cannot be null or empty.");
        }

        var errorCode = MpvError.Success;
        await Task.Run(() => errorCode = _handle.SetOptionString("sub-font", fontFamily));
        ThrowIfFailed(errorCode, "Mpv | set subtitle font family failed");
    }

    /// <summary>
    /// 设置平移扫描（主要用于去黑边）
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public async Task SetPanscanAsync(double value)
    {
        if (value < 0 || value > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Panscan value must be between 0 and 1.");
        }

        var errorCode = MpvError.Success;
        await Task.Run(() => errorCode = _handle.SetOption("panscan", value));
        ThrowIfFailed(errorCode, "Mpv | set panscan failed");
    }

    /// <summary>
    /// 截图到文件.
    /// </summary>
    public async Task TakeScreenshotAsync(string filePath)
    {
        var errorCode = MpvError.Success;
        await Task.Run(() => errorCode = _handle.Command(["screenshot-to-file", filePath]));
        ThrowIfFailed(errorCode, "Mpv | take screenshot failed");
    }
}
