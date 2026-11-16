// Copyright (c) Bili Copilot. All rights reserved.

using System.Text;

namespace Richasy.MpvKernel.Relay;

/// <summary>
/// The part of an HTTP/1.x request head the relay needs.
/// </summary>
public sealed class RelayRequestHead
{
    private static ReadOnlySpan<byte> Terminator => "\r\n\r\n"u8;

    private RelayRequestHead(string method, string target, string? range)
    {
        Method = method;
        Target = target;
        Range = range;
    }

    /// <summary>
    /// The request method, as sent.
    /// </summary>
    public string Method { get; }

    /// <summary>
    /// The request target, as sent.
    /// </summary>
    public string Target { get; }

    /// <summary>
    /// The value of the Range header; null when there is none.
    /// </summary>
    public string? Range { get; }

    /// <summary>
    /// The length of the head in <paramref name="buffer"/>, the empty line included; -1 while it is incomplete.
    /// </summary>
    public static int FindEnd(ReadOnlySpan<byte> buffer)
    {
        var index = buffer.IndexOf(Terminator);
        return index < 0 ? -1 : index + Terminator.Length;
    }

    /// <summary>
    /// Parses a complete request head. False when it is not an HTTP/1.x request.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> head, out RelayRequestHead? request)
    {
        request = null;
        var text = Encoding.ASCII.GetString(head);
        var lines = text.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0
            || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            return false;
        }

        string? range = null;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && range is null && line.AsSpan(0, colon).Trim().Equals("Range", StringComparison.OrdinalIgnoreCase))
            {
                range = line[(colon + 1)..].Trim();
            }
        }

        request = new(parts[0], parts[1], range);
        return true;
    }
}
