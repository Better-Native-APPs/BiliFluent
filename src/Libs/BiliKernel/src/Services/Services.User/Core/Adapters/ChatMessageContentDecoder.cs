// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using Richasy.BiliKernel.Models;
using Richasy.BiliKernel.Models.Appearance;
using Richasy.BiliKernel.Models.User;
using System.Text.Json;

namespace Richasy.BiliKernel.Services.User.Core;

/// <summary>Decodes documented message bodies without discarding unknown content.</summary>
internal static class ChatMessageContentDecoder
{
    internal static ChatMessage Decode(BiliChatMsg source, Dictionary<string, BiliImage>? emotes)
    {
        var raw = source.Content ?? string.Empty;
        var message = new ChatMessage
        {
            Key = source.Key,
            SenderId = source.SenderUid.ToString(),
            Time = FromTimestamp(source.Timestamp),
            MessageType = source.Type,
            RawContent = source.Content,
            PreviewText = raw,
            Type = ChatMessageType.Unknown,
            Content = new EmoteText(raw, emotes),
        };

        if (source.Type == 5)
        {
            message.Type = ChatMessageType.Withdrawn;
            return message;
        }

        if (string.IsNullOrEmpty(raw)) return message;
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            message.PreviewText = FirstText(ReadString(root, "title"), ReadString(root, "text"), ReadString(root, "content")) ?? raw;
            if (source.Type == 1 && ReadString(root, "content") is string text)
            {
                message.Type = ChatMessageType.Text;
                message.Content = new EmoteText(text, emotes);
                message.PreviewText = text;
            }
            else if (source.Type == 2 && ReadString(root, "url") is string url
                && TryImage(url) is BiliImage image)
            {
                message.Type = ChatMessageType.Image;
                message.Content = new EmoteText(string.Empty, emotes, [image]);
            }
            else if (source.Type == 10 && ReadNotification(root) is ChatNotificationCard card)
            {
                message.NotificationCard = card;
                message.Content = new EmoteText(card.Text ?? string.Empty, emotes);
                message.PreviewText = FirstText(card.Title, card.Text) ?? raw;
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // The original body remains the display fallback; never include message content in logs.
        }

        return message;
    }

    internal static DateTimeOffset? FromTimestamp(long timestamp)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(timestamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    internal static string? ReadUserPreview(string? content)
    {
        if (string.IsNullOrEmpty(content)) return content;
        try
        {
            using var document = JsonDocument.Parse(content);
            return ReadString(document.RootElement, "content") ?? string.Empty;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return content;
        }
    }

    internal static BiliImage? TryImage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? new BiliImage(uri) : null;
    }

    private static ChatNotificationCard? ReadNotification(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !IsOptionalString(root, "title") || !IsOptionalString(root, "text")) return null;
        var details = new List<ChatNotificationDetail>();
        if (root.TryGetProperty("modules", out var modules) && modules.ValueKind != JsonValueKind.Null)
        {
            if (modules.ValueKind != JsonValueKind.Array) return null;
            foreach (var module in modules.EnumerateArray())
            {
                if (module.ValueKind != JsonValueKind.Object
                    || !IsOptionalString(module, "title") || !IsOptionalString(module, "detail")) return null;
                var label = ReadString(module, "title") ?? string.Empty;
                var value = ReadString(module, "detail") ?? string.Empty;
                if (label.Length != 0 || value.Length != 0) details.Add(new(label, value));
            }
        }

        var links = new List<ChatNotificationLink>();
        foreach (var suffix in new[] { string.Empty, "_2", "_3" })
        {
            var config = root.TryGetProperty("jump_uri" + suffix + "_config", out var item) ? item : default;
            // The desktop follows the documented web -> all-platform -> legacy priority.
            var url = FirstText(ReadString(config, "web_uri"), ReadString(config, "all_uri"), ReadString(root, "jump_uri" + suffix));
            var text = FirstText(ReadString(config, "text"), ReadString(root, "jump_text" + suffix));
            if (url is not null) links.Add(new(text, url));
        }

        var title = ReadString(root, "title");
        var body = ReadString(root, "text");
        return FirstText(title, body) is null && details.Count == 0 && links.Count == 0 ? null : new ChatNotificationCard
        {
            Title = title,
            Text = body,
            Details = details.AsReadOnly(),
            Links = links.AsReadOnly(),
        };
    }

    private static bool IsOptionalString(JsonElement root, string name)
        => !root.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.String;

    private static string? ReadString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? FirstText(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
