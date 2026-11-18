// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Extensions;
using System.Diagnostics;

namespace BiliCopilot.UI.Toolkits;

/// <summary>How asking the site where a short link leads ended.</summary>
internal enum ShortLinkOutcome
{
    /// <summary>It leads to a place inside the app.</summary>
    InApp,

    /// <summary>It leads to a page the app has no place for.</summary>
    WebPage,

    /// <summary>The site answered without saying where the link leads.</summary>
    NoRedirect,

    /// <summary>The link kept pointing at further short links.</summary>
    TooManyHops,

    /// <summary>No answer came in time.</summary>
    Timeout,

    /// <summary>The request failed.</summary>
    Error,

    /// <summary>Another link was opened meanwhile, so the answer no longer matters.</summary>
    Superseded,
}

/// <summary>
/// What asking about a short link gave. <paramref name="Destination"/> is set only for
/// <see cref="ShortLinkOutcome.InApp"/>; in every other case the link itself is left to the browser.
/// <paramref name="Hops"/> counts the redirects that were read.
/// </summary>
internal sealed record ShortLinkResult(ShortLinkOutcome Outcome, TextLinkDestination? Destination, int Hops, long Milliseconds, string? ErrorType = null);

/// <summary>
/// Asks the site where one of its short links leads, the way a browser would find out, but without
/// going there: one plain request per hop, no cookies, no account, no referrer, redirects not followed,
/// only the status and the Location header read. Requests go to the short-link hosts and nowhere else;
/// an address on any other host is read by <see cref="TextLinkResolver"/> and never requested.
/// </summary>
internal sealed class ShortLinkResolver : IDisposable
{
    /// <summary>How many requests one link may cost before it is left to the browser.</summary>
    public const int MaxRequests = 3;

    /// <summary>How long the whole of it may take, all hops together.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShortLinkResolver"/> class.
    /// The handler must not follow redirects on its own; see <see cref="CreateHandler"/>.
    /// </summary>
    public ShortLinkResolver(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(InternalHttpExtensions.VideoUserAgent);
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>The handler used outside of checks. Certificates are validated the way the system does.</summary>
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,

        // The body is never wanted: drop the connection rather than read a page to keep it.
        MaxResponseDrainSize = 0,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// Asks where a short link leads. Never throws: every way this can go wrong is an outcome.
    /// Cancelling <paramref name="cancellationToken"/> gives <see cref="ShortLinkOutcome.Superseded"/>.
    /// </summary>
    public async Task<ShortLinkResult> ResolveAsync(Uri shortLink, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var hops = 0;
        ShortLinkResult End(ShortLinkOutcome outcome, TextLinkDestination? destination = null, string? errorType = null)
            => new(outcome, destination, hops, watch.ElapsedMilliseconds, errorType);

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(_timeout);
            var current = shortLink;
            for (var sent = 0; sent < MaxRequests; sent++)
            {
                // The one gate every request passes: nothing is sent to a host outside the list.
                if (!TextLinkResolver.IsShortLink(current))
                {
                    return End(ShortLinkOutcome.WebPage);
                }

                var location = await AskAsync(Secure(current), limit.Token).ConfigureAwait(false);
                if (location is null)
                {
                    return End(ShortLinkOutcome.NoRedirect);
                }

                hops++;
                var place = TextLinkResolver.ResolveAddress(location.AbsoluteUri);
                if (place is { IsExternal: false, IsShortLink: false })
                {
                    return End(ShortLinkOutcome.InApp, place);
                }

                if (!TextLinkResolver.IsShortLink(location))
                {
                    return End(ShortLinkOutcome.WebPage);
                }

                current = location;
            }

            return End(ShortLinkOutcome.TooManyHops);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return End(ShortLinkOutcome.Superseded);
        }
        catch (OperationCanceledException)
        {
            return End(ShortLinkOutcome.Timeout);
        }
        catch (Exception ex)
        {
            return End(ShortLinkOutcome.Error, errorType: ex.GetType().Name);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _client.Dispose();

    // Short links are asked about over https, whichever way they were written.
    private static Uri Secure(Uri address)
        => address.Scheme == Uri.UriSchemeHttps ? address : new UriBuilder(address) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;

    // Where the site says the address leads, or null when it does not say.
    private async Task<Uri?> AskAsync(Uri address, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location)
        {
            return null;
        }

        return location.IsAbsoluteUri ? location : new Uri(address, location);
    }
}

/// <summary>
/// Keeps short links to one question at a time: opening a link voids the one still being asked about,
/// and pressing the very link that is being asked about does not ask a second time.
/// </summary>
internal sealed class ShortLinkRequests(ShortLinkResolver resolver)
{
    private readonly object _gate = new();
    private object? _key;
    private CancellationTokenSource? _cancellation;

    /// <summary>
    /// Asks where a link leads. <paramref name="key"/> tells one link on screen from another.
    /// Null when that link is already being asked about: the press that started it opens it.
    /// <see cref="ShortLinkOutcome.Superseded"/> when something else was opened before the answer came.
    /// </summary>
    public async Task<ShortLinkResult?> AskAsync(object key, Uri shortLink)
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_cancellation is not null && ReferenceEquals(_key, key))
            {
                return null;
            }

            _cancellation?.Cancel();
            _cancellation = cancellation = new CancellationTokenSource();
            _key = key;
        }

        try
        {
            var result = await resolver.ResolveAsync(shortLink, cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!ReferenceEquals(_cancellation, cancellation))
                {
                    // Voided at the very moment the answer came: it still must not be acted on.
                    return result with { Outcome = ShortLinkOutcome.Superseded, Destination = null };
                }

                _cancellation = null;
                _key = null;
                return result;
            }
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    /// <summary>Voids the link being asked about, if there is one.</summary>
    public void Void()
    {
        lock (_gate)
        {
            _cancellation?.Cancel();
            _cancellation = null;
            _key = null;
        }
    }
}
