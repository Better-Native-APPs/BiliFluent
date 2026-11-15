// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.Models;

/// <summary>
/// 视频快照.
/// </summary>
public sealed class MediaSnapshot
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSnapshot"/> class.
    /// </summary>
    public MediaSnapshot(
        VideoInformation video,
        bool isPrivate = false)
    {
        Video = video;
        Type = BiliMediaType.Video;
        IsPrivate = isPrivate;
    }

    public MediaSnapshot(SeasonInformation? season, EpisodeInformation? episode)
    {
        Season = season;
        Episode = episode;
        Type = BiliMediaType.Pgc;
        IsPrivate = false;
    }

    /// <summary>
    /// 视频信息.
    /// </summary>
    public VideoInformation? Video { get; }

    /// <summary>
    /// 电视剧.
    /// </summary>
    public SeasonInformation? Season { get; set; }

    /// <summary>
    /// 剧集.
    /// </summary>
    public EpisodeInformation? Episode { get; set; }

    /// <summary>
    /// 无痕播放（不上报历史记录）.
    /// </summary>
    public bool IsPrivate { get; }

    /// <summary>
    /// 媒体类型.
    /// </summary>
    public BiliMediaType Type { get; set; }

    /// <summary>
    /// 偏好的清晰度.
    /// </summary>
    public int? PreferQuality { get; set; }

    /// <summary>
    /// The audio language the user picked: "original", or the language code the API uses (for example "en" or "ja").
    /// Null plays the original audio.
    /// </summary>
    public string? PreferAudioLanguageId { get; set; }

    /// <summary>
    /// The stable ID of the audio quality the user picked (for example "std:30280", "dolby:30250" or "hires:30251").
    /// Only the ID is kept, never a signed address. Null plays the default quality.
    /// </summary>
    public string? PreferAudioQualityId { get; set; }

    /// <summary>
    /// The combined ID used before language and quality became two choices: "ai:{language}" named a dubbing
    /// language, anything else an audio quality. Kept so that an old value still lands in the right field.
    /// </summary>
    public string? PreferAudioTrackId
    {
        get => PreferAudioLanguageId is { Length: > 0 } language && language != AudioLanguageInformation.OriginalId ? $"ai:{language}" : PreferAudioQualityId;
        set
        {
            if (value?.StartsWith("ai:", StringComparison.Ordinal) == true)
            {
                PreferAudioLanguageId = value.Length > 3 ? value.Substring(3) : default;
            }
            else
            {
                PreferAudioQualityId = value;
            }
        }
    }

    /// <summary>
    /// 偏好的起始位置（秒）.
    /// </summary>
    public double? StartPosition { get; set; }

    /// <summary>
    /// 偏好的分P.
    /// </summary>
    public VideoPart? PreferPart { get; set; }

    /// <summary>
    /// 播放列表.
    /// </summary>
    public List<MediaSnapshot>? Playlist { get; set; }
}
