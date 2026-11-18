// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Media;

/// <summary>
/// Dash 播放媒体信息.
/// </summary>
public sealed class DashMediaInformation(
    IList<DashSegmentInformation>? videoSegments,
    IList<DashSegmentInformation>? audioSegments,
    IList<PlayerFormatInformation>? formats,
    int duration = 0,
    IList<AudioQualityInformation>? audioQualities = default,
    string? currentLanguage = default,
    IList<AudioLanguageInformation>? audioLanguages = default,
    string? audioLanguageGroupTitle = default)
{
    /// <summary>
    /// 视频时长.
    /// </summary>
    public int Duration { get; } = duration;

    /// <summary>
    /// 不同清晰度的视频列表.
    /// </summary>
    public IList<DashSegmentInformation>? Videos { get; } = videoSegments;

    /// <summary>
    /// 不同码率的音频列表.
    /// </summary>
    public IList<DashSegmentInformation>? Audios { get; } = audioSegments;

    /// <summary>
    /// 格式列表.
    /// </summary>
    public IList<PlayerFormatInformation>? Formats { get; } = formats;

    /// <summary>
    /// Gets the audio qualities of the current language.
    /// </summary>
    public IList<AudioQualityInformation>? AudioQualities { get; } = audioQualities;

    /// <summary>
    /// Gets the available audio languages, with the original audio first.
    /// </summary>
    public IList<AudioLanguageInformation>? AudioLanguages { get; } = audioLanguages;

    /// <summary>
    /// Gets the title the API gives to its list of dubbing languages, if any.
    /// </summary>
    public string? AudioLanguageGroupTitle { get; } = audioLanguageGroupTitle;

    /// <summary>
    /// Gets the dubbing language the response serves, as reported by the API. Empty for the original audio.
    /// </summary>
    public string? CurrentLanguage { get; } = currentLanguage;
}
