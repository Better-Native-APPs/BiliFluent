// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Toolkits;
using Humanizer;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Models.User;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// 对话消息条目视图模型.
/// </summary>
public sealed partial class ChatMessageItemViewModel : ViewModelBase<ChatMessage>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ChatMessageItemViewModel"/> class.
    /// </summary>
    public ChatMessageItemViewModel(ChatMessage data, bool isSystemSession = false)
        : base(data)
    {
        RelativeTime = data.Time.Humanize();
        ActualTime = (isSystemSession ? data.Time?.ToLocalTime() : data.Time)?.ToString("yyyy/MM/dd HH:mm:ss");
        Content = data.Content;
        IsSystemSession = isSystemSession;
        IsMe = !isSystemSession && data.SenderId == this.Get<IBiliTokenResolver>().GetToken().UserId.ToString();
        IsWithdrawn = !isSystemSession && data.Type == Richasy.BiliKernel.Models.ChatMessageType.Withdrawn;
        if (!isSystemSession && data.Type == Richasy.BiliKernel.Models.ChatMessageType.Unknown)
        {
            Content = new Richasy.BiliKernel.Models.Appearance.EmoteText(ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.NotSupportContent), default);
        }

        NotificationTitle = data.NotificationCard?.Title ?? string.Empty;
        SystemText = data.NotificationCard is { } card ? card.Text ?? string.Empty
            : (string.IsNullOrEmpty(data.Content?.Text) ? data.RawContent : data.Content.Text) ?? string.Empty;
        if (data.NotificationCard is null && string.IsNullOrEmpty(SystemText))
            SystemText = ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.NotSupportContent);
        NotificationDetails = data.NotificationCard?.Details ?? [];
        NotificationLinks = data.NotificationCard?.Links.Select(link => new ChatNotificationLinkViewModel(link))
            .Where(link => link.CanOpen).ToList() ?? [];
    }
}
