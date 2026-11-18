// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Danmaku;

/// <summary>
/// The optional fields of a rating card payload.
/// </summary>
public sealed class GradeCommandDanmakuPayload
{
    /// <summary>Gets or sets the rating identifier from grade_id as a string.</summary>
    public string? GradeId { get; set; }

    /// <summary>Gets or sets the rating prompt from msg.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets the card duration in milliseconds.</summary>
    public int? DurationMs { get; set; }

    /// <summary>Gets or sets the participant count from count.</summary>
    public long? Count { get; set; }

    /// <summary>Gets or sets the server-supplied average from avg_score.</summary>
    public double? AverageScore { get; set; }

    /// <summary>Gets or sets the original current-user score from mid_score.</summary>
    public int? MyScore { get; set; }
}
