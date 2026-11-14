// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Danmaku;

/// <summary>
/// The optional fields of a voting card payload.
/// </summary>
public sealed class VoteCommandDanmakuPayload
{
    /// <summary>Gets or sets the vote identifier from vote_id as a string.</summary>
    public string? VoteId { get; set; }

    /// <summary>Gets or sets the question text.</summary>
    public string? Question { get; set; }

    /// <summary>Gets or sets the supplied options without inventing missing indices.</summary>
    public IReadOnlyList<VoteCommandDanmakuOption> Options { get; set; } = Array.Empty<VoteCommandDanmakuOption>();

    /// <summary>Gets or sets the original current-user selection from my_vote.</summary>
    public int? MyVote { get; set; }

    /// <summary>Gets or sets the card duration in milliseconds.</summary>
    public int? DurationMs { get; set; }
}
