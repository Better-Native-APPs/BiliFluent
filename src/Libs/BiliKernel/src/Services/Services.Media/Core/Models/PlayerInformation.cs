// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Richasy.BiliKernel.Services.Media.Core.Models;

/// <summary>
/// Dash播放信息.
/// </summary>
internal sealed class PlayerInformation
{
    /// <summary>
    /// 视频清晰度.
    /// </summary>
    [JsonPropertyName("quality")]
    public int Quality { get; set; }

    /// <summary>
    /// 当前视频格式.
    /// </summary>
    [JsonPropertyName("format")]
    public string Format { get; set; }

    /// <summary>
    /// 视频时长.
    /// </summary>
    [JsonPropertyName("timelength")]
    public int Duration { get; set; }

    /// <summary>
    /// 支持的清晰度说明文本列表.
    /// </summary>
    [JsonPropertyName("accept_description")]
    public List<string> AcceptDescription { get; set; }

    /// <summary>
    /// 支持的清晰度列表.
    /// </summary>
    [JsonPropertyName("accept_quality")]
    public List<int> AcceptQualities { get; set; }

    /// <summary>
    /// 视频编码Id.
    /// </summary>
    [JsonPropertyName("video_codecid")]
    public int CodecId { get; set; }

    /// <summary>
    /// Dash视频播放信息.
    /// </summary>
    [JsonPropertyName("dash")]
    public DashVideo? VideoInformation { get; set; }

    /// <summary>
    /// Flv视频播放信息.
    /// </summary>
    [JsonPropertyName("durl")]
    public List<FlvItem> FlvInformation { get; set; }

    /// <summary>
    /// 支持的视频格式列表.
    /// </summary>
    [JsonPropertyName("support_formats")]
    public List<VideoFormat> SupportFormats { get; set; }

    /// <summary>
    /// Gets or sets the available dubbing languages.
    /// </summary>
    [JsonPropertyName("language")]
    public PlayerLanguageInformation? Language { get; set; }

    /// <summary>
    /// Gets or sets the dubbing language the response serves. Empty for the original audio.
    /// </summary>
    [JsonPropertyName("cur_language")]
    public string? CurrentLanguage { get; set; }
}

/// <summary>
/// Dash视频信息.
/// </summary>
internal sealed class DashVideo
{
    /// <summary>
    /// 视频时长.
    /// </summary>
    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    /// <summary>
    /// 最低缓冲时间.
    /// </summary>
    [JsonPropertyName("minBufferTime")]
    public float MinBufferTime { get; set; }

    /// <summary>
    /// 不同清晰度的视频列表.
    /// </summary>
    [JsonPropertyName("video")]
    public List<DashItem>? Video { get; set; }

    /// <summary>
    /// 不同码率的音频列表.
    /// </summary>
    [JsonPropertyName("audio")]
    public List<DashItem>? Audio { get; set; }

    /// <summary>
    /// Gets or sets the Dolby audio information.
    /// </summary>
    [JsonPropertyName("dolby")]
    public DolbyAudioInformation? Dolby { get; set; }

    /// <summary>
    /// Gets or sets the Hi-Res audio information.
    /// </summary>
    [JsonPropertyName("flac")]
    public FlacAudioInformation? Flac { get; set; }
}

/// <summary>
/// Dolby audio streams.
/// </summary>
internal sealed class DolbyAudioInformation
{
    /// <summary>
    /// Gets or sets the Dolby audio type reported by the API.
    /// </summary>
    [JsonPropertyName("type")]
    public int? Type { get; set; }

    /// <summary>
    /// Gets or sets the available audio streams.
    /// </summary>
    [JsonPropertyName("audio")]
    public List<DashItem?>? Audio { get; set; }

    /// <summary>
    /// Gets or sets the optional VIP requirement reported by the API.
    /// </summary>
    [JsonPropertyName("need_vip")]
    public bool? NeedVip { get; set; }
}

/// <summary>
/// Hi-Res lossless audio stream.
/// </summary>
internal sealed class FlacAudioInformation
{
    /// <summary>
    /// Gets or sets whether the API displays the Hi-Res switch.
    /// </summary>
    [JsonPropertyName("display")]
    public bool? Display { get; set; }

    /// <summary>
    /// Gets or sets the audio stream.
    /// </summary>
    [JsonPropertyName("audio")]
    public DashItem? Audio { get; set; }

    /// <summary>
    /// Gets or sets the optional VIP requirement reported by the API.
    /// </summary>
    [JsonPropertyName("need_vip")]
    public bool? NeedVip { get; set; }
}

/// <summary>
/// Dubbing languages supported by a video.
/// </summary>
internal sealed class PlayerLanguageInformation
{
    /// <summary>
    /// Gets or sets whether the video supports dubbing languages.
    /// </summary>
    [JsonPropertyName("support")]
    public bool? Support { get; set; }

    /// <summary>
    /// Gets or sets the available languages.
    /// </summary>
    [JsonPropertyName("items")]
    public List<PlayerLanguageItem?>? Items { get; set; }

    /// <summary>
    /// Gets or sets the title the API shows above the language list.
    /// </summary>
    [JsonPropertyName("list_title")]
    public string? ListTitle { get; set; }
}

/// <summary>
/// A dubbing language advertised by the API.
/// </summary>
internal sealed class PlayerLanguageItem
{
    /// <summary>
    /// Gets or sets the language code accepted by cur_language.
    /// </summary>
    [JsonPropertyName("lang")]
    public string? Language { get; set; }

    /// <summary>
    /// Gets or sets the language display name.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the production type of the dubbing, as reported by the API.
    /// </summary>
    [JsonPropertyName("production_type")]
    public int? ProductionType { get; set; }

    /// <summary>
    /// Gets or sets the optional VIP requirement reported by the API.
    /// </summary>
    [JsonPropertyName("need_vip")]
    public bool? NeedVip { get; set; }
}

/// <summary>
/// Dash条目.
/// </summary>
internal sealed class DashItem
{
    /// <summary>
    /// Dash Id.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// 基链接.
    /// </summary>
    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; set; }

    /// <summary>
    /// 备份链接.
    /// </summary>
    [JsonPropertyName("backup_url")]
    public List<string>? BackupUrl { get; set; }

    /// <summary>
    /// 媒体要求的带宽.
    /// </summary>
    [JsonPropertyName("bandwidth")]
    public int BandWidth { get; set; }

    /// <summary>
    /// 媒体格式.
    /// </summary>
    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }

    /// <summary>
    /// 媒体编码.
    /// </summary>
    [JsonPropertyName("codecs")]
    public string? Codecs { get; set; }

    /// <summary>
    /// 媒体宽度.
    /// </summary>
    [JsonPropertyName("width")]
    public int Width { get; set; }

    /// <summary>
    /// 媒体高度.
    /// </summary>
    [JsonPropertyName("height")]
    public int Height { get; set; }

    /// <summary>
    /// 帧率.
    /// </summary>
    [JsonPropertyName("frame_rate")]
    public string? FrameRate { get; set; }

    /// <summary>
    /// 播放比例.
    /// </summary>
    [JsonPropertyName("sar")]
    public string? Scale { get; set; }

    /// <summary>
    /// 分段的基础信息.
    /// </summary>
    [JsonPropertyName("segment_base")]
    public SegmentBase? SegmentBase { get; set; }

    /// <summary>
    /// 编码Id.
    /// </summary>
    [JsonPropertyName("codecid")]
    public int CodecId { get; set; }

    /// <summary>
    /// Gets or sets the optional VIP requirement reported by the API.
    /// </summary>
    [JsonPropertyName("need_vip")]
    public bool? NeedVip { get; set; }
}

/// <summary>
/// 分段基础信息.
/// </summary>
internal sealed class SegmentBase
{
    /// <summary>
    /// 起始位置.
    /// </summary>
    [JsonPropertyName("initialization")]
    public string? Initialization { get; set; }

    /// <summary>
    /// 索引范围.
    /// </summary>
    [JsonPropertyName("index_range")]
    public string? IndexRange { get; set; }
}

/// <summary>
/// 支持的格式.
/// </summary>
internal sealed class VideoFormat
{
    /// <summary>
    /// 清晰度标识.
    /// </summary>
    [JsonPropertyName("quality")]
    public int Quality { get; set; }

    /// <summary>
    /// 格式.
    /// </summary>
    [JsonPropertyName("format")]
    public string Format { get; set; }

    /// <summary>
    /// 显示的说明文本.
    /// </summary>
    [JsonPropertyName("new_description")]
    public string Description { get; set; }

    /// <summary>
    /// 上标.
    /// </summary>
    [JsonPropertyName("superscript")]
    public string Superscript { get; set; }
}

/// <summary>
/// FLV视频条目.
/// </summary>
internal sealed class FlvItem
{
    /// <summary>
    /// 序号.
    /// </summary>
    [JsonPropertyName("order")]
    public int Order { get; set; }

    /// <summary>
    /// 时长.
    /// </summary>
    [JsonPropertyName("length")]
    public int Length { get; set; }

    /// <summary>
    /// 大小.
    /// </summary>
    [JsonPropertyName("size")]
    public int Size { get; set; }

    /// <summary>
    /// 播放地址.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; }

    /// <summary>
    /// 备用地址列表.
    /// </summary>
    [JsonPropertyName("backup_url")]
    public List<string> BackupUrls { get; set; }
}
