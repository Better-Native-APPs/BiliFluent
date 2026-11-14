// Copyright (c) Bili Copilot. All rights reserved.

using Richasy.BiliKernel.Models.Appearance;
using Richasy.BiliKernel.Models.User;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// 对话消息条目视图模型.
/// </summary>
public sealed partial class ChatMessageItemViewModel
{
    /// <summary>
    /// 相对时间.
    /// </summary>
    public string RelativeTime { get; init; }

    /// <summary>
    /// 实际时间.
    /// </summary>
    public string ActualTime { get; init; }

    /// <summary>
    /// 是否是我.
    /// </summary>
    public bool IsMe { get; init; }

    /// <summary>
    /// 是否已撤回.
    /// </summary>
    public bool IsWithdrawn { get; init; }

    /// <summary>
    /// 消息内容.
    /// </summary>
    public EmoteText Content { get; init; }

    /// <summary>Gets whether the item belongs to a read-only system conversation.</summary>
    public bool IsSystemSession { get; init; }

    /// <summary>Gets whether an ordinary private-message bubble should be displayed.</summary>
    public bool ShowChatMessage => !IsSystemSession && !IsWithdrawn;

    /// <summary>Gets the decoded notification title.</summary>
    public string NotificationTitle { get; init; }

    /// <summary>Gets the decoded body or unmodified unsupported body as plain text.</summary>
    public string SystemText { get; init; }

    /// <summary>Gets the ordered notification detail rows.</summary>
    public IReadOnlyList<ChatNotificationDetail> NotificationDetails { get; init; }

    /// <summary>Gets the supported notification actions in server order.</summary>
    public IReadOnlyList<ChatNotificationLinkViewModel> NotificationLinks { get; init; }

    public bool HasNotificationTitle => !string.IsNullOrEmpty(NotificationTitle);
    public bool HasSystemText => !string.IsNullOrEmpty(SystemText);
    public bool HasNotificationDetails => NotificationDetails.Count > 0;
    public bool HasNotificationLinks => NotificationLinks.Count > 0;
}
