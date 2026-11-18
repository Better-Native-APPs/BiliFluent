// Copyright (c) Bili Copilot. All rights reserved.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Core.Enums;

namespace BiliFluent.Mpv.RelayCheck;

internal static partial class AudioResumeSmoke
{
    private static async Task RunNativePlayerStateChecksAsync(Report report, string dll, string output)
    {
        await using var client = await MpvClient.CreateAsync(dll, new() { UseConfig = false, LoadScripts = false });
        foreach (var (name, value) in new (string, string)[]
        {
            ("vo", "null"), ("ao", "wasapi"), ("mute", "yes"), ("volume", "0"), ("audio-exclusive", "no"),
            ("terminal", "no"), ("idle", "yes"), ("keep-open", "yes"), ("keep-open-pause", "yes"),
            ("cache", "yes"), ("demuxer-max-bytes", "64KiB"), ("demuxer-max-back-bytes", "0"),
            ("demuxer-seekable-cache", "no"), ("log-file", Path.Combine(output, "native-state.mpv.log")),
        }) Ensure(client.SetOptionString(name, value), name);

        var state = await client.GetPlayerStateAsync();
        report.Check("native state: unloaded player reports Idle", state.IsSuccess && state.Value == MpvPlayerState.Idle);
        var ending = Path.Combine(output, "native-state-ending.wav");
        await File.WriteAllBytesAsync(ending, SilentWave(0.4));
        await client.PlayAsync(ending);
        var ended = await WaitAsync(() => Flag(client, "eof-reached") && Flag(client, "pause"), 5_000);
        state = await client.GetPlayerStateAsync();
        report.Check("native state: keep-open user pause at EOF still reports End", ended && state.IsSuccess && state.Value == MpvPlayerState.End);

        await using var media = new NativeStateMediaNode(SilentWave(9));
        await client.PlayAsync(media.Url);
        var started = await WaitAsync(() => ReadDouble(client, "time-pos") > 0.1 && double.IsFinite(ReadDouble(client, "audio-pts")), 5_000);
        report.Check("native state: local fixture starts on muted WASAPI", started && Flag(client, "mute") && client.GetPropertyString("current-ao") == "wasapi");
        Ensure(await client.PauseAsync(), "native state pause");
        media.HoldReads();
        Ensure(client.Command("seek", "6", "absolute+exact"), "native state paused seek");
        var seeking = await WaitAsync(() => Flag(client, "seeking"), 5_000);
        state = await client.GetPlayerStateAsync();
        report.Check("native state: paused seek retains Seeking priority", seeking && Flag(client, "pause") && state.IsSuccess && state.Value == MpvPlayerState.Seeking);
        media.ReleaseReads();
        await WaitAsync(() => !Flag(client, "seeking"), 5_000);
        state = await client.GetPlayerStateAsync();
        report.Check("native state: completed paused seek reports Paused", state.IsSuccess && state.Value == MpvPlayerState.Paused);
        Ensure(client.Command("stop"), "native state stop");
        await WaitAsync(() => Flag(client, "idle-active"), 5_000);
        state = await client.GetPlayerStateAsync();
        report.Check("native state: stopped player reports Idle", state.IsSuccess && state.Value == MpvPlayerState.Idle);
        report.Check("native state: all fixture playback remains muted", Flag(client, "mute"));
    }

    private static byte[] SilentWave(double seconds)
    {
        var bytes = new byte[44 + (int)(48_000 * seconds) * 2];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 48_000);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 96_000);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), bytes.Length - 44);
        return bytes;
    }

    private sealed class NativeStateMediaNode : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _loop;
        private readonly byte[] _bytes;
        private bool _hold;

        public NativeStateMediaNode(byte[] bytes)
        {
            _bytes = bytes;
            _listener.Start();
            Url = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/fixture.wav");
            _loop = AcceptAsync();
        }

        public string Url { get; }
        public void HoldReads() => Volatile.Write(ref _hold, true);
        public void ReleaseReads() => _release.TrySetResult();

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                    _connections.Add(ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token)));
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }

        private async Task ServeAsync(TcpClient connection)
        {
            try
            {
                using (connection)
                {
                    var token = _stop.Token;
                    var stream = connection.GetStream();
                    var header = new byte[8192];
                    var count = 0;
                    while (!Encoding.ASCII.GetString(header, 0, count).Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(header.AsMemory(count), token);
                        if (read == 0) return;
                        count += read;
                    }
                    var from = 0;
                    foreach (var line in Encoding.ASCII.GetString(header, 0, count).Split("\r\n"))
                        if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                            from = int.Parse(line["Range: bytes=".Length..].Split('-')[0], CultureInfo.InvariantCulture);
                    if (from >= _bytes.Length) return;
                    var response = string.Create(CultureInfo.InvariantCulture,
                        $"HTTP/1.1 206 Partial Content\r\nContent-Type: audio/wav\r\nContent-Range: bytes {from}-{_bytes.Length - 1}/{_bytes.Length}\r\nContent-Length: {_bytes.Length - from}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token);
                    while (from < _bytes.Length)
                    {
                        if (Volatile.Read(ref _hold)) await _release.Task.WaitAsync(token);
                        var length = Math.Min(16 * 1024, _bytes.Length - from);
                        await stream.WriteAsync(_bytes.AsMemory(from, length), token);
                        from += length;
                        await Task.Delay(10, token);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Dispose();
            await _loop;
            await Task.WhenAll(_connections);
            _stop.Dispose();
        }
    }
}
