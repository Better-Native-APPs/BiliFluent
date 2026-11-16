// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.Extensions.Logging;
using Richasy.MpvKernel.Player.Models;
using Richasy.MpvKernel.Relay;

namespace Richasy.MpvKernel.Player;

/// <summary>
/// Reads the streams of a source through a loopback relay the player owns, so that the address
/// a stream is read from can change without loading the media again.
/// </summary>
public sealed partial class MpvPlayer
{
    private readonly Lock _relaySync = new();
    private LoopbackMediaRelay? _relay;

    // The streams of the source that was loaded last, and of the one before it: mpv may still
    // be reading the earlier one until its replacement has loaded.
    private RelayedSource? _relayCurrent;
    private RelayedSource? _relayPrevious;
    private int _relaySources;

    // The relay could not start, or mpv cannot reach it: not tried again by this player.
    private bool _relayUnusable;

    // A load through the relay failed before a byte came through: this media is read directly.
    private bool _relaySkipsMedia;

    /// <summary>
    /// The relay gave up an address of a stream that is playing and moved on by itself.
    /// Raised on a worker thread.
    /// </summary>
    public event EventHandler<MpvRelayUpstreamFailedEventArgs>? RelayUpstreamFailed;

    /// <summary>
    /// How the relay is set up; null, the default, never uses one. A source is read through the
    /// relay when it carries <see cref="MpvMediaSource.Relay"/>; whenever the relay cannot be
    /// used, the source is played from its own URL as before.
    /// </summary>
    public LoopbackMediaRelayOptions? RelayOptions { get; set; }

    /// <summary>
    /// Whether the stream of the loaded source is read through the relay.
    /// </summary>
    public bool IsRelaying(MpvRelayStreamKind kind)
    {
        lock (_relaySync)
        {
            return _relay is not null && _relayCurrent?.GetKey(kind) is not null;
        }
    }

    /// <summary>
    /// What the relay knows about the stream of the loaded source; null when it is not relayed.
    /// </summary>
    public RelayStreamStatistics? GetRelayStatistics(MpvRelayStreamKind kind)
    {
        lock (_relaySync)
        {
            return _relay is { } relay && _relayCurrent?.GetKey(kind) is { } key ? relay.GetStatistics(key) : null;
        }
    }

    /// <summary>
    /// Reads the stream from another of its addresses from now on. mpv keeps its file, its
    /// position and its cache. False when the stream is not relayed or the address is not one of its own.
    /// </summary>
    public bool TrySwitchRelayNode(MpvRelayStreamKind kind, string url)
    {
        lock (_relaySync)
        {
            return _relay is { } relay && _relayCurrent?.GetKey(kind) is { } key && relay.SwitchNode(key, url);
        }
    }

    /// <summary>
    /// Hands the relay the addresses of a source that was served again for the media that is
    /// loaded, after the earlier ones expired. False when the loaded source is not relayed, or
    /// the new addresses do not serve the same streams; the caller then loads the media again.
    /// </summary>
    public async Task<bool> TryUpdateRelayAsync(MpvMediaSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        LoopbackMediaRelay? relay;
        RelayedSource? current;
        lock (_relaySync)
        {
            relay = _relay;
            current = _relayCurrent;
        }

        if (relay is null || current is null || source.Relay is not { } streams
            || (current.VideoKey is null) != (streams.Video is null)
            || (current.AudioKey is null) != (streams.Audio is null))
        {
            return false;
        }

        try
        {
            foreach (var kind in new[] { MpvRelayStreamKind.Video, MpvRelayStreamKind.Audio })
            {
                var stream = kind == MpvRelayStreamKind.Video ? streams.Video : streams.Audio;
                if (stream is null || current.GetKey(kind) is not { } key)
                {
                    continue;
                }

                var result = await relay.UpdateUrlsAsync(key, stream.Upstreams, stream.Current, streams.Headers, cancellationToken);
                if (result != RelayUpdateResult.Updated)
                {
                    _logger.LogInformation("Playback | relay address update failed stream={Stream} result={Result}", kind, result);
                    return false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        lock (_relaySync)
        {
            if (!ReferenceEquals(current, _relayCurrent))
            {
                return false;
            }
        }

        _cachedSource = source;
        return true;
    }

    // What to hand to mpv for the source: the URLs of the relay when it can serve the streams,
    // otherwise the URL and the options of the source itself.
    private (string Url, MpvPlayOptions Options) PrepareLoad(MpvMediaSource source)
    {
        lock (_relaySync)
        {
            RelayedSource? next = null;
            string? videoUrl = null;
            string? audioUrl = null;
            var video = source.Relay?.Video;
            var audio = source.Relay?.Audio;
            if (source.Relay is { } streams && (video is not null || audio is not null) && EnsureRelay() is { } relay)
            {
                var id = (++_relaySources).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var videoKey = video is null ? null : "v" + id;
                var audioKey = audio is null ? null : "a" + id;
                try
                {
                    videoUrl = Register(relay, videoKey, video, streams.Headers);
                    audioUrl = Register(relay, audioKey, audio, streams.Headers);
                    next = new(videoKey, audioKey);
                }
                catch (Exception ex)
                {
                    // Nothing of the address may reach the log, so the type has to do.
                    _logger.LogWarning("Playback | relay off for this load reason=register-failed error={Error}", ex.GetType().Name);
                    Unregister(relay, videoKey);
                    Unregister(relay, audioKey);
                    videoUrl = audioUrl = null;
                }
            }

            // The source before the last can no longer be read by mpv.
            if (_relay is { } active && _relayPrevious is { } retired)
            {
                Unregister(active, retired.VideoKey);
                Unregister(active, retired.AudioKey);
            }

            _relayPrevious = _relayCurrent;
            _relayCurrent = next;
            if (next is null)
            {
                return (source.Url, source.Options);
            }

            var options = source.Options;
            var relayed = new MpvPlayOptions
            {
                WindowHandle = options.WindowHandle,
                StartPosition = options.StartPosition,
                InitialVolume = options.InitialVolume,
                InitialSpeed = options.InitialSpeed,
                InitExtraLoader = options.InitExtraLoader,
                UserAgent = options.UserAgent,
                EnableYtdl = options.EnableYtdl,
                MediaName = options.MediaName,
                Subtitles = options.Subtitles,

                // The relay sends the cookie and the referer upstream; a request to the relay carries neither.
                HttpHeaders = [],
                EnableCookies = false,
                AudioTracks = videoUrl is not null && audioUrl is not null ? [audioUrl] : null,
            };
            return (videoUrl ?? audioUrl!, relayed);
        }

        static string? Register(LoopbackMediaRelay relay, string? key, MpvRelayAddresses? stream, IReadOnlyDictionary<string, string>? headers)
            => key is null || stream is null
                ? null
                : relay.Register(key, new() { Upstreams = stream.Upstreams, Current = stream.Current, Headers = headers });

        static void Unregister(LoopbackMediaRelay relay, string? key)
        {
            if (key is not null)
            {
                relay.Unregister(key);
            }
        }
    }

    // Another media starts: nothing of the previous one is served any longer.
    private void ReleaseRelayStreams()
    {
        lock (_relaySync)
        {
            if (_relay is { } relay)
            {
                foreach (var key in new[] { _relayCurrent?.VideoKey, _relayCurrent?.AudioKey, _relayPrevious?.VideoKey, _relayPrevious?.AudioKey })
                {
                    if (key is not null)
                    {
                        relay.Unregister(key);
                    }
                }
            }

            _relayCurrent = null;
            _relayPrevious = null;
            _relaySkipsMedia = false;
        }
    }

    private async ValueTask DisposeRelayAsync()
    {
        LoopbackMediaRelay? relay;
        lock (_relaySync)
        {
            relay = _relay;
            _relay = null;
            _relayCurrent = null;
            _relayPrevious = null;
            _relayUnusable = true;
        }

        Client.FileFailed -= OnRelayFileFailed;
        if (relay is not null)
        {
            relay.UpstreamFailed -= OnRelayUpstreamFailed;
            await relay.DisposeAsync();
        }
    }

    // Call while holding the lock. Null plays the source directly.
    private LoopbackMediaRelay? EnsureRelay()
    {
        if (RelayOptions is not { } options || _relayUnusable || _relaySkipsMedia || _isDisposed)
        {
            return null;
        }

        if (_relay is not null)
        {
            return _relay;
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("http_proxy"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HTTP_PROXY")))
        {
            // mpv would send its requests for the loopback address to that proxy.
            _relayUnusable = true;
            _logger.LogInformation("Playback | relay off reason=env-proxy");
            return null;
        }

        try
        {
            _relay = LoopbackMediaRelay.Start(options, _logger);
            _relay.UpstreamFailed += OnRelayUpstreamFailed;
            _logger.LogInformation("Playback | relay started");
            return _relay;
        }
        catch (Exception ex)
        {
            _relayUnusable = true;
            _logger.LogWarning("Playback | relay off reason=start-failed error={Error}", ex.GetType().Name);
            return null;
        }
    }

    private void OnRelayUpstreamFailed(object? sender, RelayUpstreamFailedEventArgs e)
    {
        MpvRelayStreamKind? kind;
        lock (_relaySync)
        {
            kind = _relayCurrent is not { } current ? null
                : string.Equals(current.VideoKey, e.Key, StringComparison.Ordinal) ? MpvRelayStreamKind.Video
                : string.Equals(current.AudioKey, e.Key, StringComparison.Ordinal) ? MpvRelayStreamKind.Audio
                : null;
        }

        if (kind is { } known)
        {
            RelayUpstreamFailed?.Invoke(this, new(known, e));
        }
    }

    // A load that failed without the relay having passed on a single byte may have failed
    // because of the relay. The next load plays directly, as it would have without a relay.
    private void OnRelayFileFailed(object? sender, MpvError e)
    {
        lock (_relaySync)
        {
            if (_relay is not { } relay || _relayCurrent is not { } current
                || relay.GetStatistics(current.VideoKey ?? current.AudioKey!) is not { } main)
            {
                return;
            }

            if (main.Requests == 0)
            {
                _relayUnusable = true;
                _logger.LogWarning("Playback | relay off reason=unreachable");
            }
            else if (main.UpstreamBytes + main.PreloadedBytes == 0 && !_relaySkipsMedia)
            {
                _relaySkipsMedia = true;
                _logger.LogInformation("Playback | relay off for this media reason=no-data");
            }
        }
    }

    private sealed record RelayedSource(string? VideoKey, string? AudioKey)
    {
        public string? GetKey(MpvRelayStreamKind kind) => kind == MpvRelayStreamKind.Video ? VideoKey : AudioKey;
    }
}
