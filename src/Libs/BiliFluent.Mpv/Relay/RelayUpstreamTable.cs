// Copyright (c) Bili Copilot. All rights reserved.

namespace Richasy.MpvKernel.Relay;

/// <summary>
/// One address a stream can be read from.
/// </summary>
public sealed class RelayUpstream
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RelayUpstream"/> class.
    /// </summary>
    /// <param name="url">The absolute http or https URL, exactly as it was issued.</param>
    /// <param name="label">The kind of node, safe to log.</param>
    public RelayUpstream(string url, string label)
    {
        if (!IsSupported(url))
        {
            // The message leaves the URL out: it is signed.
            throw new ArgumentException("The upstream address must be an absolute http or https URL.", nameof(url));
        }

        Url = url;
        Address = new Uri(url, UriKind.Absolute);
        Label = label ?? string.Empty;
    }

    /// <summary>
    /// The signed URL. It must never be logged.
    /// </summary>
    public string Url { get; }

    /// <summary>
    /// The kind of node, safe to log.
    /// </summary>
    public string Label { get; }

    internal Uri Address { get; }

    /// <summary>
    /// Whether the relay can read from the address.
    /// </summary>
    public static bool IsSupported(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// The label only, so that an instance that ends up in a log does not reveal its URL.
    /// </summary>
    public override string ToString() => Label;
}

/// <summary>
/// The address a stream is read from at one moment.
/// </summary>
/// <param name="Version">Changes whenever the address or the table changes.</param>
/// <param name="Index">The position of the address in the table.</param>
/// <param name="Upstream">The address.</param>
public readonly record struct RelayUpstreamSelection(int Version, int Index, RelayUpstream Upstream);

/// <summary>
/// The ordered addresses of one stream and the one in use. It only keeps the order and the
/// choice; it is not thread-safe, the relay guards it.
/// </summary>
public sealed class RelayUpstreamTable
{
    private IReadOnlyList<RelayUpstream> _upstreams;
    private int _current;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayUpstreamTable"/> class.
    /// </summary>
    /// <param name="upstreams">The addresses, in the order they should be tried.</param>
    /// <param name="current">The index of the address to start with.</param>
    public RelayUpstreamTable(IReadOnlyList<RelayUpstream> upstreams, int current = 0)
    {
        Validate(upstreams, current);
        _upstreams = [.. upstreams];
        _current = current;
    }

    /// <summary>
    /// The number of addresses.
    /// </summary>
    public int Count => _upstreams.Count;

    /// <summary>
    /// Changes whenever the address in use or the table changes.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>
    /// The address in use.
    /// </summary>
    public RelayUpstreamSelection Current => new(Version, _current, _upstreams[_current]);

    /// <summary>
    /// The index of the address with this URL, or -1.
    /// </summary>
    public int IndexOf(string? url)
    {
        for (var i = 0; i < _upstreams.Count; i++)
        {
            if (string.Equals(_upstreams[i].Url, url, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Makes the address at <paramref name="index"/> the one in use. False when there is no such
    /// address; choosing the address already in use changes nothing.
    /// </summary>
    public bool Select(int index)
    {
        if (index < 0 || index >= _upstreams.Count)
        {
            return false;
        }

        if (index != _current)
        {
            _current = index;
            Version++;
        }

        return true;
    }

    /// <summary>
    /// Moves on to the next address after the one that was in use at <paramref name="version"/>
    /// failed, wrapping around at the end. False when the choice has changed since: somebody
    /// else already moved on, and <paramref name="next"/> is what is in use now.
    /// </summary>
    public bool TryRotate(int version, out RelayUpstreamSelection next)
    {
        if (version != Version)
        {
            next = Current;
            return false;
        }

        // A table of one address counts as moved as well, so the address is asked again.
        _current = (_current + 1) % _upstreams.Count;
        Version++;
        next = Current;
        return true;
    }

    /// <summary>
    /// Replaces every address, for URLs that were issued again.
    /// </summary>
    public void Replace(IReadOnlyList<RelayUpstream> upstreams, int current = 0)
    {
        Validate(upstreams, current);
        _upstreams = [.. upstreams];
        _current = current;
        Version++;
    }

    private static void Validate(IReadOnlyList<RelayUpstream> upstreams, int current)
    {
        ArgumentNullException.ThrowIfNull(upstreams);
        if (upstreams.Count == 0)
        {
            throw new ArgumentException("A stream needs an address.", nameof(upstreams));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(current);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(current, upstreams.Count);
        foreach (var upstream in upstreams)
        {
            ArgumentNullException.ThrowIfNull(upstream, nameof(upstreams));
        }
    }
}
