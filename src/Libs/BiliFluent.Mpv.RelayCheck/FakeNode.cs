// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BiliFluent.Mpv.RelayCheck;

/// <summary>
/// How a fake node answers.
/// </summary>
internal enum NodeMode
{
    /// <summary>Serves the range that was asked for.</summary>
    Ok,

    /// <summary>Answers every request with 403.</summary>
    Forbidden,

    /// <summary>Sends <see cref="FakeNode.Limit"/> bytes of the body, closes, and is <see cref="Forbidden"/> from then on.</summary>
    CutOnce,

    /// <summary>Sends <see cref="FakeNode.Limit"/> bytes of the body, then nothing, with the connection open.</summary>
    StallBody,

    /// <summary>Accepts the connection and never answers.</summary>
    StallHead,
}

/// <summary>
/// One CDN node on the loopback interface. Every node serves the same file: a byte pattern
/// that is computed from the offset, so nothing has to be held in memory.
/// </summary>
internal sealed class FakeNode : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly Task _loop;
    private long _served;
    private int _requests;
    private int _open;

    public FakeNode(string name, long length)
    {
        Name = name;
        Length = length;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptAsync);
    }

    public string Name { get; }

    public int Port { get; }

    public string Url => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{Port}/file.m4s?deadline=1&sign={Name}");

    /// <summary>The length the node claims and serves.</summary>
    public long Length { get; set; }

    public NodeMode Mode { get; set; }

    /// <summary>Body bytes sent before <see cref="NodeMode.CutOnce"/> or <see cref="NodeMode.StallBody"/> takes effect.</summary>
    public long Limit { get; set; }

    /// <summary>Bytes per second; 0 is as fast as the client reads.</summary>
    public int Throttle { get; set; }

    public long Served => Interlocked.Read(ref _served);

    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Requests that are still being answered. A connection that never sent one, as a connection pool keeps them, does not count.</summary>
    public int Open => Volatile.Read(ref _open);

    /// <summary>The request heads received, in order.</summary>
    public ConcurrentQueue<string> Heads { get; } = new();

    public static byte At(long offset) => (byte)((offset * 31) ^ (offset >> 8) ^ (offset >> 16));

    public static void Fill(Span<byte> buffer, long offset)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = At(offset + i);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Dispose();
        await _loop;
        await Task.WhenAll(_connections);
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _connections.Add(Task.Run(() => ServeAsync(client)));
        }
    }

    private async Task ServeAsync(TcpClient connection)
    {
        var counted = false;
        try
        {
            using (connection)
            {
                var token = _stop.Token;
                var stream = connection.GetStream();
                var head = new byte[8192];
                var length = 0;
                while (!Encoding.ASCII.GetString(head, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(head.AsMemory(length), token);
                    if (read <= 0)
                    {
                        return;
                    }

                    length += read;
                }

                var text = Encoding.ASCII.GetString(head, 0, length);
                Heads.Enqueue(text);
                Interlocked.Increment(ref _requests);
                Interlocked.Increment(ref _open);
                counted = true;
                var mode = Mode;
                if (mode == NodeMode.StallHead)
                {
                    await Task.Delay(Timeout.Infinite, token);
                }

                if (mode == NodeMode.Forbidden)
                {
                    await stream.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
                    return;
                }

                long from = 0;
                var to = Length - 1;
                foreach (var line in text.Split("\r\n"))
                {
                    if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line["Range: bytes=".Length..].Split('-');
                        from = long.Parse(parts[0], CultureInfo.InvariantCulture);
                        if (parts[1].Length > 0)
                        {
                            to = Math.Min(to, long.Parse(parts[1], CultureInfo.InvariantCulture));
                        }
                    }
                }

                if (from >= Length)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{Length}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token);
                    return;
                }

                await stream.WriteAsync(
                    Encoding.ASCII.GetBytes($"HTTP/1.1 206 Partial Content\r\nContent-Type: video/mp4\r\nContent-Range: bytes {from}-{to}/{Length}\r\nContent-Length: {to - from + 1}\r\nConnection: close\r\n\r\n"),
                    token);
                var buffer = new byte[16 * 1024];
                var offset = from;
                long sent = 0;
                while (offset <= to)
                {
                    var count = (int)Math.Min(buffer.Length, to - offset + 1);
                    if (mode is NodeMode.CutOnce or NodeMode.StallBody)
                    {
                        count = (int)Math.Min(count, Limit - sent);
                        if (count <= 0)
                        {
                            if (mode == NodeMode.CutOnce)
                            {
                                Mode = NodeMode.Forbidden;
                                return;
                            }

                            await Task.Delay(Timeout.Infinite, token);
                        }
                    }

                    Fill(buffer.AsSpan(0, count), offset);
                    await stream.WriteAsync(buffer.AsMemory(0, count), token);
                    offset += count;
                    sent += count;
                    Interlocked.Add(ref _served, count);
                    if (Throttle > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds((double)count / Throttle), token);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // The relay went away, or the check is over.
        }
        finally
        {
            if (counted)
            {
                Interlocked.Decrement(ref _open);
            }
        }
    }
}
