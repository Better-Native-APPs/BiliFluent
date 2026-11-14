// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Danmaku;

/// <summary>
/// A server-supplied option in a voting card.
/// </summary>
public sealed class VoteCommandDanmakuOption
{
    /// <summary>Gets or sets the original option index from idx.</summary>
    public int? Index { get; set; }

    /// <summary>Gets or sets the option text from desc.</summary>
    public string? Text { get; set; }

    /// <summary>Gets or sets the vote count from cnt, when supplied.</summary>
    public long? Count { get; set; }
}
