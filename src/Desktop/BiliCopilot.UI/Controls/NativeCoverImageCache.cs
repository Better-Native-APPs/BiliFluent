// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Extensions;
using Richasy.WinUIKernel.Share;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Web;
using Windows.Storage;

namespace BiliCopilot.UI.Controls;

internal static class NativeCoverImageCache
{
    private static readonly ConcurrentDictionary<string, Task<byte[]>> _pending = new(StringComparer.Ordinal);

    public static async Task<byte[]> GetAsync(Uri source, CancellationToken cancellationToken)
    {
        var key = NormalizeKey(source.AbsoluteUri);
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = _pending.GetOrAdd(key, completion.Task);
        if (ReferenceEquals(pending, completion.Task))
        {
            _ = ReadAndCompleteAsync(source, key, completion);
        }

        // Recycling a card cancels its decode, not another card's shared download.
        return await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReadAndCompleteAsync(Uri source, string key, TaskCompletionSource<byte[]> completion)
    {
        try
        {
            completion.SetResult(await ReadOrDownloadAsync(source, key).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
            // The last consumer may have been recycled while the download ran.
            _ = completion.Task.Exception;
        }
    }

    private static async Task<byte[]> ReadOrDownloadAsync(Uri source, string key)
    {
        try
        {
            string? cachePath = null;
            try
            {
                var root = await Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalCacheFolder.CreateFolderAsync("ImageExCache", CreationCollisionOption.OpenIfExists).AsTask().ConfigureAwait(false);
                var folder = await root.CreateFolderAsync("ImageCache", CreationCollisionOption.OpenIfExists).AsTask().ConfigureAwait(false);
#pragma warning disable CA5351 // MD5 preserves existing cache identifiers, not security hashes.
                var fileName = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(key)));
#pragma warning restore CA5351
                cachePath = Path.Combine(folder.Path, fileName);
                if (File.Exists(cachePath))
                {
                    if (DateTimeOffset.UtcNow - File.GetCreationTimeUtc(cachePath) <= WinUIKernelShareExtensions.ImageCacheTime)
                    {
                        return await File.ReadAllBytesAsync(cachePath).ConfigureAwait(false);
                    }

                    File.Delete(cachePath);
                }
            }
            catch (Exception ex) when (IsCacheAccessFailure(ex))
            {
                // Cache clearing can race a visible card; downloading still works.
                cachePath = null;
            }

            var bytes = await InternalHttpExtensions.ImageClient.GetByteArrayAsync(source).ConfigureAwait(false);
            if (cachePath is not null)
            {
                await WriteCacheAsync(cachePath, bytes).ConfigureAwait(false);
            }

            return bytes;
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    private static async Task WriteCacheAsync(string cachePath, byte[] bytes)
    {
        var temporaryPath = cachePath + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes).ConfigureAwait(false);
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        catch (Exception ex) when (IsCacheAccessFailure(ex))
        {
            // A failed cache write must not discard an already downloaded image.
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (IsCacheAccessFailure(ex))
            {
            }
        }
    }

    private static string NormalizeKey(string key)
    {
        if (key.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(key);
            if (!string.IsNullOrEmpty(uri.Query))
            {
                var query = HttpUtility.ParseQueryString(uri.Query);
                var parameters = query.AllKeys.OrderBy(name => name).Select(name => name + "=" + query[name]);
                return uri.GetLeftPart(UriPartial.Path) + "?" + string.Join("&", parameters);
            }

            return uri.AbsoluteUri.ToLowerInvariant();
        }

        return key.ToLowerInvariant();
    }

    private static bool IsCacheAccessFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or COMException;
}
