// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using Richasy.BiliKernel.Models.Appearance;

namespace Richasy.BiliKernel.Models.User;

/// <summary>
/// 聊天消息.
/// </summary>
public sealed class ChatMessage
{
    /// <summary>
    /// 消息内容.
    /// </summary>
    public EmoteText? Content { get; set; }

    /// <summary>
    /// 消息时间.
    /// </summary>
    public DateTimeOffset? Time { get; set; }

    /// <summary>
    /// 消息 Id.
    /// </summary>
    public long Key { get; set; }

    /// <summary>
    /// 发送者 Id.
    /// </summary>
    public string SenderId { get; set; }

    /// <summary>
    /// 消息类型.
    /// </summary>
    public ChatMessageType Type { get; set; }

    /// <summary>
    /// Gets or sets the original protocol message type, including unsupported values.
    /// </summary>
    public int MessageType { get; set; }

    /// <summary>
    /// Gets or sets the unmodified message body for unsupported or malformed content.
    /// </summary>
    public string? RawContent { get; set; }

    /// <summary>
    /// Gets or sets the plain-text conversation preview.
    /// </summary>
    public string? PreviewText { get; set; }

    /// <summary>
    /// Gets or sets a supported notification card, or null when the body cannot be decoded.
    /// </summary>
    public ChatNotificationCard? NotificationCard { get; set; }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ChatMessage message && Key == message.Key;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Key);
}
