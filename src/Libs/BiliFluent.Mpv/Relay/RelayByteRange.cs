// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;

namespace Richasy.MpvKernel.Relay;

/// <summary>
/// The byte range a client asked for, before the length of the resource is known.
/// </summary>
/// <param name="First">The first byte, or the number of trailing bytes when <paramref name="IsSuffix"/> is set.</param>
/// <param name="Last">The last byte, inclusive; null for "to the end".</param>
/// <param name="IsSuffix">The request is for the last <paramref name="First"/> bytes.</param>
public readonly record struct RelayRangeRequest(long First, long? Last, bool IsSuffix);

/// <summary>
/// How a range request is answered.
/// </summary>
public enum RelayRangeKind
{
    /// <summary>The whole resource, with status 200.</summary>
    Full,

    /// <summary>A part of the resource, with status 206.</summary>
    Partial,

    /// <summary>Nothing of the resource lies in the range: status 416.</summary>
    Unsatisfiable,
}

/// <summary>
/// The bytes to send for a request.
/// </summary>
/// <param name="Kind">How the request is answered.</param>
/// <param name="From">The first byte to send.</param>
/// <param name="To">The last byte to send, inclusive.</param>
public readonly record struct RelayResolvedRange(RelayRangeKind Kind, long From, long To)
{
    /// <summary>
    /// The number of bytes to send.
    /// </summary>
    public long Length => Kind == RelayRangeKind.Unsatisfiable ? 0 : To - From + 1;
}

/// <summary>
/// Reads the Range header of a request and maps it onto a resource.
/// </summary>
public static class RelayByteRange
{
    private const string Unit = "bytes=";

    /// <summary>
    /// Parses a Range header value. Null stands for the whole resource: the header is missing,
    /// malformed, in another unit, or lists several ranges, all of which a server may ignore.
    /// </summary>
    public static RelayRangeRequest? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.AsSpan().Trim();
        if (!text.StartsWith(Unit, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        text = text[Unit.Length..].Trim();
        var dash = text.IndexOf('-');
        if (dash < 0 || text.Contains(','))
        {
            return null;
        }

        var first = text[..dash].Trim();
        var last = text[(dash + 1)..].Trim();
        if (first.IsEmpty)
        {
            // bytes=-500: the last 500 bytes.
            return TryParseOffset(last, out var suffix) ? new(suffix, null, true) : null;
        }

        if (!TryParseOffset(first, out var from))
        {
            return null;
        }

        if (last.IsEmpty)
        {
            return new(from, null, false);
        }

        return TryParseOffset(last, out var to) && to >= from ? new(from, to, false) : null;
    }

    /// <summary>
    /// Maps a request onto a resource of <paramref name="total"/> bytes.
    /// </summary>
    public static RelayResolvedRange Resolve(RelayRangeRequest? request, long total)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        if (request is not { } range)
        {
            // An empty resource is still answered with 200 and no body.
            return new(RelayRangeKind.Full, 0, total - 1);
        }

        if (range.IsSuffix)
        {
            return range.First == 0 || total == 0
                ? new(RelayRangeKind.Unsatisfiable, 0, -1)
                : new(RelayRangeKind.Partial, Math.Max(0, total - range.First), total - 1);
        }

        return range.First >= total
            ? new(RelayRangeKind.Unsatisfiable, 0, -1)
            : new(RelayRangeKind.Partial, range.First, Math.Min(range.Last ?? long.MaxValue, total - 1));
    }

    /// <summary>
    /// The Content-Range value of a partial response.
    /// </summary>
    public static string FormatContentRange(long from, long to, long total)
        => string.Create(CultureInfo.InvariantCulture, $"bytes {from}-{to}/{total}");

    /// <summary>
    /// The Content-Range value of a 416 response.
    /// </summary>
    public static string FormatUnsatisfied(long total)
        => string.Create(CultureInfo.InvariantCulture, $"bytes */{total}");

    private static bool TryParseOffset(ReadOnlySpan<char> text, out long value)
        => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
