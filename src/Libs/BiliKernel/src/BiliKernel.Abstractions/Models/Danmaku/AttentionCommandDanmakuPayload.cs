// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Danmaku;

/// <summary>
/// The payload of a follow or triple-action prompt.
/// </summary>
public sealed class AttentionCommandDanmakuPayload
{
    /// <summary>Gets or sets the prompt duration in milliseconds.</summary>
    public int? DurationMs { get; set; }

    /// <summary>Gets or sets the original horizontal coordinate from posX.</summary>
    public double? PosX { get; set; }

    /// <summary>Gets or sets the original vertical coordinate from posY.</summary>
    public double? PosY { get; set; }

    /// <summary>Gets or sets the prompt image URL from icon.</summary>
    public string? IconUrl { get; set; }

    /// <summary>Gets or sets the type: 0 for follow, 1 for triple-action, or 2 for both.</summary>
    public int? AttentionType { get; set; }
}
