// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Windows.Storage;

namespace Richasy.BiliKernel.Resolvers.Storage;

internal sealed class ProtectedCredentialStore<T>
    where T : class
{
    private readonly Func<string> _getLocalFolderPath;
    private readonly string _fileName;
    private readonly byte[] _entropy;
    private readonly JsonTypeInfo<T> _jsonTypeInfo;
    private readonly Func<string?> _readLegacyValue;
    private readonly Action _removeLegacyValue;
    private readonly Func<string, byte[]> _readFile;
    private readonly Action<Stream, byte[]> _writeFile;
    private bool _skipLegacyMigration;

    internal ProtectedCredentialStore(
        Func<string> getLocalFolderPath,
        string fileName,
        byte[] entropy,
        JsonTypeInfo<T> jsonTypeInfo,
        Func<string?> readLegacyValue,
        Action removeLegacyValue,
        Func<string, byte[]>? readFile = null,
        Action<Stream, byte[]>? writeFile = null)
    {
        _getLocalFolderPath = getLocalFolderPath;
        _fileName = fileName;
        _entropy = entropy.ToArray();
        _jsonTypeInfo = jsonTypeInfo;
        _readLegacyValue = readLegacyValue;
        _removeLegacyValue = removeLegacyValue;
        _readFile = readFile ?? File.ReadAllBytes;
        _writeFile = writeFile ?? ((stream, bytes) => stream.Write(bytes));
    }

    internal static ProtectedCredentialStore<T> CreateForApplication(
        string fileName,
        byte[] entropy,
        JsonTypeInfo<T> jsonTypeInfo,
        string legacyKey)
        => new(
            () => ApplicationData.Current.LocalFolder.Path,
            fileName,
            entropy,
            jsonTypeInfo,
            () => GetContainer().Values.TryGetValue(legacyKey, out var value) ? value?.ToString() : null,
            () => GetContainer().Values.Remove(legacyKey));

    internal T? Read()
    {
        string path;
        byte[] ciphertext;
        try
        {
            path = GetFilePath();
        }
        catch
        {
            return null;
        }

        // Only an absent file permits migration. File.Exists also hides access errors.
        try
        {
            ciphertext = _readFile(path);
        }
        catch (FileNotFoundException)
        {
            return _skipLegacyMigration ? null : Migrate(path);
        }
        catch (DirectoryNotFoundException)
        {
            return _skipLegacyMigration ? null : Migrate(path);
        }
        catch
        {
            _skipLegacyMigration = true;
            TryDeleteFile(path);
            return null;
        }

        // Never revive a legacy value after a protected file was found in this instance.
        _skipLegacyMigration = true;
        T value;
        try
        {
            value = DeserializeProtected(ciphertext);
        }
        catch
        {
            TryDeleteFile(path);
            return null;
        }

        TryRemoveLegacyValue();
        return value;
    }

    internal void Save(T value)
    {
        try
        {
            WriteProtected(GetFilePath(), value);
        }
        catch
        {
            // Do not attach exceptions that may contain JSON, credentials, or local paths.
            throw new IOException("credential file write failed");
        }

        try
        {
            _removeLegacyValue();
        }
        catch
        {
            throw new IOException("credential legacy value removal failed");
        }
    }

    internal void Remove()
    {
        var failed = false;
        try
        {
            File.Delete(GetFilePath());
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Logout also succeeds before the credential directory has ever been created.
        }
        catch
        {
            failed = true;
        }

        try
        {
            _removeLegacyValue();
        }
        catch
        {
            failed = true;
        }

        if (failed)
        {
            throw new IOException("credential removal failed");
        }
    }

    private static ApplicationDataContainer GetContainer()
        => ApplicationData.Current.LocalSettings.CreateContainer("BiliKernel", ApplicationDataCreateDisposition.Always);

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A read or a failed migration must not throw when cleanup is unavailable.
        }
    }

    private string GetFilePath()
        => Path.Combine(_getLocalFolderPath(), "BiliKernel", "Credentials", _fileName);

    private T? Migrate(string path)
    {
        T? legacyValue;
        try
        {
            var json = _readLegacyValue();
            legacyValue = json is null ? null : JsonSerializer.Deserialize(json, _jsonTypeInfo);
        }
        catch
        {
            return null;
        }

        if (legacyValue is null)
        {
            return null;
        }

        var written = false;
        var verified = false;
        try
        {
            WriteProtected(path, legacyValue);
            written = true;
            var readBack = DeserializeProtected(_readFile(path));
            verified = JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(legacyValue, _jsonTypeInfo),
                JsonSerializer.SerializeToElement(readBack, _jsonTypeInfo));
            if (verified)
            {
                _removeLegacyValue();
            }
        }
        catch
        {
            // Keep the old value usable in memory and retry migration or cleanup next time.
        }
        finally
        {
            if (written && !verified)
            {
                // An unverified candidate must not block the next migration attempt.
                TryDeleteFile(path);
            }
        }

        return legacyValue;
    }

    private T DeserializeProtected(byte[] ciphertext)
    {
        var plaintext = WindowsDataProtection.Unprotect(ciphertext, _entropy);
        try
        {
            return JsonSerializer.Deserialize(plaintext, _jsonTypeInfo)
                ?? throw new JsonException("credential content is invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void WriteProtected(string path, T value)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value, _jsonTypeInfo);
        byte[] ciphertext;
        try
        {
            ciphertext = WindowsDataProtection.Protect(plaintext, _entropy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{_fileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                _writeFile(stream, ciphertext);
                stream.Flush(flushToDisk: true);
            }

            // A same-directory rename atomically replaces an existing file on Windows.
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private void TryRemoveLegacyValue()
    {
        try
        {
            _removeLegacyValue();
        }
        catch
        {
            // The verified protected copy remains usable; a later read retries cleanup.
        }
    }
}

internal static partial class WindowsDataProtection
{
    internal static byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy)
        => Transform(plaintext, entropy, protect: true);

    internal static byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy)
        => Transform(ciphertext, entropy, protect: false);

    private static unsafe byte[] Transform(ReadOnlySpan<byte> input, ReadOnlySpan<byte> entropy, bool protect)
    {
        fixed (byte* inputPointer = input)
        fixed (byte* entropyPointer = entropy)
        {
            var inputBlob = new DataBlob { Length = input.Length, Data = (nint)inputPointer };
            var entropyBlob = new DataBlob { Length = entropy.Length, Data = (nint)entropyPointer };
            DataBlob output = default;
            try
            {
                // Omitting CRYPTPROTECT_LOCAL_MACHINE selects current-user protection.
                const uint uiForbidden = 1;
                var result = protect
                    ? CryptProtectData(in inputBlob, 0, in entropyBlob, 0, 0, uiForbidden, out output)
                    : CryptUnprotectData(in inputBlob, 0, in entropyBlob, 0, 0, uiForbidden, out output);
                if (result == 0)
                {
                    throw new CryptographicException("credential protection failed");
                }

                return new ReadOnlySpan<byte>((void*)output.Data, output.Length).ToArray();
            }
            finally
            {
                if (output.Data != 0)
                {
                    CryptographicOperations.ZeroMemory(new Span<byte>((void*)output.Data, output.Length));
                    _ = LocalFree(output.Data);
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public nint Data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CryptProtectData(in DataBlob input, nint description, in DataBlob entropy, nint reserved, nint prompt, uint flags, out DataBlob output);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CryptUnprotectData(in DataBlob input, nint description, in DataBlob entropy, nint reserved, nint prompt, uint flags, out DataBlob output);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint LocalFree(nint memory);
}
