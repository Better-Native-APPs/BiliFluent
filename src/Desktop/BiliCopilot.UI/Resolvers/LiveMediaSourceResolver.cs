// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Live.Playback;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Media;
using Richasy.MpvKernel.Core.Models;
using Richasy.MpvKernel.Player;
using Richasy.MpvKernel.Player.Models;
using System.Globalization;

namespace BiliCopilot.UI.Resolvers;

/// <summary>
/// Resolves a live room once per media generation, then serves its ordered candidates directly.
/// Resolution, candidate advancement and native lifetime belong to the player, not this resolver.
/// </summary>
public sealed class LiveMediaSourceResolver(IPlayerService player, IBiliCookiesResolver cookies) : IMpvMediaSourceResolver
{
    private LiveSourceResult? _result;
    private CancellationToken _token;
    private IntPtr _windowHandle;
    private double _volume;

    /// <summary>The canonical room verified against the page, stream and resolved request.</summary>
    public long RoomId { get; private set; }

    /// <summary>The response's eligible requestable tiers, including accept_qn entries.</summary>
    public IReadOnlyList<PlayerFormatInformation> Qualities => _result?.Qualities ?? [];

    /// <summary>Only tiers with a matching current_qn and EOTF are playable in this response.</summary>
    public IReadOnlyList<PlayerFormatInformation> PlayableQualities { get; private set; } = [];

    /// <summary>The actual playable quality, never a silently downgraded explicit request.</summary>
    public PlayerFormatInformation? SelectedQuality => _result?.SelectedQuality;

    /// <summary>The resolved room information used to bind the room session.</summary>
    public LiveInformation? Room => _result?.LivePage.Information;

    /// <summary>At most three attempts, in the existing live source service's order.</summary>
    public int CandidateCount => _result is null ? 0 : Math.Min(3, Math.Max(1, _result.Media.Candidates.Count));

    /// <summary>The candidate selected by the owning player's one initialization loop.</summary>
    public int CandidateIndex { get; private set; }

    /// <summary>Resolves fresh media without on-demand preferences, history, DASH or a relay.</summary>
    public async Task ResolveAsync(MediaSnapshot snapshot, IntPtr windowHandle, double volume,
        bool loggedIn, bool vip, bool preferHdr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Type != BiliMediaType.Live || snapshot.Live is null)
            throw new ArgumentException("A live room snapshot is required.", nameof(snapshot));
        cancellationToken.ThrowIfCancellationRequested();
        _result = null;
        _token = cancellationToken;
        RoomId = 0;
        PlayableQualities = [];
        try
        {
            var service = new LiveMediaSourceService(player, cookies,
                format => (!format.NeedLogin || loggedIn) && (!format.NeedVip || vip), () => preferHdr);
            var request = new LivePlaybackRequest(snapshot.Live, snapshot.PreferQuality, snapshot.PreferEotf);
            var result = await service.ResolveAsync(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var room = CanonicalRoom(result);
            ValidateMedia(result.Media);
            _result = result;
            _token = cancellationToken;
            _windowHandle = windowHandle;
            _volume = volume;
            RoomId = room;
            CandidateIndex = 0;
            PlayableQualities = Array.AsReadOnly(result.Qualities.Where(format => result.Media.Live.Lines.Any(line =>
                line.Quality == format.Quality && line.Eotf == format.Eotf)).Distinct().ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Live media resolution was cancelled.", cancellationToken);
        }
        catch (Exception)
        {
            // SDK/native exception text can contain signed addresses or account credentials.
            throw new LiveSourceException("Live media or the requested quality is unavailable.");
        }
    }

    /// <summary>Selects an already resolved candidate; it does not retry or resolve another tier.</summary>
    public void SelectCandidate(int index)
    {
        _token.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, CandidateCount);
        CandidateIndex = index;
    }

    /// <inheritdoc/>
    public Task<MpvMediaSource> GetSourceAsync()
    {
        _token.ThrowIfCancellationRequested();
        var result = _result ?? throw new InvalidOperationException("Live media has not been resolved.");
        var media = result.Media;
        var uri = media.Candidates.Count == 0 ? media.StreamUri : media.Candidates[CandidateIndex].Uri;
        var headers = new Dictionary<string, string>(media.Headers, StringComparer.OrdinalIgnoreCase);
        headers.Remove("User-Agent", out var agent);
        return Task.FromResult(new MpvMediaSource(uri.AbsoluteUri, "live:" + RoomId.ToString(CultureInfo.InvariantCulture),
            result.LivePage.Information.Identifier.Title, new MpvPlayOptions
            {
                WindowHandle = _windowHandle,
                HttpHeaders = headers,
                UserAgent = agent,
                EnableCookies = true,
                EnableYtdl = false,
                InitialVolume = _volume,
                InitialSpeed = 1,
                // No start/history position, external audio, subtitles or relay streams.
            }));
    }

    /// <inheritdoc/>
    public IMpvMediaSourceResolver Clone()
        => new LiveMediaSourceResolver(player, cookies)
        {
            _result = _result,
            _token = _token,
            _windowHandle = _windowHandle,
            _volume = _volume,
            RoomId = RoomId,
            CandidateIndex = CandidateIndex,
            PlayableQualities = PlayableQualities,
        };

    private static long CanonicalRoom(LiveSourceResult result)
    {
        if (!TryRoom(result.LivePage.Information.Identifier.Id, out var room)
            || !TryRoom(result.Media.Live.Id, out var streamRoom) || streamRoom != room
            || !TryRoom(result.Media.Request.Live.Identifier.Id, out var requestRoom) || requestRoom != room)
            throw new LiveSourceException("Live canonical room or source scope is invalid.");
        return room;
    }

    private static bool TryRoom(string? text, out long room)
        => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out room) && room > 0;

    private static void ValidateMedia(LiveResolvedMedia media)
    {
        foreach (var uri in media.Candidates.Take(3).Select(candidate => candidate.Uri).Prepend(media.StreamUri))
            if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                throw new LiveSourceException("Live media requires an HTTP(S) address.");
        foreach (var header in media.Headers)
            if (header.Key.Contains('\r') || header.Key.Contains('\n') || header.Value.Contains('\r') || header.Value.Contains('\n'))
                throw new LiveSourceException("Live media headers are invalid.");
    }
}
