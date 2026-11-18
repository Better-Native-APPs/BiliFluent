// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Appearance;

/// <summary>
/// Something in a text that the server describes as more than plain text: a mentioned user,
/// a topic, or a link. It says what the text points to; opening it is up to the app.
/// </summary>
public sealed class TextLink
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TextLink"/> class.
    /// </summary>
    /// <param name="kind">What the text points to.</param>
    /// <param name="text">The piece of the text this stands for, exactly as it is written there.</param>
    /// <param name="target">The user identifier for a mention, the word to search for a topic or keyword.</param>
    /// <param name="addresses">The addresses the server gives for a link, the most specific first.</param>
    public TextLink(TextLinkKind kind, string text, string? target = default, IReadOnlyList<string>? addresses = default)
    {
        Kind = kind;
        Text = text;
        Target = target;
        Addresses = addresses ?? Array.Empty<string>();
    }

    /// <summary>
    /// Gets what the text points to.
    /// </summary>
    public TextLinkKind Kind { get; }

    /// <summary>
    /// Gets the piece of the text this stands for, exactly as it is written there: "@name", "#topic#",
    /// an address, a video number. Empty when the link belongs to the text as a whole.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Gets the user identifier for a mention, or the word to search for a topic or keyword. Null for links.
    /// </summary>
    public string? Target { get; }

    /// <summary>
    /// Gets the addresses the server gives for a link, the most specific first, exactly as sent.
    /// They are not checked: they can use any scheme, including ones of other apps.
    /// </summary>
    public IReadOnlyList<string> Addresses { get; }

    /// <summary>
    /// Gets or sets the title to show in place of <see cref="Text"/>, such as the title of a linked video.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the small picture the server sends to go with the title.
    /// </summary>
    public BiliImage? Icon { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="Icon"/> goes after the title instead of before it.
    /// </summary>
    public bool IsIconTrailing { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether only the first place <see cref="Text"/> appears is meant.
    /// </summary>
    public bool IsFirstOccurrenceOnly { get; set; }
}

/// <summary>
/// What a <see cref="TextLink"/> points to.
/// </summary>
public enum TextLinkKind
{
    /// <summary>A user mentioned with @.</summary>
    Mention,

    /// <summary>A topic written as #topic#.</summary>
    Topic,

    /// <summary>A link the author put in the text: a video, an article, a web address.</summary>
    Link,

    /// <summary>A paid link: something for sale, or another promotion placed in the text.</summary>
    Goods,

    /// <summary>A word the platform picked out on its own and offers to search for.</summary>
    Keyword,

    /// <summary>The full note a comment was written as.</summary>
    Note,
}
