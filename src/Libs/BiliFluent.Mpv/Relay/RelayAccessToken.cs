// Copyright (c) Bili Copilot. All rights reserved.

using System.Security.Cryptography;
using System.Text;

namespace Richasy.MpvKernel.Relay;

/// <summary>
/// The secret every request to a relay must carry in its path. The relay listens on the loopback
/// interface, which any process of the machine can reach; without the token none of them can
/// read a stream through it.
/// </summary>
public sealed class RelayAccessToken
{
    private const int MaxKeyLength = 64;
    private readonly byte[] _value;

    private RelayAccessToken(string value)
    {
        Value = value;
        _value = Encoding.ASCII.GetBytes(value);
    }

    /// <summary>
    /// The token as it appears in a path. It must never be logged.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Creates a token from <see cref="RelayDefaults.TokenBytes"/> bytes of cryptographic randomness.
    /// </summary>
    public static RelayAccessToken Create()
        => new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(RelayDefaults.TokenBytes)));

    /// <summary>
    /// Whether a key can be part of a path: letters, digits, '-' and '_', at most 64 of them.
    /// </summary>
    public static bool IsValidKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength)
        {
            return false;
        }

        foreach (var c in key)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The path of the stream registered under <paramref name="key"/>.
    /// </summary>
    public string BuildPath(string key)
        => IsValidKey(key) ? "/" + Value + "/" + key : throw new ArgumentException("The key is not valid.", nameof(key));

    /// <summary>
    /// Reads the key from a request target of the form /token/key. False when the target has
    /// another shape or carries another token; the comparison takes the same time either way.
    /// </summary>
    public bool TryGetKey(string? requestTarget, out string key)
    {
        key = string.Empty;
        if (string.IsNullOrEmpty(requestTarget) || requestTarget[0] != '/')
        {
            return false;
        }

        var path = requestTarget.AsSpan(1);
        var query = path.IndexOfAny('?', '#');
        if (query >= 0)
        {
            path = path[..query];
        }

        var slash = path.IndexOf('/');
        if (slash <= 0 || slash > 4 * RelayDefaults.TokenBytes)
        {
            return false;
        }

        var candidate = path[(slash + 1)..].ToString();
        Span<byte> token = stackalloc byte[slash];
        if (Encoding.ASCII.GetBytes(path[..slash], token) != slash
            || !CryptographicOperations.FixedTimeEquals(token, _value)
            || !IsValidKey(candidate))
        {
            return false;
        }

        key = candidate;
        return true;
    }

    /// <inheritdoc/>
    public override string ToString() => nameof(RelayAccessToken);
}
