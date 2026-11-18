// Copyright (c) Bili Copilot. All rights reserved.

namespace Richasy.MpvKernel.Core;

public sealed partial class MpvClient
{
    /// <summary>Updates forward cache limits on an initialized, playing core without loading the file again.</summary>
    /// <param name="maxBytes">Maximum forward demuxer bytes.</param>
    /// <param name="readAheadSeconds">The demuxer readahead target in media seconds.</param>
    /// <param name="cacheSeconds">The network cache target in media seconds.</param>
    /// <returns>The completion of the runtime property updates.</returns>
    public Task UpdateCacheLimitsAsync(long maxBytes, double readAheadSeconds, double cacheSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        if (!double.IsFinite(readAheadSeconds) || readAheadSeconds < 0) throw new ArgumentOutOfRangeException(nameof(readAheadSeconds));
        if (!double.IsFinite(cacheSeconds) || cacheSeconds < 0) throw new ArgumentOutOfRangeException(nameof(cacheSeconds));
        return Task.Run(() =>
        {
            ThrowIfFailed(SetProperty("demuxer-max-bytes", maxBytes), "Mpv | update cache byte limit failed");
            ThrowIfFailed(SetProperty("demuxer-readahead-secs", readAheadSeconds), "Mpv | update readahead limit failed");
            ThrowIfFailed(SetProperty("cache-secs", cacheSeconds), "Mpv | update network cache limit failed");
        });
    }
}
