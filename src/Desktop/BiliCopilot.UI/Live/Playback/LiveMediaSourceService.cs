using BiliCopilot.UI.Extensions;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.Live.Playback;

// Messages created here are fixed safe disclosures, never remote exception text/URLs/headers.
public sealed class LiveSourceException : Exception
{
    public LiveSourceException() { }
    public LiveSourceException(string? message) : base(message) { }
    public LiveSourceException(string? message, Exception? innerException) : base(message, innerException) { }
}

public sealed class LiveSourceResult
{
    public required LiveResolvedMedia Media { get; init; }
    public required LivePlayerView LivePage { get; init; }
    public IReadOnlyList<PlayerFormatInformation> Qualities { get; init; } = Array.Empty<PlayerFormatInformation>();
    public PlayerFormatInformation? SelectedQuality { get; init; }
}

public sealed class LiveMediaSourceService
{
    private readonly IPlayerService _player;
    private readonly IBiliCookiesResolver _cookies;
    private readonly Func<PlayerFormatInformation, bool> _canPlayFormat;
    private readonly Func<bool> _preferHdr;

    public LiveMediaSourceService(IPlayerService player, IBiliCookiesResolver cookies,
        Func<PlayerFormatInformation, bool> canPlayFormat, Func<bool>? preferHdr = null)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        _canPlayFormat = canPlayFormat ?? throw new ArgumentNullException(nameof(canPlayFormat));
        // Without a caller that knows the display, automatic live selection stays on SDR.
        _preferHdr = preferHdr ?? (() => false);
    }

    public async Task<LiveSourceResult> ResolveAsync(LivePlaybackRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var livePage = await _player.GetLivePageDetailAsync(request.Live.Identifier, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        request = request with { Live = livePage.Information };
        // The web quality table reaches 30000; do not cap automatic requests at the legacy 10000 tier.
        var live = await _player.GetLivePlayDetailAsync(request.Live.Identifier,
            request.PreferQuality is > 0 ? request.PreferQuality.Value : 30000, false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var qualities = live.Formats?.Where(_canPlayFormat).Where(format => live.Lines.Any(line => line.Eotf == format.Eotf
                && (line.Quality == format.Quality || line.AcceptQualities.Contains(format.Quality))))
            .Distinct().ToArray() ?? [];
        var selected = SelectQuality(request, qualities, live);
        if (request.PreferQuality is > 0 && selected.Quality != request.PreferQuality)
            throw new LiveSourceException("Requested quality is unavailable.");
        var candidates = SelectCandidates(live, selected);
        var uri = candidates.FirstOrDefault()?.Uri
            ?? throw new LiveSourceException("Playable media is unavailable.");
        cancellationToken.ThrowIfCancellationRequested();
        // Headers never enter diagnostic records or messages.
        var headers = new Dictionary<string, string>
        {
            ["Referer"] = "https://live.bilibili.com/",
            ["User-Agent"] = InternalHttpExtensions.VideoUserAgent,
            ["Cookie"] = _cookies.GetCookieString(),
        };
        return new()
        {
            Media = new() { Request = request, Live = live, Candidates = candidates, StreamUri = uri, Headers = headers },
            LivePage = livePage,
            Qualities = Array.AsReadOnly(qualities),
            SelectedQuality = selected,
        };
    }

    private static IReadOnlyList<LiveStreamCandidate> SelectCandidates(LiveMediaInformation live, PlayerFormatInformation selected)
    {
        var candidates = new List<LiveStreamCandidate>();
        foreach (var line in live.Lines.Where(line => line.Quality == selected.Quality && line.Eotf == selected.Eotf))
        {
            var codec = line.Name.Equals("avc", StringComparison.OrdinalIgnoreCase) ? "avc"
                : line.Name.Equals("hevc", StringComparison.OrdinalIgnoreCase) ? "hevc" : "other";
            foreach (var url in line.Urls)
            {
                if (!Uri.TryCreate(url.ToString(), UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                    || candidates.Any(candidate => candidate.Uri == uri)) continue;
                // Prefer API metadata; older callers can still omit format_name. Never log route/query.
                var path = uri.AbsolutePath;
                var format = line.Format?.ToLowerInvariant();
                format ??= path.Contains("fmp4", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) ? "fmp4"
                    : path.EndsWith(".flv", StringComparison.OrdinalIgnoreCase) ? "flv"
                    : path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ? "ts"
                    : path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? "hls" : "unknown";
                candidates.Add(new() { Uri = uri, Format = format, Codec = codec });
            }
        }

        var ordered = candidates.OrderBy(candidate => candidate.Codec switch { "avc" => 0, "hevc" => 1, _ => 2 })
            .ThenBy(candidate => candidate.Format switch { "flv" => 0, "ts" => 1, "hls" => 2, "fmp4" => 3, _ => 4 }).ToList();
        if (ordered.Count == 0) return Array.Empty<LiveStreamCandidate>();
        var first = ordered[0];
        var attempts = new List<LiveStreamCandidate> { first };
        var otherHost = ordered.FirstOrDefault(candidate => candidate.Codec == first.Codec && candidate.Format == first.Format
            && !string.Equals(candidate.Uri.Host, first.Uri.Host, StringComparison.OrdinalIgnoreCase));
        if (otherHost is not null) attempts.Add(otherHost);
        var otherFormat = ordered.FirstOrDefault(candidate => candidate.Format != first.Format)
            // Older callers may omit format_name for HLS. A different route still
            // supplies an alternate stream family without exposing it in logs.
            ?? ordered.FirstOrDefault(candidate => candidate.Codec == first.Codec
                && candidate.Uri.AbsolutePath != first.Uri.AbsolutePath);
        if (otherFormat is not null && !attempts.Contains(otherFormat)) attempts.Add(otherFormat);
        foreach (var candidate in ordered)
        {
            if (attempts.Count == 3) break;
            if (!attempts.Contains(candidate)) attempts.Add(candidate);
        }

        return attempts.AsReadOnly();
    }

    private PlayerFormatInformation SelectQuality(LivePlaybackRequest request, IReadOnlyList<PlayerFormatInformation> qualities,
        LiveMediaInformation live)
    {
        if (request.PreferQuality is < 0) throw new LiveSourceException("Requested quality is invalid.");
        if (request.PreferEotf is < 0 or > 2)
            throw new LiveSourceException("Requested live color format is invalid.");
        // accept_qn advertises switchable tiers; only current_qn is playable in this response.
        var candidates = qualities.Where(format => live.Lines.Any(line => line.Quality == format.Quality
            && line.Eotf == format.Eotf));
        if (request.PreferQuality is > 0)
            candidates = candidates.Where(format => format.Quality == request.PreferQuality);
        if (request.PreferEotf.HasValue)
            candidates = candidates.Where(format => format.Eotf == request.PreferEotf.Value);
        // An automatic choice follows the display before the tier: HDR only where HDR output is active,
        // even when the other kind is offered at a higher tier. The other kind is used only when
        // nothing else is playable. An explicit SDR/PQ/HLG choice was already applied by the filter above.
        var preferHdr = !request.PreferEotf.HasValue && _preferHdr();
        return candidates.OrderByDescending(format => format.IsHdr == preferHdr)
            .ThenByDescending(format => format.Quality).ThenBy(format => format.Eotf).FirstOrDefault()
            ?? throw new LiveSourceException("Requested live quality or matching stream is unavailable.");
    }
}
