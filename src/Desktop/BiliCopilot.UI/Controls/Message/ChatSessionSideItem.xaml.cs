// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Controls.Message;

/// <summary>
/// 聊天消息侧边项.
/// </summary>
public sealed partial class ChatSessionSideItem : ChatMessageControlBase
{
    // The preview shows one trimmed line, so a very long message is cut before it reaches text layout.
    private const int MaxPreviewLength = 200;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChatSessionSideItem"/> class.
    /// </summary>
    public ChatSessionSideItem() => InitializeComponent();

    /// <summary>
    /// Flattens a message into a single line for the conversation preview.
    /// Line breaks, tabs and runs of spaces become one space; leading and trailing space is dropped.
    /// </summary>
    /// <param name="text">The last message of the conversation.</param>
    /// <returns>The one-line preview text; empty when there is no message.</returns>
    public static string ToSingleLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(text.Length, MaxPreviewLength));
        var pendingSpace = false;
        foreach (var ch in text)
        {
            // A TextBlock starts a new line at a line break even when wrapping is off.
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
            if (builder.Length >= MaxPreviewLength)
            {
                break;
            }
        }

        // Do not leave half of a surrogate pair (an emoji cut in two) at the end.
        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1]))
        {
            builder.Length--;
        }

        return builder.ToString();
    }
}
