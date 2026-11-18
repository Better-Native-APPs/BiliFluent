// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Danmaku;

/// <summary>
/// The payload of a related video card.
/// </summary>
public sealed class LinkCommandDanmakuPayload
{
    /// <summary>Gets or sets the related video's aid as a string.</summary>
    public string? Aid { get; set; }

    /// <summary>Gets or sets the related video's bvid.</summary>
    public string? Bvid { get; set; }

    /// <summary>Gets or sets the related video's title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the card image URL from icon.</summary>
    public string? IconUrl { get; set; }
}
