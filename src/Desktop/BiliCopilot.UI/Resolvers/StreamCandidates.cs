// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using System.Text.RegularExpressions;

namespace BiliCopilot.UI.Resolvers;

/// <summary>
/// Reads what the player needs from a signed stream URL without keeping or logging the URL itself.
/// </summary>
internal static partial class CdnHost
{
    /// <summary>
    /// The category that stands for "no stream" in logs.
    /// </summary>
    public const string NoCategory = "n/a";

    /// <summary>
    /// The full host name, as the in-memory key of a node. It must never be logged.
    /// </summary>
    public static string GetKey(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : string.Empty;

    /// <summary>
    /// The kind of node behind a URL, with region, carrier and node numbers removed, so that it
    /// can be logged. The same kind is shared by every user of that CDN.
    /// </summary>
    public static string Classify(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return NoCategory;
        }

        var host = uri.Host.ToLowerInvariant();
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return "ip";
        }

        if (host.EndsWith(".szbdyd.com", StringComparison.Ordinal))
        {
            return "szbdyd";
        }

        if (host.Contains(".mcdn.bilivideo.", StringComparison.Ordinal))
        {
            return "mcdn";
        }

        // upos-sz-mirrorcos.bilivideo.com, upos-hz-mirrorakam.akamaized.net: the mirror name is the CDN vendor.
        var upos = UposRegex().Match(host);
        if (upos.Success)
        {
            var name = upos.Groups["name"].Value;
            return name.StartsWith("mirror", StringComparison.Ordinal) && name.Length > "mirror".Length
                ? "upos-mirror-" + name["mirror".Length..]
                : "upos-" + name;
        }

        // cn-gdfs-ct-01-06.bilivideo.com: a self-built node; region, carrier and numbers are dropped.
        if (NodeRegex().IsMatch(host))
        {
            return "cn-node";
        }

        if (host.EndsWith(".bilivideo.com", StringComparison.Ordinal) || host.EndsWith(".bilivideo.cn", StringComparison.Ordinal))
        {
            return "bilivideo-other";
        }

        return host.EndsWith(".akamaized.net", StringComparison.Ordinal) ? "akamai" : "other";
    }

    /// <summary>
    /// The time a signed URL stops working, from its deadline query parameter (Unix seconds).
    /// </summary>
    public static DateTimeOffset? GetDeadline(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            const string Name = "deadline=";
            if (pair.StartsWith(Name, StringComparison.Ordinal)
                && long.TryParse(pair.AsSpan(Name.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0 && seconds < 253402300800)
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }

        return null;
    }

    [GeneratedRegex(@"^upos-[a-z0-9]+-(?<name>[a-z0-9]{1,16})\.", RegexOptions.CultureInvariant)]
    private static partial Regex UposRegex();

    [GeneratedRegex(@"^cn-[a-z0-9]+-[a-z0-9]+-\d+-\d+\.", RegexOptions.CultureInvariant)]
    private static partial Regex NodeRegex();
}

/// <summary>
/// Remembers, for this app session only, which hosts failed and when.
/// </summary>
internal sealed class CdnHostHealth
{
    private readonly Dictionary<string, DateTimeOffset> _failures = new(StringComparer.Ordinal);

    /// <summary>
    /// The record shared by every player of this app session.
    /// </summary>
    public static CdnHostHealth Session { get; } = new();

    public void ReportFailure(string hostKey, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(hostKey))
        {
            return;
        }

        lock (_failures)
        {
            _failures[hostKey] = now;
        }
    }

    public bool IsCoolingDown(string hostKey, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(hostKey))
        {
            return false;
        }

        lock (_failures)
        {
            if (!_failures.TryGetValue(hostKey, out var failed))
            {
                return false;
            }

            if (now - failed < PlaybackNetworkPolicy.HostFailureCooldown)
            {
                return true;
            }

            _failures.Remove(hostKey);
            return false;
        }
    }
}

/// <summary>
/// The two streams of a DASH media; each one is read from an address of its own.
/// </summary>
internal enum PlaybackStreamKind
{
    Video,
    Audio,
}

/// <summary>
/// One address a stream can be read from: the signed URL exactly as the API returned it.
/// </summary>
/// <param name="Url">The signed URL. It must never be logged.</param>
/// <param name="HostKey">The full host name, the in-memory key of the node. It must never be logged.</param>
/// <param name="Category">The kind of node, safe to log.</param>
/// <param name="IsP2P">Whether the node is a P2P one.</param>
internal sealed record StreamCandidate(string Url, string HostKey, string Category, bool IsP2P);

internal static class StreamCandidates
{
    /// <summary>
    /// Lists the addresses of one stream in the order they should be tried: the order the API
    /// returned them, with P2P nodes moved back and hosts that failed recently moved behind those.
    /// </summary>
    /// <param name="baseUrl">The main URL.</param>
    /// <param name="backupUrls">The backup URLs.</param>
    /// <param name="excludeP2P">Drops P2P nodes while any other node is left.</param>
    /// <param name="isP2P">Recognizes the URL of a P2P node.</param>
    /// <param name="health">The record of failed hosts.</param>
    /// <param name="now">The current time.</param>
    public static IReadOnlyList<StreamCandidate> Build(
        string? baseUrl,
        IEnumerable<string?>? backupUrls,
        bool excludeP2P,
        Func<string, bool> isP2P,
        CdnHostHealth health,
        DateTimeOffset now)
    {
        var candidates = new List<StreamCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Add(baseUrl);
        foreach (var url in backupUrls ?? [])
        {
            Add(url);
        }

        if (excludeP2P && candidates.Exists(candidate => !candidate.IsP2P))
        {
            // With only P2P nodes on offer the list is kept, so the media still plays.
            candidates.RemoveAll(candidate => candidate.IsP2P);
        }

        // OrderBy is stable: candidates of the same rank keep the order of the API.
        return candidates
            .OrderBy(candidate => (health.IsCoolingDown(candidate.HostKey, now) ? 2 : 0) + (candidate.IsP2P ? 1 : 0))
            .ToList();

        void Add(string? url)
        {
            if (string.IsNullOrEmpty(url) || !seen.Add(url))
            {
                return;
            }

            var key = CdnHost.GetKey(url);
            candidates.Add(new(url, key, CdnHost.Classify(url), key.Length > 0 && isP2P(url)));
        }
    }
}

/// <summary>
/// The addresses a media source was served with. It is immutable and may be read on any thread.
/// </summary>
/// <param name="Video">The video address in use; null when the media has no video stream.</param>
/// <param name="Audio">The audio address in use; null when the media has no separate audio stream.</param>
/// <param name="VideoRemaining">Video addresses that were not tried for this media yet.</param>
/// <param name="AudioRemaining">Audio addresses that were not tried for this media yet.</param>
/// <param name="Quality">The id of the video quality.</param>
/// <param name="VideoBandwidth">The bandwidth of the video stream, in bits per second.</param>
/// <param name="AudioBandwidth">The bandwidth of the audio stream, in bits per second.</param>
/// <param name="Deadline">The earliest time one of the two URLs stops working; null when unknown.</param>
internal sealed record PlaybackStreamSelection(
    StreamCandidate? Video,
    StreamCandidate? Audio,
    int VideoRemaining,
    int AudioRemaining,
    int Quality,
    int VideoBandwidth,
    int AudioBandwidth,
    DateTimeOffset? Deadline);
