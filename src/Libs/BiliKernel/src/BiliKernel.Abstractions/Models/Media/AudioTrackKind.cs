// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Media;

/// <summary>
/// The type of an audio track.
/// </summary>
public enum AudioTrackKind
{
    /// <summary>
    /// A standard DASH audio track.
    /// </summary>
    Standard,

    /// <summary>
    /// A Dolby audio track.
    /// </summary>
    Dolby,

    /// <summary>
    /// A Hi-Res lossless audio track.
    /// </summary>
    HiRes,

    /// <summary>
    /// An AI dubbing language.
    /// </summary>
    AiDubbing,
}
