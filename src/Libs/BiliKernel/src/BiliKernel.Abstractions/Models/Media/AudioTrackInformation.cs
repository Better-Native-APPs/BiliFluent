// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Media;

/// <summary>
/// A selectable audio track or an AI dubbing language.
/// </summary>
public sealed class AudioTrackInformation(
    string id,
    string? name,
    AudioTrackKind kind,
    string? languageCode = default,
    bool isAI = false,
    bool needVip = false,
    DashSegmentInformation? segment = default)
{
    /// <summary>
    /// Gets the stable track identifier.
    /// </summary>
    public string Id { get; } = id;

    /// <summary>
    /// Gets the display name supplied by the API, if any.
    /// </summary>
    public string? Name { get; } = name;

    /// <summary>
    /// Gets the track type.
    /// </summary>
    public AudioTrackKind Kind { get; } = kind;

    /// <summary>
    /// Gets the language code supplied by the API, if any.
    /// </summary>
    public string? LanguageCode { get; } = languageCode;

    /// <summary>
    /// Gets a value indicating whether this track uses AI dubbing.
    /// </summary>
    public bool IsAI { get; } = isAI;

    /// <summary>
    /// Gets a value indicating whether the API requires VIP access.
    /// </summary>
    public bool NeedVip { get; } = needVip;

    /// <summary>
    /// Gets the audio stream, or null when the dubbing language has not been requested.
    /// </summary>
    public DashSegmentInformation? Segment { get; } = segment;
}
