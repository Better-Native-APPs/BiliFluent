// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.User;

/// <summary>
/// A notification received through a private-message conversation.
/// </summary>
public sealed class ChatNotificationCard
{
    /// <summary>Gets the notification title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets the notification body as display text, not HTML.</summary>
    public string? Text { get; set; }

    /// <summary>Gets the ordered label/value detail rows.</summary>
    public IReadOnlyList<ChatNotificationDetail> Details { get; set; } = [];

    /// <summary>Gets the available actions in server order.</summary>
    public IReadOnlyList<ChatNotificationLink> Links { get; set; } = [];

    /// <summary>Gets the first action's destination, when present.</summary>
    public string? JumpUrl => Links.Count > 0 ? Links[0].Url : null;
}

/// <summary>A notification detail row.</summary>
public sealed class ChatNotificationDetail
{
    /// <summary>Initializes a notification detail row.</summary>
    /// <param name="label">The row's label.</param>
    /// <param name="value">The row's display value.</param>
    public ChatNotificationDetail(string label, string value)
    {
        Label = label;
        Value = value;
    }

    /// <summary>Gets the row's label.</summary>
    public string Label { get; }

    /// <summary>Gets the row's display value.</summary>
    public string Value { get; }
}

/// <summary>A notification action. The consumer decides whether its destination is supported.</summary>
public sealed class ChatNotificationLink
{
    /// <summary>Initializes a notification action.</summary>
    /// <param name="text">The server's action label, or null for the localized default.</param>
    /// <param name="url">The action destination.</param>
    public ChatNotificationLink(string? text, string url)
    {
        Text = text;
        Url = url;
    }

    /// <summary>Gets the server's action label, or null for the localized default.</summary>
    public string? Text { get; }

    /// <summary>Gets the action destination.</summary>
    public string Url { get; }
}
