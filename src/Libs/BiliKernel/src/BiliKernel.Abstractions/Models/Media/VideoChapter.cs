// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Media;

/// <summary>
/// A chapter supplied by the video player API.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="VideoChapter"/> class.
/// </remarks>
public sealed class VideoChapter(double start, double end, string title, string? imageUrl, int type)
{
    /// <summary>
    /// Inclusive start position, in seconds.
    /// </summary>
    public double Start { get; } = start;

    /// <summary>
    /// Exclusive end position, in seconds.
    /// </summary>
    public double End { get; } = end;

    /// <summary>
    /// Chapter title.
    /// </summary>
    public string Title { get; } = title;

    /// <summary>
    /// Chapter preview image URL, when available.
    /// </summary>
    public string? ImageUrl { get; } = imageUrl;

    /// <summary>
    /// Unmodified chapter type from the API.
    /// </summary>
    public int Type { get; } = type;
}
