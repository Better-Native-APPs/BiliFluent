// Copyright (c) Bili Copilot. All rights reserved.

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Richasy.MpvKernel.Relay;

/// <summary>
/// An HTTP/1.1 server inside the process, on the loopback interface only, that the player reads
/// its streams from instead of the CDN. Each stream is registered under a key with the ordered
/// addresses it can be read from; the relay fetches the bytes from whichever address is in use
/// and can move to another one in the middle of a response, at the byte it has reached, so the
/// player sees one uninterrupted body and keeps its cache.
/// </summary>
/// <remarks>
/// Nothing is buffered beyond one copy buffer per response: a player that stops reading stops
/// the relay from reading upstream. Every request must carry the random token of the relay in
/// its path. Neither that token nor an upstream URL or host is ever logged or put in an exception.
/// </remarks>
public sealed class LoopbackMediaRelay : IAsyncDisposable
{
    private const string DefaultContentType = "video/mp4";
    private readonly LoopbackMediaRelayOptions _options;
    private readonly ILogger _logger;
    private readonly TcpListener _listener;
    private readonly HttpClient _http;
    private readonly RelayAccessToken _token = RelayAccessToken.Create();
    private readonly ConcurrentDictionary<string, StreamEntry> _streams = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _acceptLoop;
    private int _connections;
    private int _disposed;

    private LoopbackMediaRelay(LoopbackMediaRelayOptions options, ILogger logger, TcpListener listener)
    {
        _options = options;
        _logger = logger;
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var handler = new SocketsHttpHandler
        {
            // The player reads its streams without the system proxy; so does the relay.
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = RelayDefaults.MaxRedirects,
            ConnectTimeout = options.ConnectTimeout,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        };
        if (!options.VerifyTls)
        {
#pragma warning disable CA5359 // The caller turned validation off, as it does for the player itself.
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>
    /// An upstream address failed and the relay moved on by itself. Raised on a worker thread.
    /// </summary>
    public event EventHandler<RelayUpstreamFailedEventArgs>? UpstreamFailed;

    /// <summary>
    /// The loopback port the relay listens on.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Starts a relay on 127.0.0.1 and a port the system picks. Throws when no port can be bound.
    /// </summary>
    public static LoopbackMediaRelay Start(LoopbackMediaRelayOptions? options = null, ILogger? logger = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // No other process may bind the same port and take the requests of the player.
                listener.ExclusiveAddressUse = true;
            }

            listener.Start();
        }
        catch
        {
            listener.Dispose();
            throw;
        }

        return new LoopbackMediaRelay(options ?? new(), logger ?? NullLogger.Instance, listener);
    }

    /// <summary>
    /// Serves a stream under <paramref name="key"/> and returns the URL the player reads it from.
    /// </summary>
    /// <param name="key">Letters, digits, '-' and '_'; not in use by another stream.</param>
    /// <param name="registration">The addresses and what else describes the stream.</param>
    public string Register(string key, RelayStreamRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var path = _token.BuildPath(key);
        var entry = new StreamEntry(key, registration);
        if (!_streams.TryAdd(key, entry))
        {
            entry.Dispose();
            throw new ArgumentException("The key is already in use.", nameof(key));
        }

        return string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{Port}{path}");
    }

    /// <summary>
    /// Reads the stream from the address with this URL from now on. A response that is under way
    /// goes on from that address at the byte it has reached. False when the stream or the address
    /// is not known.
    /// </summary>
    public bool SwitchNode(string key, string url)
        => _streams.TryGetValue(key, out var entry) && entry.Select(url);

    /// <summary>
    /// Replaces the addresses of a stream, for URLs that were issued again after the old ones
    /// expired. When the length of the stream is known, the new addresses must serve that many
    /// bytes; otherwise they are not the same file and the old addresses stay.
    /// </summary>
    /// <param name="key">The key of the stream.</param>
    /// <param name="upstreams">The new addresses, in the order they should be tried.</param>
    /// <param name="current">The index of the address to start with.</param>
    /// <param name="headers">The headers of upstream requests from now on; null keeps them.</param>
    /// <param name="cancellationToken">Cancels the check; the old addresses stay.</param>
    public async Task<RelayUpdateResult> UpdateUrlsAsync(
        string key,
        IReadOnlyList<RelayUpstream> upstreams,
        int current = 0,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upstreams);
        ArgumentOutOfRangeException.ThrowIfZero(upstreams.Count, nameof(upstreams));
        ArgumentOutOfRangeException.ThrowIfNegative(current);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(current, upstreams.Count);
        if (!_streams.TryGetValue(key, out var entry))
        {
            return RelayUpdateResult.UnknownKey;
        }

        headers ??= entry.Headers;
        var known = entry.Length;
        var start = current;
        RelayFailureReason? skipped = null;
        if (known >= 0)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token, entry.Removed);
            var verified = -1;
            for (var i = 0; i < upstreams.Count && verified < 0; i++)
            {
                var index = (current + i) % upstreams.Count;
                var (total, reason) = await ProbeAsync(upstreams[index], headers, linked.Token).ConfigureAwait(false);
                if (total is not { } length)
                {
                    skipped ??= reason;
                }
                else if (length != known)
                {
                    return RelayUpdateResult.LengthMismatch;
                }
                else
                {
                    verified = index;
                }
            }

            if (verified < 0)
            {
                return RelayUpdateResult.Unreachable;
            }

            start = verified;
        }

        if (!_streams.TryGetValue(key, out var registered) || !ReferenceEquals(registered, entry))
        {
            return RelayUpdateResult.UnknownKey;
        }

        entry.Replace(upstreams, start, headers);
        if (start != current)
        {
            // The address asked for did not answer the check; the stream starts with one that did.
            Interlocked.Increment(ref entry.Failovers);
            RaiseUpstreamFailed(entry, upstreams[current], upstreams[start], skipped ?? RelayFailureReason.Connect);
        }

        return RelayUpdateResult.Updated;
    }

    /// <summary>
    /// Stops serving a stream and ends the responses that are under way for it.
    /// </summary>
    public bool Unregister(string key)
    {
        if (!_streams.TryRemove(key, out var entry))
        {
            return false;
        }

        entry.Dispose();
        return true;
    }

    /// <summary>
    /// What the relay knows about a stream; null when none is registered under the key.
    /// </summary>
    public RelayStreamStatistics? GetStatistics(string key)
        => _streams.TryGetValue(key, out var entry) ? entry.GetStatistics() : null;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _listener.Dispose();
#pragma warning disable VSTHRD003 // The relay started both; they are joined here so that nothing outlives it.
        await _acceptLoop.ConfigureAwait(false);
        if (Volatile.Read(ref _connections) > 0)
        {
            try
            {
                await _drained.Task.WaitAsync(RelayDefaults.DrainTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Relay | connections did not end in time count={Count}", Volatile.Read(ref _connections));
            }
        }
#pragma warning restore VSTHRD003

        foreach (var key in _streams.Keys)
        {
            Unregister(key);
        }

        _http.Dispose();
        _lifetime.Dispose();
    }

    private static void CancelInBackground(CancellationTokenSource source)
        => ThreadPool.UnsafeQueueUserWorkItem(static state => Cancel(state), source, preferLocal: false);

    private static void Cancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (AggregateException)
        {
            // A callback of pending I/O threw; the I/O itself is cancelled all the same.
        }
        finally
        {
            source.Dispose();
        }
    }

    private static async Task WriteHeadAsync(NetworkStream client, int status, string reason, long length, string? contentType, string? contentRange, CancellationToken token)
    {
        var head = new StringBuilder(256);
        head.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {reason}\r\n");
        if (contentType is not null)
        {
            head.Append(CultureInfo.InvariantCulture, $"Content-Type: {contentType}\r\nAccept-Ranges: bytes\r\n");
        }

        if (contentRange is not null)
        {
            head.Append(CultureInfo.InvariantCulture, $"Content-Range: {contentRange}\r\n");
        }

        head.Append(CultureInfo.InvariantCulture, $"Content-Length: {length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await client.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), token).ConfigureAwait(false);
    }

    private static Task WriteStatusAsync(NetworkStream client, int status, string reason, CancellationToken token)
        => WriteHeadAsync(client, status, reason, 0, null, null, token);

    private async Task AcceptLoopAsync()
    {
        var token = _lifetime.Token;
        var failures = 0;
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                failures = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                // A connection that was reset before it was accepted is no reason to stop listening.
                if (++failures >= 20)
                {
                    _logger.LogWarning("Relay | stopped listening error={Error}", ex.SocketErrorCode);
                    break;
                }

                continue;
            }

            Interlocked.Increment(ref _connections);
            _ = ServeAsync(client, token);
        }
    }

    private async Task ServeAsync(TcpClient connection, CancellationToken lifetime)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(RelayDefaults.BufferBytes);
        try
        {
            // Leave the accept loop at once; the rest runs on the pool.
            await Task.Yield();
            using (connection)
            {
                connection.NoDelay = true;
                var client = connection.GetStream();
                var (complete, request) = await ReadHeadAsync(client, buffer, lifetime).ConfigureAwait(false);
                if (request is not null)
                {
                    await HandleAsync(client, request, buffer, lifetime).ConfigureAwait(false);
                }
                else if (complete)
                {
                    await WriteStatusAsync(client, 400, "Bad Request", lifetime).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The stream was unregistered or the relay is closing.
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // The player closes its connection whenever it seeks or stops: the regular end of a response.
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Relay | request failed error={Error}", ex.GetType().Name);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (Interlocked.Decrement(ref _connections) == 0 && Volatile.Read(ref _disposed) != 0)
            {
                _drained.TrySetResult();
            }
        }
    }

    // Complete is false when the client went away or sent too much before the head ended.
    private async Task<(bool Complete, RelayRequestHead? Request)> ReadHeadAsync(NetworkStream client, byte[] buffer, CancellationToken lifetime)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        timeout.CancelAfter(_options.RequestHeadTimeout);
        var length = 0;
        while (length < RelayDefaults.MaxRequestHeadBytes)
        {
            var read = await client.ReadAsync(buffer.AsMemory(length, RelayDefaults.MaxRequestHeadBytes - length), timeout.Token).ConfigureAwait(false);
            if (read <= 0)
            {
                return (false, null);
            }

            length += read;
            var end = RelayRequestHead.FindEnd(buffer.AsSpan(0, length));
            if (end >= 0)
            {
                return RelayRequestHead.TryParse(buffer.AsSpan(0, end), out var request) ? (true, request) : (true, null);
            }
        }

        return (false, null);
    }

    private async Task HandleAsync(NetworkStream client, RelayRequestHead request, byte[] buffer, CancellationToken lifetime)
    {
        var headOnly = string.Equals(request.Method, "HEAD", StringComparison.Ordinal);
        if (!headOnly && !string.Equals(request.Method, "GET", StringComparison.Ordinal))
        {
            await WriteStatusAsync(client, 405, "Method Not Allowed", lifetime).ConfigureAwait(false);
            return;
        }

        // A key that is not registered is answered like a wrong token: nothing tells the two apart.
        if (!_token.TryGetKey(request.Target, out var key) || !_streams.TryGetValue(key, out var entry))
        {
            await WriteStatusAsync(client, 404, "Not Found", lifetime).ConfigureAwait(false);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, entry.Removed);
        var token = linked.Token;
        Interlocked.Increment(ref entry.Requests);
        Interlocked.Increment(ref entry.ActiveRequests);
        using var transfer = new Transfer(this, entry, client, buffer, token);
        try
        {
            var range = RelayByteRange.Parse(request.Range);
            if (entry.Length < 0)
            {
                // The first upstream answer tells the length. It is asked for the bytes the
                // response starts with, so the connection goes on to serve the body.
                var opened = range switch
                {
                    null => await transfer.EnsureUpstreamAsync(0, null).ConfigureAwait(false),
                    { IsSuffix: false } start => await transfer.EnsureUpstreamAsync(start.First, start.Last).ConfigureAwait(false),
                    _ => await transfer.EnsureUpstreamAsync(0, 0).ConfigureAwait(false),
                };
                if (!opened || entry.Length < 0)
                {
                    await WriteStatusAsync(client, 502, "Bad Gateway", token).ConfigureAwait(false);
                    return;
                }
            }

            var total = entry.Length;
            var resolved = RelayByteRange.Resolve(range, total);
            if (resolved.Kind == RelayRangeKind.Unsatisfiable)
            {
                await WriteHeadAsync(client, 416, "Range Not Satisfiable", 0, null, RelayByteRange.FormatUnsatisfied(total), token).ConfigureAwait(false);
                return;
            }

            var partial = resolved.Kind == RelayRangeKind.Partial;
            await WriteHeadAsync(
                client,
                partial ? 206 : 200,
                partial ? "Partial Content" : "OK",
                resolved.Length,
                entry.ContentType ?? DefaultContentType,
                partial ? RelayByteRange.FormatContentRange(resolved.From, resolved.To, total) : null,
                token).ConfigureAwait(false);
            if (!headOnly)
            {
                await transfer.CopyAsync(resolved.From, resolved.To, bounded: range is { Last: not null }).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref entry.ActiveRequests);
        }
    }

    // Asks an address for one byte to learn the length of what it serves; null when it does not answer.
    private async Task<(long? Total, RelayFailureReason Reason)> ProbeAsync(RelayUpstream upstream, IReadOnlyDictionary<string, string>? headers, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_options.ResponseTimeout);
        try
        {
            var answer = await RequestAsync(upstream, headers, 0, 0, timeout.Token).ConfigureAwait(false);
            answer.Response?.Dispose();
            return (answer.Response is null ? null : answer.Total, answer.Reason);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return (null, RelayFailureReason.Timeout);
        }
    }

    // Sends one ranged request. The caller owns the response of an answer that has one.
    private async Task<UpstreamAnswer> RequestAsync(RelayUpstream upstream, IReadOnlyDictionary<string, string>? headers, long from, long? to, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, upstream.Address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                if (!string.IsNullOrEmpty(value) && !string.Equals(name, "Range", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        request.Headers.Range = new RangeHeaderValue(from, to);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            return new(null, 0, null, RelayFailureReason.Connect);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // The connect timeout of the handler, which is no cancellation of the caller.
            return new(null, 0, null, RelayFailureReason.Timeout);
        }

        var status = (int)response.StatusCode;
        var content = response.Content.Headers;
        if (status == 206 && content.ContentRange is { From: { } first, Length: { } length } && first == from)
        {
            return new(response, length, content.ContentType?.MediaType, default);
        }

        if (status == 200 && from == 0 && content.ContentLength is { } whole)
        {
            // A node that ignores the range still serves the file from its first byte.
            return new(response, whole, content.ContentType?.MediaType, default);
        }

        response.Dispose();
        return new(null, 0, null, status is >= 200 and < 300 ? RelayFailureReason.Unsupported : RelayFailureReason.Status);
    }

    private void RaiseUpstreamFailed(StreamEntry entry, RelayUpstream failed, RelayUpstream current, RelayFailureReason reason)
    {
        _logger.LogDebug("Relay | upstream failed stream={Key} node={Node} reason={Reason} next={Next}", entry.Key, failed.Label, reason, current.Label);
        try
        {
            UpstreamFailed?.Invoke(this, new(entry.Key, failed, current, reason));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Relay | failure handler threw error={Error}", ex.GetType().Name);
        }
    }

    private readonly record struct UpstreamAnswer(HttpResponseMessage? Response, long Total, string? ContentType, RelayFailureReason Reason);

    private sealed class UpstreamConnection(HttpResponseMessage response, Stream body, CancellationTokenSource cancellation, RelayUpstreamSelection selection, long position) : IDisposable
    {
        public Stream Body { get; } = body;

        // Ends with the request, with a change of address, and with the timeout of the read under way.
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public RelayUpstreamSelection Selection { get; } = selection;

        public long Position { get; set; } = position;

        public long Delivered { get; set; }

        public void Dispose()
        {
            response.Dispose();
            Cancellation.Dispose();
        }
    }

    /// <summary>
    /// One registered stream. The table is guarded by its lock; the counters are interlocked.
    /// </summary>
    private sealed class StreamEntry : IDisposable
    {
        // Counters, updated with Interlocked.
        public int Requests;
        public int ActiveRequests;
        public int Failovers;
        public int Handovers;
        public long UpstreamBytes;
        public long PreloadedBytes;
        private readonly Lock _sync = new();
        private readonly RelayUpstreamTable _table;
        private readonly RelayPreload[] _preloaded;
        private readonly RelayRateMeter _rate = new(RelayDefaults.RateWindow);
#pragma warning disable CA2213 // Both are cancelled and disposed on the pool, see Dispose and Signal.
        private readonly CancellationTokenSource _removed = new();
        private CancellationTokenSource _changed = new();
#pragma warning restore CA2213
        private CancellationToken _changedToken;
        private IReadOnlyDictionary<string, string>? _headers;
        private string? _contentType;
        private long _length;
        private long _firstByteTicks = -1;
        private int _disposed;

        public StreamEntry(string key, RelayStreamRegistration registration)
        {
            Key = key;
            _table = new(registration.Upstreams, registration.Current);
            _headers = registration.Headers;
            _preloaded = registration.Preloaded is { } preloaded ? [.. preloaded] : [];
            _length = registration.Length is >= 0 and var length ? length : -1;
            _changedToken = _changed.Token;
            Removed = _removed.Token;
        }

        public string Key { get; }

        // Cancelled when the stream is unregistered.
        public CancellationToken Removed { get; }

        public IReadOnlyDictionary<string, string>? Headers => Volatile.Read(ref _headers);

        public string? ContentType => Volatile.Read(ref _contentType);

        public long Length => Interlocked.Read(ref _length);

        public int Count
        {
            get
            {
                lock (_sync)
                {
                    return _table.Count;
                }
            }
        }

        public int Version
        {
            get
            {
                lock (_sync)
                {
                    return _table.Version;
                }
            }
        }

        // The address in use, and a token that is cancelled as soon as it stops being the one in use.
        public (RelayUpstreamSelection Selection, CancellationToken Changed) Snapshot()
        {
            lock (_sync)
            {
                return (_table.Current, _changedToken);
            }
        }

        public bool Select(string url)
        {
            CancellationTokenSource? retired;
            lock (_sync)
            {
                var version = _table.Version;
                if (!_table.Select(_table.IndexOf(url)))
                {
                    return false;
                }

                retired = _table.Version != version ? RetireChangeSignal() : null;
            }

            Signal(retired);
            return true;
        }

        public bool TryRotate(int version, out RelayUpstreamSelection next)
        {
            CancellationTokenSource? retired;
            bool rotated;
            lock (_sync)
            {
                rotated = _table.TryRotate(version, out next);
                retired = rotated ? RetireChangeSignal() : null;
            }

            Signal(retired);
            return rotated;
        }

        public void Replace(IReadOnlyList<RelayUpstream> upstreams, int current, IReadOnlyDictionary<string, string>? headers)
        {
            CancellationTokenSource? retired;
            lock (_sync)
            {
                _table.Replace(upstreams, current);
                Volatile.Write(ref _headers, headers);
                retired = RetireChangeSignal();
            }

            Signal(retired);
        }

        // False when the stream is already known to have another length.
        public bool TrySetLength(long length)
            => Interlocked.CompareExchange(ref _length, length, -1) is var known && (known < 0 || known == length);

        public void NoteAnswer(TimeSpan firstByte, string? contentType)
        {
            Interlocked.Exchange(ref _firstByteTicks, firstByte.Ticks);
            if (!string.IsNullOrEmpty(contentType))
            {
                Interlocked.CompareExchange(ref _contentType, contentType, null);
            }
        }

        public void AddUpstreamBytes(int count)
        {
            Interlocked.Add(ref UpstreamBytes, count);
            _rate.Add(Environment.TickCount64, count);
        }

        public RelayPreload? FindPreload(long offset)
        {
            foreach (var preload in _preloaded)
            {
                if (offset >= preload.Offset && offset < preload.Offset + preload.Data.Length)
                {
                    return preload;
                }
            }

            return null;
        }

        // The offset of the first preloaded part that starts after the offset.
        public long FindNextPreload(long offset)
        {
            var next = long.MaxValue;
            foreach (var preload in _preloaded)
            {
                if (preload.Offset > offset && preload.Offset < next)
                {
                    next = preload.Offset;
                }
            }

            return next;
        }

        public RelayStreamStatistics GetStatistics()
        {
            RelayUpstream current;
            lock (_sync)
            {
                current = _table.Current.Upstream;
            }

            var firstByte = Interlocked.Read(ref _firstByteTicks);
            var length = Length;
            return new(
                Interlocked.Read(ref UpstreamBytes),
                Interlocked.Read(ref PreloadedBytes),
                _rate.GetRate(Environment.TickCount64),
                firstByte < 0 ? null : TimeSpan.FromTicks(firstByte),
                current,
                Volatile.Read(ref Requests),
                Volatile.Read(ref ActiveRequests),
                length < 0 ? null : length,
                Volatile.Read(ref Failovers),
                Volatile.Read(ref Handovers));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            CancellationTokenSource changed;
            lock (_sync)
            {
                changed = _changed;
            }

            // Cancelling runs the callbacks of pending I/O; none of that belongs on the caller's thread.
            // The removal is signalled first, so that no response takes the end of the stream for a
            // change of address.
            ThreadPool.UnsafeQueueUserWorkItem(
                static state =>
                {
                    Cancel(state.Removed);
                    Cancel(state.Changed);
                },
                (Removed: _removed, Changed: changed),
                preferLocal: false);
        }

        // Call while holding the lock; the returned source is cancelled outside of it.
        private CancellationTokenSource? RetireChangeSignal()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return null;
            }

            var retired = _changed;
            _changed = new();
            _changedToken = _changed.Token;
            return retired;
        }

        private static void Signal(CancellationTokenSource? retired)
        {
            if (retired is not null)
            {
                CancelInBackground(retired);
            }
        }
    }

    /// <summary>
    /// The body of one response: copies bytes to the player and keeps an upstream connection
    /// positioned where the next byte comes from.
    /// </summary>
    private sealed class Transfer(LoopbackMediaRelay relay, StreamEntry entry, NetworkStream client, byte[] buffer, CancellationToken token) : IDisposable
    {
        private UpstreamConnection? _upstream;

        // Upstream requests that failed since the last byte arrived.
        private int _failures;

        public void Dispose() => CloseUpstream();

        // Sends the bytes from..to, from memory where the stream has them preloaded and from
        // upstream otherwise. Returns early when every address failed; the player then sees the
        // body end short and asks again.
        public async Task CopyAsync(long from, long to, bool bounded)
        {
            var offset = from;
            while (offset <= to)
            {
                if (entry.FindPreload(offset) is { } preload)
                {
                    var skip = (int)(offset - preload.Offset);
                    var count = (int)Math.Min(preload.Data.Length - skip, to - offset + 1);
                    await client.WriteAsync(preload.Data.Slice(skip, count), token).ConfigureAwait(false);
                    offset += count;
                    Interlocked.Add(ref entry.PreloadedBytes, count);
                    continue;
                }

                if (!await EnsureUpstreamAsync(offset, bounded ? to : null).ConfigureAwait(false))
                {
                    return;
                }

                var upstream = _upstream!;

                // Never past the start of a preloaded part: what is in memory is served from memory.
                var wanted = (int)Math.Min(buffer.Length, Math.Min(to + 1, entry.FindNextPreload(offset)) - offset);
                var read = 0;
                RelayFailureReason? failure = null;
                var started = Stopwatch.GetTimestamp();
                try
                {
                    // Only the wait for upstream data is timed: a player that reads slowly is no stalled node.
                    upstream.Cancellation.CancelAfter(relay._options.ReadTimeout);
                    read = await upstream.Body.ReadAsync(buffer.AsMemory(0, wanted), upstream.Cancellation.Token).ConfigureAwait(false);
                    upstream.Cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
                    if (read <= 0)
                    {
                        // The body ended before the bytes that were asked for.
                        failure = RelayFailureReason.Read;
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    if (entry.Version != upstream.Selection.Version)
                    {
                        // Another address was chosen: go on from it at this byte.
                        continue;
                    }

                    if (Stopwatch.GetElapsedTime(started) < relay._options.ReadTimeout / 2)
                    {
                        // The timer of the read before fired just as that read completed: no stall.
                        CloseUpstream();
                        continue;
                    }

                    failure = RelayFailureReason.Timeout;
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or SocketException)
                {
                    failure = RelayFailureReason.Read;
                }

                if (failure is { } reason)
                {
                    var selection = upstream.Selection;
                    var delivered = upstream.Delivered;
                    CloseUpstream();
                    if (reason == RelayFailureReason.Read && delivered > 0)
                    {
                        // A node that had been delivering closes a connection that sat idle while the
                        // player was not reading. It is asked once more before it counts as failed.
                        continue;
                    }

                    if (!Fail(selection, reason))
                    {
                        return;
                    }

                    continue;
                }

                await client.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                offset += read;
                upstream.Position = offset;
                upstream.Delivered += read;
                _failures = 0;
                entry.AddUpstreamBytes(read);
            }
        }

        // Makes sure an upstream response is open at the address in use and positioned at the
        // offset. False when every address failed.
        public async Task<bool> EnsureUpstreamAsync(long offset, long? last)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var (selection, changed) = entry.Snapshot();
                if (_upstream is { } open && open.Selection.Version == selection.Version && open.Position == offset)
                {
                    return true;
                }

                var handover = _upstream is { } previous && previous.Selection.Version != selection.Version;
                CloseUpstream();
                var (connection, reason, superseded) = await OpenAsync(selection, changed, offset, last).ConfigureAwait(false);
                if (connection is not null)
                {
                    _upstream = connection;
                    if (handover)
                    {
                        Interlocked.Increment(ref entry.Handovers);
                    }

                    return true;
                }

                if (!superseded && !Fail(selection, reason))
                {
                    return false;
                }
            }
        }

        private async Task<(UpstreamConnection? Connection, RelayFailureReason Reason, bool Superseded)> OpenAsync(
            RelayUpstreamSelection selection, CancellationToken changed, long offset, long? last)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, changed);
            HttpResponseMessage? response = null;
            try
            {
                cancellation.CancelAfter(relay._options.ResponseTimeout);
                var started = Stopwatch.GetTimestamp();
                var answer = await relay.RequestAsync(selection.Upstream, entry.Headers, offset, last, cancellation.Token).ConfigureAwait(false);
                response = answer.Response;
                if (response is null)
                {
                    cancellation.Dispose();
                    return (null, answer.Reason, false);
                }

                if (!entry.TrySetLength(answer.Total))
                {
                    // Not the file the player has been reading: its bytes must not be mixed in.
                    response.Dispose();
                    cancellation.Dispose();
                    return (null, RelayFailureReason.Length, false);
                }

                var body = await response.Content.ReadAsStreamAsync(cancellation.Token).ConfigureAwait(false);
                cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
                entry.NoteAnswer(Stopwatch.GetElapsedTime(started), answer.ContentType);
                return (new(response, body, cancellation, selection, offset), default, false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                response?.Dispose();
                cancellation.Dispose();
                return entry.Version != selection.Version ? (null, default, true) : (null, RelayFailureReason.Timeout, false);
            }
            catch
            {
                response?.Dispose();
                cancellation.Dispose();
                throw;
            }
        }

        // Counts a failed address and moves the stream on to the next one, unless somebody else
        // already did. False once every address has failed without a byte in between.
        private bool Fail(RelayUpstreamSelection selection, RelayFailureReason reason)
        {
            // The end of the stream or of the relay is no failure of a node.
            token.ThrowIfCancellationRequested();
            _failures++;
            if (entry.TryRotate(selection.Version, out var next))
            {
                Interlocked.Increment(ref entry.Failovers);
                relay.RaiseUpstreamFailed(entry, selection.Upstream, next.Upstream, reason);
            }

            if (_failures < Math.Max(entry.Count, RelayDefaults.MinimumAttempts))
            {
                return true;
            }

            relay._logger.LogWarning("Relay | gave up stream={Key} reason={Reason} attempts={Attempts}", entry.Key, reason, _failures);
            return false;
        }

        private void CloseUpstream()
        {
            _upstream?.Dispose();
            _upstream = null;
        }
    }
}
