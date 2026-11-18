// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using Richasy.BiliKernel.Adapters;
using Richasy.BiliKernel.Models.Appearance;

namespace Richasy.BiliKernel.Services.Comment.Core.Adapters;

internal static class CommentLinkAdapter
{
    /// <summary>
    /// Reads what the comment says about its own text: the users it mentions, its topics and its links.
    /// Every map is keyed by the piece of the message it describes.
    /// </summary>
    public static IReadOnlyList<TextLink> ToTextLinks(this Bilibili.Main.Community.Reply.V1.Content content)
    {
        var links = new List<TextLink>();

        // Mentions: the message has "@name", both maps go from the name to the user.
        var mentioned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in content.AtNameToMid)
        {
            AddMention(links, mentioned, pair.Key, pair.Value);
        }

        foreach (var pair in content.Members)
        {
            AddMention(links, mentioned, pair.Key, pair.Value?.Mid ?? 0);
        }

        // Topics: the message has "#name#". The server's own link for one is a search for the name.
        foreach (var pair in content.Topics)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                links.Add(new TextLink(TextLinkKind.Topic, "#" + pair.Key + "#", pair.Key.Trim()));
            }
        }

        // Links: keyed by what the author wrote, an address or a number such as BV1xx411c7mD.
        foreach (var pair in content.Urls)
        {
            var key = pair.Key;
            var url = pair.Value;
            if (string.IsNullOrWhiteSpace(key) || url is null)
            {
                continue;
            }

            var title = string.IsNullOrWhiteSpace(url.Title) ? null : url.Title.Trim();
            if (url.Extra is { IsWordSearch: true })
            {
                links.Add(new TextLink(TextLinkKind.Keyword, key, title ?? key.Trim())
                {
                    IsFirstOccurrenceOnly = url.MatchOnce,
                });
                continue;
            }

            var addresses = new List<string>();
            AddAddress(addresses, url.AppUrlSchema);
            AddAddress(addresses, url.PcUrl);
            AddAddress(addresses, key);
            links.Add(new TextLink(IsCommercial(url.Extra) ? TextLinkKind.Goods : TextLinkKind.Link, key, addresses: addresses)
            {
                Title = title == key ? null : title,
                Icon = ToIcon(url.PrefixIcon),
                IsIconTrailing = url.IconPosition == Bilibili.Main.Community.Reply.V1.Url.Types.IconPosition.Suffix,
                IsFirstOccurrenceOnly = url.MatchOnce,
            });
        }

        // A comment written as a note links to the note itself, not from any piece of the message.
        if (content.RichText?.Note is { } note && !string.IsNullOrWhiteSpace(note.ClickUrl))
        {
            links.Add(new TextLink(TextLinkKind.Note, string.Empty, addresses: [note.ClickUrl.Trim()]));
        }

        return links.AsReadOnly();
    }

    private static void AddMention(List<TextLink> links, HashSet<string> mentioned, string name, long userId)
    {
        if (userId > 0 && !string.IsNullOrWhiteSpace(name) && mentioned.Add(name))
        {
            links.Add(new TextLink(TextLinkKind.Mention, "@" + name, userId.ToString(CultureInfo.InvariantCulture)));
        }
    }

    // Shop items carry an item id; other paid placements carry the commercial control fields.
    private static bool IsCommercial(Bilibili.Main.Community.Reply.V1.Url.Types.Extra? extra)
        => extra is not null && (extra.GoodsItemId > 0 || extra.GoodsCmControl > 0 || !string.IsNullOrEmpty(extra.GoodsPrefetchedCache));

    private static void AddAddress(List<string> addresses, string? address)
    {
        var value = address?.Trim();
        if (!string.IsNullOrEmpty(value) && !addresses.Contains(value!))
        {
            addresses.Add(value!);
        }
    }

    private static BiliImage? ToIcon(string? url)
        => !string.IsNullOrWhiteSpace(url) && url!.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url.ToImage() : null;
}
