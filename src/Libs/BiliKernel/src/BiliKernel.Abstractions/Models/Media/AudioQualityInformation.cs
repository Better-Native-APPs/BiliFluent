// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Media;

/// <summary>
/// A selectable audio quality of the loaded audio language: a standard bitrate, Dolby, or Hi-Res lossless.
/// </summary>
public sealed class AudioQualityInformation(
    string id,
    AudioQualityKind kind,
    bool needVip = false,
    DashSegmentInformation? segment = default)
{
    /// <summary>
    /// Gets the stable quality identifier, such as "std:30280", "dolby:30250" or "hires:30251".
    /// </summary>
    public string Id { get; } = id;

    /// <summary>
    /// Gets the audio quality type.
    /// </summary>
    public AudioQualityKind Kind { get; } = kind;

    /// <summary>
    /// Gets a value indicating whether the API requires VIP access.
    /// </summary>
    public bool NeedVip { get; } = needVip;

    /// <summary>
    /// Gets the audio stream.
    /// </summary>
    public DashSegmentInformation? Segment { get; } = segment;

    /// <summary>
    /// Gets a value indicating whether the stream comes with an address. Without one it cannot be chosen.
    /// </summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(Segment?.BaseUrl);
}
