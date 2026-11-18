// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace Richasy.BiliKernel.Authorizers.TV.Core;

internal static class WebLoginPasswordEncryption
{
    internal static string Encrypt(string hash, string password, string publicKey)
    {
        var plaintext = new char[hash.Length + password.Length];
        byte[]? bytes = null;
        byte[]? encrypted = null;
        try
        {
            hash.CopyTo(0, plaintext, 0, hash.Length);
            password.CopyTo(0, plaintext, hash.Length, password.Length);
            bytes = Encoding.UTF8.GetBytes(plaintext);
            using var rsa = RSA.Create();
            rsa.ImportParameters(ReadPublicKey(publicKey));
            encrypted = rsa.Encrypt(bytes, RSAEncryptionPadding.Pkcs1);
            return Convert.ToBase64String(encrypted);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            throw new CryptographicException("The login public key or encrypted input is invalid.");
        }
        finally
        {
            Array.Clear(plaintext, 0, plaintext.Length);
            if (bytes is not null)
            {
                Array.Clear(bytes, 0, bytes.Length);
            }

            if (encrypted is not null)
            {
                Array.Clear(encrypted, 0, encrypted.Length);
            }
        }
    }

    // Decode the two standard RSA public-key PEM envelopes without an extra dependency.
    // This also supports the project's netstandard2.0 target, where ImportFromPem is unavailable.
    private static RSAParameters ReadPublicKey(string pem)
    {
        var pkcs1 = pem.Contains("-----BEGIN RSA PUBLIC KEY-----");
        var base64 = pem.Replace("-----BEGIN PUBLIC KEY-----", string.Empty)
            .Replace("-----END PUBLIC KEY-----", string.Empty)
            .Replace("-----BEGIN RSA PUBLIC KEY-----", string.Empty)
            .Replace("-----END RSA PUBLIC KEY-----", string.Empty);
        var der = Convert.FromBase64String(base64);
        var outer = new DerReader(der).Read(0x30);
        if (!pkcs1)
        {
            var algorithm = outer.Read(0x30);
            var oid = algorithm.ReadBytes(0x06);
            if (!oid.SequenceEqual(new byte[] { 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01 }))
            {
                throw new CryptographicException("The login public key algorithm is unsupported.");
            }

            var bitString = outer.ReadBytes(0x03);
            if (bitString.Length < 2 || bitString[0] != 0)
            {
                throw new CryptographicException("The login public key encoding is invalid.");
            }

            outer = new DerReader(bitString, 1, bitString.Length - 1).Read(0x30);
        }

        return new RSAParameters
        {
            Modulus = TrimInteger(outer.ReadBytes(0x02)),
            Exponent = TrimInteger(outer.ReadBytes(0x02)),
        };
    }

    private static byte[] TrimInteger(byte[] value)
        => value.Length > 1 && value[0] == 0 ? value.Skip(1).ToArray() : value;

    private sealed class DerReader
    {
        private readonly byte[] _bytes;
        private readonly int _end;
        private int _position;

        internal DerReader(byte[] bytes, int offset = 0, int? length = null)
        {
            _bytes = bytes;
            _position = offset;
            _end = offset + (length ?? bytes.Length);
        }

        internal DerReader Read(byte tag)
        {
            var length = ReadLength(tag);
            var result = new DerReader(_bytes, _position, length);
            _position += length;
            return result;
        }

        internal byte[] ReadBytes(byte tag)
        {
            var length = ReadLength(tag);
            var result = new byte[length];
            Buffer.BlockCopy(_bytes, _position, result, 0, length);
            _position += length;
            return result;
        }

        private int ReadLength(byte tag)
        {
            if (_position + 2 > _end || _bytes[_position++] != tag)
            {
                throw new CryptographicException("The login public key encoding is invalid.");
            }

            var first = _bytes[_position++];
            var length = (int)first;
            if ((first & 0x80) != 0)
            {
                var count = first & 0x7f;
                if (count is 0 or > 4 || _position + count > _end)
                {
                    throw new CryptographicException("The login public key length is invalid.");
                }

                length = 0;
                for (var i = 0; i < count; i++)
                {
                    length = checked((length << 8) | _bytes[_position++]);
                }
            }

            if (length < 0 || length > _end - _position)
            {
                throw new CryptographicException("The login public key length is invalid.");
            }

            return length;
        }
    }
}
