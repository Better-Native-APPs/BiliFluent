// Copyright (c) BiliFluent. All rights reserved.

using Richasy.BiliKernel.Models.Appearance;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BiliCopilot.UI.Toolkits;

/// <summary>Where a link in a text leads.</summary>
internal enum TextLinkDestinationKind
{
    /// <summary>The space of a user, inside the app.</summary>
    User,

    /// <summary>A search inside the app.</summary>
    Search,

    /// <summary>A video, in the app's player.</summary>
    Video,

    /// <summary>One episode of a series or film, in the app's player.</summary>
    Episode,

    /// <summary>A series or film, in the app's player.</summary>
    Season,

    /// <summary>A live room, in the app's player.</summary>
    Live,

    /// <summary>An article, in the app's reader.</summary>
    Article,

    /// <summary>A web page, in the default browser.</summary>
    Browser,

    /// <summary>
    /// A short link of the site itself. Where it leads is only known once the site is asked, which
    /// happens when the link is opened: usually a place inside the app, otherwise the browser.
    /// </summary>
    ShortLink,
}

/// <summary>
/// Where a link leads. <see cref="Value"/> is the identifier or the search words for places inside the app;
/// <see cref="WebUri"/> is the http(s) address for <see cref="TextLinkDestinationKind.Browser"/> and
/// <see cref="TextLinkDestinationKind.ShortLink"/>.
/// </summary>
internal sealed record TextLinkDestination(TextLinkDestinationKind Kind, string? Value, Uri? WebUri = null)
{
    /// <summary>Gets a value indicating whether the link leaves the app.</summary>
    public bool IsExternal => Kind == TextLinkDestinationKind.Browser;

    /// <summary>Gets a value indicating whether the site has to be asked where the link leads.</summary>
    public bool IsShortLink => Kind == TextLinkDestinationKind.ShortLink;
}

/// <summary>
/// Decides where a link in a text leads. Only http, https and the bilibili:// addresses this app has a
/// page for are accepted; anything else leads nowhere and stays plain text. Nothing is fetched here:
/// a short link of the site is only recognised as one, see <see cref="ShortLinkResolver"/> for the rest.
/// </summary>
internal static partial class TextLinkResolver
{
    /// <summary>
    /// Words the platform picks out of a comment on its own and offers to search for. They are not
    /// something the author linked, so they stay plain text. Set to true to make them open a search.
    /// </summary>
    internal static bool PlatformKeywordsEnabled => false;

    /// <summary>
    /// The short-link hosts of the site, all registered to its operator. This is the whole list of
    /// hosts the app itself ever asks where a link leads; anything else is left to the browser.
    /// </summary>
    private static readonly HashSet<string> ShortLinkHosts = new(StringComparer.Ordinal)
    {
        "b23.tv",
        "bili2233.cn",
        "bili22.cn",
        "bili23.cn",
        "bili33.cn",
    };

    private const string BvAlphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    /// <summary>
    /// Decides where a mention, topic or link described by the server leads. Null when it leads nowhere.
    /// </summary>
    public static TextLinkDestination? Resolve(TextLink link)
    {
        switch (link.Kind)
        {
            case TextLinkKind.Mention:
                return NumberId(link.Target) is { } userId ? new(TextLinkDestinationKind.User, userId) : null;
            case TextLinkKind.Topic:
                return Words(link.Target) is { } topic ? new(TextLinkDestinationKind.Search, topic) : null;
            case TextLinkKind.Keyword:
                return PlatformKeywordsEnabled && Words(link.Target) is { } keyword ? new(TextLinkDestinationKind.Search, keyword) : null;
            case TextLinkKind.Goods:
                // Shops are never opened inside the app, whatever address the server gives.
                return link.Addresses.Select(ResolveBrowser).FirstOrDefault(p => p is not null);
            default:
                // A place inside the app wins over a web page, whichever address names it. Failing that
                // the first usable address counts, so a short link is only asked about when the server
                // did not already say which page it stands for.
                var places = link.Addresses.Select(ResolveAddress).Where(p => p is not null).ToList();
                return places.FirstOrDefault(p => p is { IsExternal: false, IsShortLink: false }) ?? places.FirstOrDefault();
        }
    }

    /// <summary>
    /// Decides where an address leads: a place inside the app when it names one, the browser for any
    /// other http(s) address, nowhere (null) for everything else.
    /// </summary>
    public static TextLinkDestination? ResolveAddress(string? address)
    {
        var value = address?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        // What people write in place of an address: BV1xx411c7mD, av170001, cv123456.
        if (VideoId(value, allowNumber: false) is { } bareVideo)
        {
            return new(TextLinkDestinationKind.Video, bareVideo);
        }

        if (Prefixed(value, "cv") is { } bareArticle)
        {
            return new(TextLinkDestinationKind.Article, bareArticle);
        }

        if (ParseUri(value) is not { } uri)
        {
            return null;
        }

        return uri.Scheme == "bilibili" ? ResolveNative(uri) : ResolveWeb(uri);
    }

    /// <summary>
    /// The browser destination of an address, or null when it is not a plain http(s) address.
    /// </summary>
    public static TextLinkDestination? ResolveBrowser(string? address)
        => ParseUri(address?.Trim()) is { } uri && uri.Scheme != "bilibili" ? new(TextLinkDestinationKind.Browser, null, uri) : null;

    /// <summary>
    /// Whether an address is a short link of the site: http(s) on the usual port, nothing before the
    /// host, and the host exactly one of <see cref="ShortLinkHosts"/>. A look-alike host, a host that
    /// merely ends or starts the same way, or another port is an ordinary web address.
    /// </summary>
    public static bool IsShortLink(Uri uri)
        => uri.IsAbsoluteUri
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && ShortLinkHosts.Contains(uri.IdnHost.ToLowerInvariant());

    private static Uri? ParseUri(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = "https:" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var isWeb = uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

        // "https://bilibili.com@elsewhere.example/" reads as one site and opens another: not a link.
        return (isWeb && string.IsNullOrEmpty(uri.UserInfo)) || uri.Scheme == "bilibili" ? uri : null;
    }

    private static TextLinkDestination ResolveWeb(Uri uri)
    {
        var browser = new TextLinkDestination(TextLinkDestinationKind.Browser, null, uri);
        var host = uri.IdnHost.ToLowerInvariant();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var first = segments.Length > 0 ? segments[0] : string.Empty;
        var second = segments.Length > 1 ? segments[1] : string.Empty;
        var third = segments.Length > 2 ? segments[2] : string.Empty;
        if (IsShortLink(uri))
        {
            return ResolveShortLink(uri, segments);
        }

        switch (host)
        {
            case "www.bilibili.com" or "bilibili.com" or "m.bilibili.com":
                if (Is(first, "video") && VideoId(second, allowNumber: false) is { } video)
                {
                    return new(TextLinkDestinationKind.Video, video);
                }

                if (VideoId(first, allowNumber: false) is { } rootVideo)
                {
                    return new(TextLinkDestinationKind.Video, rootVideo);
                }

                if (Is(first, "bangumi") && Is(second, "play"))
                {
                    if (Prefixed(third, "ep") is { } episode)
                    {
                        return new(TextLinkDestinationKind.Episode, episode);
                    }

                    if (Prefixed(third, "ss") is { } season)
                    {
                        return new(TextLinkDestinationKind.Season, season);
                    }
                }

                if (Is(first, "read") && Prefixed(second, "cv") is { } article)
                {
                    return new(TextLinkDestinationKind.Article, article);
                }

                if (Is(first, "read") && Is(second, "mobile") && (NumberId(third) ?? NumberId(Query(uri, "id"))) is { } mobileArticle)
                {
                    return new(TextLinkDestinationKind.Article, mobileArticle);
                }

                if (Is(first, "space") && NumberId(second) is { } mobileUser)
                {
                    return new(TextLinkDestinationKind.User, mobileUser);
                }

                return browser;
            case "space.bilibili.com":
                return NumberId(first) is { } user ? new(TextLinkDestinationKind.User, user) : browser;
            case "live.bilibili.com":
                return (NumberId(first) ?? (Is(first, "h5") || Is(first, "blanc") ? NumberId(second) : null)) is { } room
                    ? new(TextLinkDestinationKind.Live, room)
                    : browser;
            case "search.bilibili.com":
                return Words(Query(uri, "keyword")) is { } keyword ? new(TextLinkDestinationKind.Search, keyword) : browser;
            default:
                return browser;
        }
    }

    // Short links that spell out what they lead to need no asking. Only the spellings the site was
    // seen to redirect to the very same page are read this way: /BV1…, /av…, /ep…, /ss…, written
    // exactly so. Everything else (random codes, /cv…, longer paths) waits until it is opened.
    private static TextLinkDestination ResolveShortLink(Uri uri, string[] segments)
    {
        if (segments.Length == 1)
        {
            var name = segments[0];
            if ((name.StartsWith("BV1", StringComparison.Ordinal) || name.StartsWith("av", StringComparison.Ordinal))
                && VideoId(name, allowNumber: false) is { } video)
            {
                return new(TextLinkDestinationKind.Video, video);
            }

            if (name.StartsWith("ep", StringComparison.Ordinal) && Prefixed(name, "ep") is { } episode)
            {
                return new(TextLinkDestinationKind.Episode, episode);
            }

            if (name.StartsWith("ss", StringComparison.Ordinal) && Prefixed(name, "ss") is { } season)
            {
                return new(TextLinkDestinationKind.Season, season);
            }
        }

        return new(TextLinkDestinationKind.ShortLink, null, uri);
    }

    // The app's own addresses, only the ones there is a page for. The rest lead nowhere on purpose:
    // handing them to the system would start whichever app has claimed the scheme.
    private static TextLinkDestination? ResolveNative(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var first = segments.Length > 0 ? segments[0] : string.Empty;
        var second = segments.Length > 1 ? segments[1] : string.Empty;
        return uri.Host.ToLowerInvariant() switch
        {
            "video" when VideoId(first, allowNumber: true) is { } video => new(TextLinkDestinationKind.Video, video),
            "article" when NumberId(first) is { } article => new(TextLinkDestinationKind.Article, article),
            "live" when NumberId(first) is { } room => new(TextLinkDestinationKind.Live, room),
            "space" when NumberId(first) is { } user => new(TextLinkDestinationKind.User, user),
            "bangumi" when Is(first, "season") && NumberId(second) is { } season => new(TextLinkDestinationKind.Season, season),
            "search" when Words(Query(uri, "keyword")) is { } keyword => new(TextLinkDestinationKind.Search, keyword),
            _ => null,
        };
    }

    private static bool Is(string value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static string? Prefixed(string value, string prefix)
        => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? NumberId(value[prefix.Length..]) : null;

    // The video's av number, which is what the player is opened with.
    private static string? VideoId(string value, bool allowNumber)
    {
        if (Prefixed(value, "av") is { } aid)
        {
            return aid;
        }

        if (value.Length == 12 && value.StartsWith("BV1", StringComparison.Ordinal) && value.Skip(3).All(BvAlphabet.Contains))
        {
            return NumberId(IdToolkit.Bv2Av(value).ToString(CultureInfo.InvariantCulture));
        }

        return allowNumber ? NumberId(value) : null;
    }

    private static string? NumberId(string? value)
        => value is { Length: > 0 and <= 19 } && value.All(c => c is >= '0' and <= '9')
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id.ToString(CultureInfo.InvariantCulture)
            : null;

    private static string? Words(string? value)
    {
        var words = value is null ? string.Empty : WhitespaceRegex().Replace(value, " ").Trim();
        return words.Length is > 0 and <= 100 ? words : null;
    }

    private static string? Query(Uri uri, string name)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(pair[0], name, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return Uri.UnescapeDataString(pair[1].Replace('+', ' '));
                }
                catch (UriFormatException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
