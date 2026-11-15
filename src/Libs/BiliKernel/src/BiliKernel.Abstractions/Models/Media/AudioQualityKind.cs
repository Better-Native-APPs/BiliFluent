// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Media;

/// <summary>
/// The type of an audio quality.
/// </summary>
public enum AudioQualityKind
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

}
