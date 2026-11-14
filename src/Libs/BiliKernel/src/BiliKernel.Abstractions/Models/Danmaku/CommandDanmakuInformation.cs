// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Danmaku;

/// <summary>
/// A timed interactive danmaku and its optional structured payload.
/// </summary>
public sealed class CommandDanmakuInformation
{
    /// <summary>Gets or sets the danmaku identifier, preferring its string representation.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the video content identifier.</summary>
    public string Cid { get; set; } = string.Empty;

    /// <summary>Gets or sets the sender's user identifier, when provided.</summary>
    public string? SenderId { get; set; }

    /// <summary>Gets or sets the recognized command kind.</summary>
    public CommandDanmakuKind Kind { get; set; }

    /// <summary>Gets or sets the original command, including unsupported values.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Gets or sets the original danmaku content.</summary>
    public string? Content { get; set; }

    /// <summary>Gets or sets the appearance time in milliseconds.</summary>
    public int ProgressMs { get; set; }

    /// <summary>Gets or sets the original extra JSON, even when parsing fails.</summary>
    public string? ExtraRaw { get; set; }

    /// <summary>Gets or sets the uploader payload, when it can be parsed.</summary>
    public UpCommandDanmakuPayload? Up { get; set; }

    /// <summary>Gets or sets the related video payload, when it can be parsed.</summary>
    public LinkCommandDanmakuPayload? Link { get; set; }

    /// <summary>Gets or sets the follow or triple-action payload, when it can be parsed.</summary>
    public AttentionCommandDanmakuPayload? Attention { get; set; }

    /// <summary>Gets or sets the vote payload, when it can be parsed.</summary>
    public VoteCommandDanmakuPayload? Vote { get; set; }

    /// <summary>Gets or sets the rating payload, when it can be parsed.</summary>
    public GradeCommandDanmakuPayload? Grade { get; set; }
}
