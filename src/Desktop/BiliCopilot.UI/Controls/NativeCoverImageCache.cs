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
    private const long CacheBudgetBytes = 512L * 1024 * 1024;
    private const long CleanupWriteThresholdBytes = 32L * 1024 * 1024;
    private const int MaxImageResponseBytes = 16 * 1024 * 1024;
    private const int ReadBufferBytes = 64 * 1024;
    private static readonly TimeSpan StartupCleanupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, Task<byte[]>> _pending = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> _activeCacheFiles = new(StringComparer.OrdinalIgnoreCase);
    private static long _bytesWrittenSinceCleanup;
    private static long _lastCleanupTick;
    private static int _startupCleanupScheduled;
    private static int _cleanupScheduled;

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
        string? activePath = null;
        try
        {
            string? cachePath = null;
            try
            {
                var root = await Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalCacheFolder.CreateFolderAsync("ImageExCache", CreationCollisionOption.OpenIfExists).AsTask().ConfigureAwait(false);
                var folder = await root.CreateFolderAsync("ImageCache", CreationCollisionOption.OpenIfExists).AsTask().ConfigureAwait(false);
                if (Interlocked.CompareExchange(ref _startupCleanupScheduled, 1, 0) == 0)
                {
                    ScheduleCleanup(folder.Path, StartupCleanupDelay);
                }

#pragma warning disable CA5351 // MD5 preserves existing cache identifiers, not security hashes.
                var fileName = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(key)));
#pragma warning restore CA5351
                var path = Path.Combine(folder.Path, fileName);
                // Never wait for cleanup. A file being evicted is simply a cache miss.
                if (_activeCacheFiles.TryAdd(path, 0))
                {
                    activePath = cachePath = path;
                }

                if (cachePath is not null && File.Exists(cachePath))
                {
                    if (DateTimeOffset.UtcNow - File.GetCreationTimeUtc(cachePath) <= WinUIKernelShareExtensions.ImageCacheTime)
                    {
                        using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read,
                            FileShare.Read | FileShare.Delete, ReadBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        if (stream.Length <= MaxImageResponseBytes)
                        {
                            var cachedBytes = await ReadBoundedAsync(stream).ConfigureAwait(false);
                            TouchCacheFile(cachePath);
                            return cachedBytes;
                        }
                    }

                    File.Delete(cachePath);
                }
            }
            catch (Exception ex) when (IsCacheAccessFailure(ex))
            {
                // Cache clearing can race a visible card; downloading still works.
                cachePath = null;
            }

            using var response = await InternalHttpExtensions.PublicImageClient.GetAsync(source, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxImageResponseBytes)
            {
                throw new InvalidDataException("Cover image exceeds the response size limit.");
            }

            using var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(responseStream).ConfigureAwait(false);
            if (cachePath is not null)
            {
                await WriteCacheAsync(cachePath, bytes).ConfigureAwait(false);
            }

            return bytes;
        }
        finally
        {
            if (activePath is not null) _activeCacheFiles.TryRemove(activePath, out _);
            _pending.TryRemove(key, out _);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream)
    {
        using var result = new MemoryStream();
        var buffer = new byte[ReadBufferBytes];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
        {
            if (read > MaxImageResponseBytes - result.Length)
            {
                throw new InvalidDataException("Cover image exceeds the response size limit.");
            }

            await result.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }

        return result.ToArray();
    }

    private static async Task WriteCacheAsync(string cachePath, byte[] bytes)
    {
        var temporaryPath = cachePath + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes).ConfigureAwait(false);
            File.Move(temporaryPath, cachePath, overwrite: true);
            TouchCacheFile(cachePath);
            if (Interlocked.Add(ref _bytesWrittenSinceCleanup, bytes.Length) >= CleanupWriteThresholdBytes)
            {
                var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastCleanupTick));
                ScheduleCleanup(Path.GetDirectoryName(cachePath)!, elapsed < CleanupInterval ? CleanupInterval - elapsed : TimeSpan.Zero);
            }
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

    private static void TouchCacheFile(string cachePath)
    {
        try
        {
            // NTFS does not necessarily update access times on reads.
            File.SetLastAccessTimeUtc(cachePath, DateTime.UtcNow);
        }
        catch (Exception ex) when (IsCacheAccessFailure(ex))
        {
        }
    }

    private static void ScheduleCleanup(string folderPath, TimeSpan delay)
    {
        if (Interlocked.CompareExchange(ref _cleanupScheduled, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                Interlocked.Exchange(ref _bytesWrittenSinceCleanup, 0);
                CleanupCache(folderPath);
            }
            catch (Exception ex) when (IsCacheAccessFailure(ex))
            {
                // Directory clearing and inaccessible files do not affect visible images.
            }
            finally
            {
                Interlocked.Exchange(ref _lastCleanupTick, Environment.TickCount64);
                Volatile.Write(ref _cleanupScheduled, 0);
                if (Interlocked.Read(ref _bytesWrittenSinceCleanup) >= CleanupWriteThresholdBytes)
                {
                    ScheduleCleanup(folderPath, CleanupInterval);
                }
            }
        });
    }

    private static void CleanupCache(string folderPath)
    {
        var files = new List<(string Path, long Length, DateTime Accessed, DateTime Created)>();
        var now = DateTime.UtcNow;
        long totalBytes = 0;
        foreach (var path in Directory.EnumerateFiles(folderPath))
        {
            try
            {
                var file = new FileInfo(path);
                var partialIndex = file.Name.IndexOf(".partial-", StringComparison.Ordinal);
                if (partialIndex >= 0)
                {
                    var destination = Path.Combine(folderPath, file.Name[..partialIndex]);
                    if (now - file.CreationTimeUtc > CleanupInterval && _activeCacheFiles.TryAdd(destination, 0))
                    {
                        try
                        {
                            file.Delete();
                        }
                        finally
                        {
                            _activeCacheFiles.TryRemove(destination, out _);
                        }
                    }

                    continue;
                }

                files.Add((path, file.Length, file.LastAccessTimeUtc, file.CreationTimeUtc));
                totalBytes += file.Length;
            }
            catch (Exception ex) when (IsCacheAccessFailure(ex))
            {
            }
        }

        foreach (var file in files.OrderBy(file => file.Accessed))
        {
            if (totalBytes <= CacheBudgetBytes && now - file.Created <= WinUIKernelShareExtensions.ImageCacheTime) continue;
            if (!_activeCacheFiles.TryAdd(file.Path, 0)) continue;
            try
            {
                var current = new FileInfo(file.Path);
                if (!current.Exists)
                {
                    totalBytes -= file.Length;
                    continue;
                }

                // A completed read or replacement after the scan makes this snapshot stale.
                var currentLength = current.Length;
                totalBytes += currentLength - file.Length;
                if (current.LastAccessTimeUtc != file.Accessed || current.CreationTimeUtc != file.Created || currentLength != file.Length) continue;
                current.Delete();
                totalBytes -= currentLength;
            }
            catch (Exception ex) when (IsCacheAccessFailure(ex))
            {
            }
            finally
            {
                _activeCacheFiles.TryRemove(file.Path, out _);
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
