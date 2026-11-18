// Copyright (c) BiliFluent. All rights reserved.

using Richasy.BiliKernel.Models.Appearance;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BiliCopilot.UI.Toolkits;

/// <summary>What a piece of a text is shown as.</summary>
internal enum TextSegmentKind
{
    /// <summary>Plain text.</summary>
    Text,

    /// <summary>An emote picture; the segment text is its key, brackets included.</summary>
    Emote,

    /// <summary>Something that can be opened.</summary>
    Link,
}

/// <summary>
/// One piece of a text, in reading order. For a link, <paramref name="Link"/> is what the server said
/// about it (null for an address the author simply typed) and <paramref name="Destination"/> is where it leads.
/// </summary>
internal sealed record TextSegment(TextSegmentKind Kind, string Text, TextLink? Link = null, TextLinkDestination? Destination = null)
{
    /// <summary>
    /// Gets a value indicating whether the link belongs to the text as a whole instead of a piece of it,
    /// such as the note a comment was written as. It comes after the text and has no text of its own.
    /// </summary>
    public bool IsAttached => Kind == TextSegmentKind.Link && Text.Length == 0;
}

/// <summary>
/// Cuts a text into plain pieces, emotes and links. Pure text work: nothing is fetched or opened.
/// </summary>
internal static partial class TextLinkParser
{
    /// <summary>The longest title or address shown in a link; the rest becomes an ellipsis.</summary>
    internal const int MaxLinkLength = 36;

    /// <summary>
    /// Cuts <paramref name="text"/> into pieces. With <paramref name="links"/> null the text has no link
    /// information at all and only emotes are picked out; otherwise mentions, topics, the links the
    /// server describes and plain http(s) addresses become links, as far as they lead somewhere.
    /// </summary>
    public static IReadOnlyList<TextSegment> Parse(string? text, IReadOnlyDictionary<string, BiliImage>? emotes, IReadOnlyList<TextLink>? links)
    {
        text ??= string.Empty;
        var found = new List<Found>();

        if (links is not null)
        {
            foreach (var link in links)
            {
                if (link.Text.Length == 0 || TextLinkResolver.Resolve(link) is not { } destination)
                {
                    continue;
                }

                var segment = new TextSegment(TextSegmentKind.Link, link.Text, link, destination);
                for (var index = text.IndexOf(link.Text, StringComparison.Ordinal);
                    index >= 0;
                    index = link.IsFirstOccurrenceOnly ? -1 : text.IndexOf(link.Text, index + link.Text.Length, StringComparison.Ordinal))
                {
                    found.Add(new(index, link.Text.Length, 0, segment));
                }
            }
        }

        if (emotes is { Count: > 0 })
        {
            foreach (Match match in EmoteRegex().Matches(text))
            {
                if (emotes.TryGetValue(match.Value, out var image) && image?.Uri is not null)
                {
                    found.Add(new(match.Index, match.Length, 1, new(TextSegmentKind.Emote, match.Value)));
                }
            }
        }

        if (links is not null)
        {
            foreach (Match match in AddressRegex().Matches(text))
            {
                var address = TrimAddressEnd(match.Value);
                if (TextLinkResolver.ResolveAddress(address) is { } destination)
                {
                    found.Add(new(match.Index, address.Length, 2, new(TextSegmentKind.Link, address, null, destination)));
                }
            }
        }

        // Earliest first. At the same place what the server described wins, then the longer piece
        // ("@ab" and "@abc" both known: the text "@abc" is the second one).
        found.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start)
            : a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank)
            : b.Length.CompareTo(a.Length));

        var result = new List<TextSegment>();
        var position = 0;
        foreach (var item in found)
        {
            if (item.Start < position)
            {
                continue;
            }

            AddText(result, text[position..item.Start]);
            result.Add(item.Segment);
            position = item.Start + item.Length;
        }

        AddText(result, text[position..]);

        if (links is not null)
        {
            foreach (var link in links)
            {
                if (link.Text.Length == 0 && TextLinkResolver.Resolve(link) is { } destination)
                {
                    result.Add(new(TextSegmentKind.Link, string.Empty, link, destination));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The words a link is shown as: the title the server gives when there is one, otherwise what the
    /// author wrote. Long ones are cut to <see cref="MaxLinkLength"/> characters, so that a link stays
    /// a short run of the sentence it sits in and wraps with it.
    /// </summary>
    public static string GetDisplayText(TextSegment segment)
    {
        var text = !string.IsNullOrWhiteSpace(segment.Link?.Title) ? Flatten(segment.Link!.Title!) : segment.Text;
        if (segment.Link is null or { Kind: TextLinkKind.Link or TextLinkKind.Goods or TextLinkKind.Note })
        {
            return Shorten(text, MaxLinkLength);
        }

        return text;
    }

    /// <summary>
    /// The full words behind <see cref="GetDisplayText"/> when that had to be cut, otherwise null.
    /// </summary>
    public static string? GetFullText(TextSegment segment)
    {
        var text = !string.IsNullOrWhiteSpace(segment.Link?.Title) ? Flatten(segment.Link!.Title!) : segment.Text;
        return text != GetDisplayText(segment) ? text : null;
    }

    /// <summary>
    /// Cuts a text to at most <paramref name="length"/> characters as a reader counts them, ending in an ellipsis.
    /// </summary>
    internal static string Shorten(string text, int length)
    {
        var info = new StringInfo(text);
        return info.LengthInTextElements <= length ? text : info.SubstringByTextElements(0, length - 1).TrimEnd() + "…";
    }

    private static string Flatten(string text) => WhitespaceRegex().Replace(text, " ").Trim();

    private static void AddText(List<TextSegment> result, string text)
    {
        if (text.Length > 0)
        {
            result.Add(new(TextSegmentKind.Text, text));
        }
    }

    // Sentence punctuation right after an address belongs to the sentence, not to the address.
    private static string TrimAddressEnd(string text)
    {
        var end = text.Length;
        while (end > 0)
        {
            var last = text[end - 1];
            if (".,;:!?'*~".Contains(last)
                || (last == ')' && text[..end].Count(c => c == ')') > text[..end].Count(c => c == '(')))
            {
                end--;
            }
            else
            {
                break;
            }
        }

        return text[..end];
    }

    [GeneratedRegex(@"\[.*?\]")]
    private static partial Regex EmoteRegex();

    // Only the characters an address is made of: text in other scripts often follows one without a space.
    // Square brackets are left out, an emote such as [doge] can come right after an address.
    [GeneratedRegex(@"(?<![A-Za-z0-9])https?://[A-Za-z0-9\-._~:/?#@!$&'()*+,;=%]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddressRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private readonly record struct Found(int Start, int Length, int Rank, TextSegment Segment);
}
