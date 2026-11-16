// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Richasy.MpvKernel.Relay;

namespace BiliFluent.Mpv.RelayCheck;

/// <summary>
/// The relay against fake nodes on the loopback interface: what the player receives must be the
/// file, byte for byte, whatever happens to the nodes behind it.
/// </summary>
internal static class RelayChecks
{
    private const long FileLength = (3 * 1024 * 1024) + 12345;
    private static readonly LoopbackMediaRelayOptions FastOptions = new()
    {
        ConnectTimeout = TimeSpan.FromMilliseconds(800),
        ResponseTimeout = TimeSpan.FromMilliseconds(800),
        ReadTimeout = TimeSpan.FromMilliseconds(800),
        RequestHeadTimeout = TimeSpan.FromMilliseconds(800),
    };

    private static readonly Dictionary<string, string> UpstreamHeaders = new()
    {
        ["User-Agent"] = "RelayCheck/1.0 (test)",
        ["Referer"] = "https://referer.example",
        ["Cookie"] = "SESSDATA=upstream-secret",
    };

    public static async Task RunAsync(Report report)
    {
        await report.RunAsync("requests", () => RequestsAsync(report));
        await report.RunAsync("headers", () => HeadersAsync(report));
        await report.RunAsync("handover", () => HandoverAsync(report));
        await report.RunAsync("failover", () => FailoverAsync(report));
        await report.RunAsync("length", () => LengthAsync(report));
        await report.RunAsync("update", () => UpdateAsync(report));
        await report.RunAsync("preload", () => PreloadAsync(report));
        await report.RunAsync("backpressure", () => BackpressureAsync(report));
        await report.RunAsync("lifetime", () => LifetimeAsync(report));
        await report.RunAsync("stress", () => StressAsync(report));
    }

    private static HttpClient CreateClient()
        => new(new SocketsHttpHandler { UseProxy = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

    private static RelayUpstream Upstream(FakeNode node) => new(node.Url, node.Name);

    // A port nothing listens on: connecting is refused at once.
    private static RelayUpstream Dead(string name)
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new($"http://127.0.0.1:{port}/file.m4s?sign={name}", name);
    }

    private static RelayStreamRegistration Registration(params RelayUpstream[] upstreams)
        => new() { Upstreams = upstreams, Headers = UpstreamHeaders };

    // Reads a response body and compares every byte with the file, from the given offset on.
    private static async Task<(long Count, bool Matches)> ReadAsync(Stream body, long offset, long? stopAfter = null, int chunk = 32 * 1024)
    {
        var buffer = new byte[chunk];
        var expected = new byte[chunk];
        long count = 0;
        var matches = true;
        while (stopAfter is null || count < stopAfter)
        {
            var wanted = stopAfter is { } stop ? (int)Math.Min(chunk, stop - count) : chunk;
            int read;
            try
            {
                read = await body.ReadAsync(buffer.AsMemory(0, wanted));
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                // The relay ended the body before the promised length.
                break;
            }

            if (read <= 0)
            {
                break;
            }

            FakeNode.Fill(expected.AsSpan(0, read), offset + count);
            matches &= buffer.AsSpan(0, read).SequenceEqual(expected.AsSpan(0, read));
            count += read;
        }

        return (count, matches);
    }

    private static async Task<(HttpStatusCode Status, long Count, bool Matches, HttpResponseMessage Response)> GetAsync(HttpClient client, string url, long? from = null, long? to = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (from is not null || to is not null)
        {
            request.Headers.Range = new RangeHeaderValue(from, to);
        }

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        var start = response.Content.Headers.ContentRange?.From ?? 0;
        var (count, matches) = await ReadAsync(body, start);
        return (response.StatusCode, count, matches, response);
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int milliseconds = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > milliseconds)
            {
                return false;
            }

            await Task.Delay(20);
        }

        return true;
    }

    private static async Task RequestsAsync(Report report)
    {
        await using var node = new FakeNode("a", FileLength);
        await using var relay = LoopbackMediaRelay.Start(FastOptions);
        using var client = CreateClient();
        var url = relay.Register("v1", Registration(Upstream(node)));
        report.Check("requests: url is loopback with a token", url.StartsWith($"http://127.0.0.1:{relay.Port}/", StringComparison.Ordinal) && url.EndsWith("/v1", StringComparison.Ordinal) && url.Length > 80);

        var whole = await GetAsync(client, url);
        report.Check("requests: whole file", whole is { Status: HttpStatusCode.OK, Count: FileLength, Matches: true });
        report.Equal("requests: content type passed on", "video/mp4", whole.Response.Content.Headers.ContentType?.MediaType);
        report.Equal("requests: accept-ranges", "bytes", string.Join(',', whole.Response.Headers.AcceptRanges));

        var open = await GetAsync(client, url, 1_000_000);
        report.Check("requests: open range", open is { Status: HttpStatusCode.PartialContent, Count: FileLength - 1_000_000, Matches: true });
        report.Equal("requests: content-range", $"bytes 1000000-{FileLength - 1}/{FileLength}", open.Response.Content.Headers.ContentRange?.ToString());

        var closed = await GetAsync(client, url, 4096, 8191);
        report.Check("requests: closed range", closed is { Status: HttpStatusCode.PartialContent, Count: 4096, Matches: true });
        var clamped = await GetAsync(client, url, FileLength - 10, FileLength + 500);
        report.Check("requests: range clamped to the end", clamped is { Status: HttpStatusCode.PartialContent, Count: 10, Matches: true });
        var suffix = await GetAsync(client, url, null, 100);
        report.Check("requests: suffix range", suffix is { Status: HttpStatusCode.PartialContent, Count: 100, Matches: true });
        var beyond = await GetAsync(client, url, FileLength);
        report.Check("requests: range past the end", beyond is { Status: HttpStatusCode.RequestedRangeNotSatisfiable, Count: 0 });
        report.Equal("requests: 416 names the length", $"bytes */{FileLength}", beyond.Response.Content.Headers.ContentRange?.ToString());

        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
        report.Check("requests: head", head.StatusCode == HttpStatusCode.OK && head.Content.Headers.ContentLength == FileLength);
        using var post = await client.PostAsync(url, new StringContent("x"));
        report.Equal("requests: other method", HttpStatusCode.MethodNotAllowed, post.StatusCode);

        var slash = url.LastIndexOf('/');
        using var unknown = await client.GetAsync(url[..slash] + "/v2");
        report.Equal("requests: unknown key", HttpStatusCode.NotFound, unknown.StatusCode);
        using var wrong = await client.GetAsync($"http://127.0.0.1:{relay.Port}/{new string('0', 64)}/v1");
        report.Equal("requests: wrong token", HttpStatusCode.NotFound, wrong.StatusCode);
        using var bare = await client.GetAsync($"http://127.0.0.1:{relay.Port}/v1");
        report.Equal("requests: no token", HttpStatusCode.NotFound, bare.StatusCode);
        using var root = await client.GetAsync($"http://127.0.0.1:{relay.Port}/");
        report.Equal("requests: root", HttpStatusCode.NotFound, root.StatusCode);
        var requestsBefore = node.Requests;
        using var probe = await client.GetAsync($"http://127.0.0.1:{relay.Port}/{new string('0', 64)}/v1");
        report.Equal("requests: a rejected request reaches no node", requestsBefore, node.Requests);

        // Not HTTP at all, and a client that connects and says nothing.
        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, relay.Port);
            await raw.GetStream().WriteAsync("hello\r\n\r\n"u8.ToArray());
            var answer = new byte[64];
            var read = await raw.GetStream().ReadAsync(answer);
            report.Check("requests: not http", Encoding.ASCII.GetString(answer, 0, read).StartsWith("HTTP/1.1 400", StringComparison.Ordinal));
        }

        using (var silent = new TcpClient())
        {
            await silent.ConnectAsync(IPAddress.Loopback, relay.Port);
            var watch = Stopwatch.StartNew();
            var read = await silent.GetStream().ReadAsync(new byte[16]);
            report.Check("requests: silent client is dropped", read == 0 && watch.ElapsedMilliseconds < 5000, watch.ElapsedMilliseconds + " ms");
        }

        var threw = false;
        try
        {
            relay.Register("v1", Registration(Upstream(node)));
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        report.Check("requests: key in use", threw);
        var stats = relay.GetStatistics("v1");
        report.Check("requests: statistics", stats is { Length: FileLength, ActiveRequests: 0, Failovers: 0, Handovers: 0, FirstByteTime: not null } && stats.Value.UpstreamBytes > FileLength && stats.Value.Current.Label == "a");
        report.Check("requests: statistics of unknown key", relay.GetStatistics("nope") is null);
    }

    private static async Task HeadersAsync(Report report)
    {
        await using var node = new FakeNode("a", FileLength);
        await using var relay = LoopbackMediaRelay.Start(FastOptions);
        using var client = CreateClient();
        var url = relay.Register("v1", Registration(Upstream(node)));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(10, 19);
        request.Headers.TryAddWithoutValidation("Cookie", "client-cookie=1");
        request.Headers.TryAddWithoutValidation("X-Client", "client-header");
        request.Headers.TryAddWithoutValidation("User-Agent", "client-agent");
        using var response = await client.SendAsync(request);
        await response.Content.ReadAsByteArrayAsync();
        node.Heads.TryDequeue(out var head);
        head ??= string.Empty;
        report.Check("headers: user agent, referer and cookie go upstream", head.Contains("User-Agent: RelayCheck/1.0 (test)\r\n", StringComparison.Ordinal)
            && head.Contains("Referer: https://referer.example\r\n", StringComparison.Ordinal) && head.Contains("Cookie: SESSDATA=upstream-secret\r\n", StringComparison.Ordinal));
        report.Check("headers: nothing of the client goes upstream", !head.Contains("client-", StringComparison.Ordinal));
        report.Check("headers: range is translated", head.Contains("Range: bytes=10-19\r\n", StringComparison.Ordinal));
        report.Check("headers: signed query is kept", head.StartsWith("GET /file.m4s?deadline=1&sign=a HTTP/1.1\r\n", StringComparison.Ordinal));
        report.Check("headers: nothing upstream reaches the client", !response.Headers.Contains("Set-Cookie") && response.Headers.CacheControl?.NoStore == true);
    }

    private static async Task HandoverAsync(Report report)
    {
        await using var a = new FakeNode("a", FileLength) { Throttle = 400_000 };
        await using var b = new FakeNode("b", FileLength);
        await using var relay = LoopbackMediaRelay.Start(FastOptions);
        using var client = CreateClient();
        var url = relay.Register("v1", Registration(Upstream(a), Upstream(b)));
        report.Check("handover: unknown address", !relay.SwitchNode("v1", "http://127.0.0.1:1/other") && !relay.SwitchNode("nope", b.Url));

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        var first = await ReadAsync(body, 0, 200_000);
        report.Check("handover: before", first is { Count: 200_000, Matches: true } && b.Served == 0);
        var watch = Stopwatch.StartNew();
        report.Check("handover: switch accepted", relay.SwitchNode("v1", b.Url));
        var rest = await ReadAsync(body, 200_000);
        report.Check("handover: one response, every byte right", rest.Matches && first.Count + rest.Count == FileLength, $"{first.Count + rest.Count} bytes, {watch.ElapsedMilliseconds} ms after the switch");
        report.Check("handover: the rest came from the other node", b.Served > FileLength / 2 && a.Served < FileLength / 2 && a.Served + b.Served >= FileLength, $"a={a.Served} b={b.Served}");
        report.Equal("handover: the player made one request", 1, relay.GetStatistics("v1")?.Requests);
        report.Check("handover: counted, and no failure", relay.GetStatistics("v1") is { Handovers: 1, Failovers: 0 } stats && stats.Current.Label == "b");
        report.Check("handover: the old node was let go", await WaitAsync(() => a.Open == 0), $"open={a.Open}");
        report.Check("handover: same address again changes nothing", relay.SwitchNode("v1", b.Url) && relay.GetStatistics("v1")?.Handovers == 1);

        // A switch while the node is silent must not wait for its timeout.
        await using var c = new FakeNode("c", FileLength) { Mode = NodeMode.StallBody, Limit = 50_000 };
        await using var slow = LoopbackMediaRelay.Start(new() { ReadTimeout = TimeSpan.FromSeconds(30), ResponseTimeout = TimeSpan.FromSeconds(30) });
        var stalled = slow.Register("v1", Registration(Upstream(c), Upstream(b)));
        using var second = await client.GetAsync(stalled, HttpCompletionOption.ResponseHeadersRead);
        await using var secondBody = await second.Content.ReadAsStreamAsync();
        var head = await ReadAsync(secondBody, 0, 50_000);
        await Task.Delay(200);
        watch.Restart();
        slow.SwitchNode("v1", b.Url);
        var tail = await ReadAsync(secondBody, 50_000);
        report.Check("handover: interrupts a silent node at once", head.Matches && tail.Matches && head.Count + tail.Count == FileLength && watch.ElapsedMilliseconds < 5000, watch.ElapsedMilliseconds + " ms");
    }

    private static async Task FailoverAsync(Report report)
    {
        using var client = CreateClient();
        await using var good = new FakeNode("good", FileLength);

        // Cannot connect.
        await using (var relay = LoopbackMediaRelay.Start(FastOptions))
        {
            var failures = new ConcurrentQueue<RelayUpstreamFailedEventArgs>();
            relay.UpstreamFailed += (_, e) => failures.Enqueue(e);
            var url = relay.Register("v1", Registration(Dead("dead"), Upstream(good)));
            var result = await GetAsync(client, url, 500);
            report.Check("failover: dead node", result is { Status: HttpStatusCode.PartialContent, Count: FileLength - 500, Matches: true });
            report.Check("failover: dead node reported", failures.Count == 1 && failures.TryPeek(out var e) && e is { Key: "v1", Reason: RelayFailureReason.Connect or RelayFailureReason.Timeout } && e.Failed.Label == "dead" && e.Current.Label == "good", failures.TryPeek(out var seen) ? seen.Reason.ToString() : "none");
            report.Check("failover: stays on the node that works", relay.GetStatistics("v1") is { Failovers: 1 } stats && stats.Current.Label == "good");
            var again = await GetAsync(client, url, 0, 99);
            report.Check("failover: the dead node is not asked again", again is { Count: 100, Matches: true } && failures.Count == 1);
        }

        // Answers with an error status.
        await using (var relay = LoopbackMediaRelay.Start(FastOptions))
        await using (var forbidden = new FakeNode("forbidden", FileLength) { Mode = NodeMode.Forbidden })
        {
            var failures = new ConcurrentQueue<RelayUpstreamFailedEventArgs>();
            relay.UpstreamFailed += (_, e) => failures.Enqueue(e);
            var result = await GetAsync(client, relay.Register("v1", Registration(Upstream(forbidden), Upstream(good))));
            report.Check("failover: error status", result is { Status: HttpStatusCode.OK, Count: FileLength, Matches: true } && failures.TryPeek(out var e) && e.Reason == RelayFailureReason.Status);
        }

        // Breaks in the middle of the body.
        await using (var relay = LoopbackMediaRelay.Start(FastOptions))
        await using (var cut = new FakeNode("cut", FileLength) { Mode = NodeMode.CutOnce, Limit = 300_000 })
        {
            var failures = new ConcurrentQueue<RelayUpstreamFailedEventArgs>();
            relay.UpstreamFailed += (_, e) => failures.Enqueue(e);
            var result = await GetAsync(client, relay.Register("v1", Registration(Upstream(cut), Upstream(good))));
            report.Check("failover: body cut short", result is { Status: HttpStatusCode.OK, Count: FileLength, Matches: true }, $"{result.Count} bytes");
            report.Check("failover: cut node was asked once more, then left", cut.Requests == 2 && failures.Count == 1 && cut.Served == 300_000, $"requests={cut.Requests} failures={failures.Count}");
        }

        // Connected, delivering, then silent.
        await using (var relay = LoopbackMediaRelay.Start(FastOptions))
        await using (var stall = new FakeNode("stall", FileLength) { Mode = NodeMode.StallBody, Limit = 100_000 })
        {
            var failures = new ConcurrentQueue<RelayUpstreamFailedEventArgs>();
            relay.UpstreamFailed += (_, e) => failures.Enqueue(e);
            var watch = Stopwatch.StartNew();
            var result = await GetAsync(client, relay.Register("v1", Registration(Upstream(stall), Upstream(good))));
            report.Check("failover: connected but silent", result is { Count: FileLength, Matches: true } && failures.TryPeek(out var e) && e.Reason == RelayFailureReason.Timeout, $"{watch.ElapsedMilliseconds} ms");
            report.Check("failover: silent node is left after the read timeout", watch.ElapsedMilliseconds is > 700 and < 4000, watch.ElapsedMilliseconds + " ms");
        }

        // Connected, never answers.
        await using (var relay = LoopbackMediaRelay.Start(FastOptions))
        await using (var mute = new FakeNode("mute", FileLength) { Mode = NodeMode.StallHead })
        {
            var failures = new ConcurrentQueue<RelayUpstreamFailedEventArgs>();
            relay.UpstreamFailed += (_, e) => failures.Enqueue(e);
            var watch = Stopwatch.StartNew();
            var result = await GetAsync(client, relay.Register("v1", Registration(Upstream(mute), Upstream(good))));
            report.Check("failover: no answer", result is { Count: FileLength, Matches: true } && failures.TryPeek(out var e) && e.Reason == RelayFailureReason.Timeout && watch.ElapsedMilliseconds < 4000, $"{watch.ElapsedMilliseconds} ms");
        }

        // A player that reads slowly is not a stalled node.
        await using (var relay = LoopbackMediaRelay.Start(FastOptions))
        {
            var failures = new ConcurrentQueue<RelayUpstreamFailedEventArgs>();
            relay.UpstreamFailed += (_, e) => failures.Enqueue(e);
            using var response = await client.GetAsync(relay.Register("v1", Registration(Upstream(good), Upstream(good))), HttpCompletionOption.ResponseHeadersRead);
            await using var body = await response.Content.ReadAsStreamAsync();
            var first = await ReadAsync(body, 0, 100_000);
            await Task.Delay(2500);
            var rest = await ReadAsync(body, 100_000);
            report.Check("failover: a slow player is no stall", first.Matches && rest.Matches && first.Count + rest.Count == FileLength && failures.IsEmpty, $"failures={failures.Count}");
        }

        // Nothing works.
        await using (var relay = LoopbackMediaRelay.Start(FastOptions))
        {
            var failures = new ConcurrentQueue<RelayUpstreamFailedEventArgs>();
            relay.UpstreamFailed += (_, e) => failures.Enqueue(e);
            var url = relay.Register("v1", Registration(Dead("d1"), Dead("d2"), Dead("d3")));
            using var response = await client.GetAsync(url);
            report.Check("failover: every node dead", response.StatusCode == HttpStatusCode.BadGateway && failures.Count == 3, $"status={(int)response.StatusCode} failures={failures.Count}");
            using var single = await client.GetAsync(relay.Register("v2", Registration(Dead("d4"))));
            report.Check("failover: a single dead node is asked twice", single.StatusCode == HttpStatusCode.BadGateway && relay.GetStatistics("v2")?.Failovers == RelayDefaults.MinimumAttempts);
        }

        // Every node dies in the middle: the body ends short, as it would without a relay.
        await using (var relay = LoopbackMediaRelay.Start(FastOptions))
        await using (var cut = new FakeNode("cut", FileLength) { Mode = NodeMode.CutOnce, Limit = 200_000 })
        {
            var result = await GetAsync(client, relay.Register("v1", Registration(Upstream(cut), Dead("d1"))));
            report.Check("failover: gives up in the middle", result is { Status: HttpStatusCode.OK, Count: 200_000, Matches: true }, $"{result.Count} bytes");
            report.Check("failover: nothing left open", await WaitAsync(() => relay.GetStatistics("v1")?.ActiveRequests == 0 && cut.Open == 0));
        }
    }

    private static async Task LengthAsync(Report report)
    {
        using var client = CreateClient();
        await using var a = new FakeNode("a", FileLength) { Throttle = 600_000 };
        await using var other = new FakeNode("other", FileLength + 1000);
        await using var relay = LoopbackMediaRelay.Start(FastOptions);
        var failures = new ConcurrentQueue<RelayUpstreamFailedEventArgs>();
        relay.UpstreamFailed += (_, e) => failures.Enqueue(e);
        var url = relay.Register("v1", Registration(Upstream(a), Upstream(other)));
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        var first = await ReadAsync(body, 0, 100_000);
        relay.SwitchNode("v1", other.Url);
        var rest = await ReadAsync(body, 100_000);
        report.Check("length: a node with another file is refused", first.Matches && rest.Matches && first.Count + rest.Count == FileLength && relay.GetStatistics("v1") is { Handovers: 0 } stats && stats.Current.Label == "a" && stats.UpstreamBytes == FileLength);
        report.Check("length: reported", failures.TryPeek(out var e) && e.Reason == RelayFailureReason.Length && e.Failed.Label == "other" && e.Current.Label == "a");

        // A length given at registration is the truth from the first request on.
        await using var known = LoopbackMediaRelay.Start(FastOptions);
        var fixedUrl = known.Register("v1", new() { Upstreams = [Upstream(other), Upstream(a)], Length = FileLength });
        var result = await GetAsync(client, fixedUrl, FileLength - 5000);
        report.Check("length: registered length", result is { Count: 5000, Matches: true } && known.GetStatistics("v1")?.Current.Label == "a");
    }

    private static async Task UpdateAsync(Report report)
    {
        using var client = CreateClient();
        await using var old = new FakeNode("old", FileLength) { Throttle = 500_000 };
        await using var fresh = new FakeNode("fresh", FileLength);
        await using var spare = new FakeNode("spare", FileLength);
        await using var other = new FakeNode("other", FileLength - 1);
        await using var relay = LoopbackMediaRelay.Start(FastOptions);
        var failures = new ConcurrentQueue<RelayUpstreamFailedEventArgs>();
        relay.UpstreamFailed += (_, e) => failures.Enqueue(e);
        var url = relay.Register("v1", Registration(Upstream(old)));
        report.Equal("update: unknown key", RelayUpdateResult.UnknownKey, await relay.UpdateUrlsAsync("nope", [Upstream(fresh)]));

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        var first = await ReadAsync(body, 0, 150_000);

        report.Equal("update: another file is refused", RelayUpdateResult.LengthMismatch, await relay.UpdateUrlsAsync("v1", [Upstream(other), Upstream(fresh)]));
        report.Equal("update: unreachable addresses are refused", RelayUpdateResult.Unreachable, await relay.UpdateUrlsAsync("v1", [Dead("d1"), Dead("d2")]));
        report.Check("update: the old addresses stay after a refusal", relay.GetStatistics("v1")?.Current.Label == "old" && relay.SwitchNode("v1", old.Url) && !relay.SwitchNode("v1", fresh.Url));

        old.Mode = NodeMode.Forbidden;
        report.Equal("update: accepted", RelayUpdateResult.Updated, await relay.UpdateUrlsAsync("v1", [Upstream(fresh), Upstream(spare)]));
        var rest = await ReadAsync(body, 150_000);
        report.Check("update: the response goes on from the new address", first.Matches && rest.Matches && first.Count + rest.Count == FileLength && fresh.Served > FileLength / 2, $"fresh={fresh.Served}");
        report.Check("update: the old address is gone", !relay.SwitchNode("v1", old.Url) && relay.SwitchNode("v1", spare.Url) && relay.GetStatistics("v1")?.Current.Label == "spare");
        report.Check("update: no failure was reported", failures.IsEmpty, $"failures={failures.Count}");

        // The address asked for is dead: the table starts with one that answered.
        report.Equal("update: skips a dead address", RelayUpdateResult.Updated, await relay.UpdateUrlsAsync("v1", [Dead("d3"), Upstream(fresh)]));
        report.Check("update: skipped address is reported", relay.GetStatistics("v1")?.Current.Label == "fresh" && failures.TryPeek(out var e) && e.Failed.Label == "d3" && e.Current.Label == "fresh");

        // New headers come with new addresses.
        while (fresh.Heads.TryDequeue(out _))
        {
        }

        await relay.UpdateUrlsAsync("v1", [Upstream(fresh)], 0, new Dictionary<string, string> { ["Cookie"] = "SESSDATA=renewed" });
        await GetAsync(client, url, 0, 9);
        report.Check("update: headers replaced", fresh.Heads.All(head => head.Contains("Cookie: SESSDATA=renewed\r\n", StringComparison.Ordinal) && !head.Contains("upstream-secret", StringComparison.Ordinal)) && !fresh.Heads.IsEmpty);

        // Before the length is known there is nothing to compare with.
        await using var blank = LoopbackMediaRelay.Start(FastOptions);
        blank.Register("v1", Registration(Dead("d4")));
        report.Equal("update: unknown length accepts", RelayUpdateResult.Updated, await blank.UpdateUrlsAsync("v1", [Upstream(other)]));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var threw = false;
        try
        {
            await relay.UpdateUrlsAsync("v1", [Upstream(spare)], cancellationToken: cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }

        report.Check("update: cancelled keeps the table", threw && relay.GetStatistics("v1")?.Current.Label == "fresh");
    }

    private static async Task PreloadAsync(Report report)
    {
        using var client = CreateClient();
        await using var node = new FakeNode("a", FileLength);
        await using var relay = LoopbackMediaRelay.Start(FastOptions);
        var head = new byte[4000];
        FakeNode.Fill(head, 0);
        var middle = new byte[50_000];
        FakeNode.Fill(middle, 1_000_000);
        var url = relay.Register("a1", new()
        {
            Upstreams = [Upstream(node)],
            Length = FileLength,
            Preloaded = [new(0, head), new(1_000_000, middle)],
        });

        var first = await GetAsync(client, url, 0, 3999);
        report.Check("preload: answered from memory", first is { Count: 4000, Matches: true } && node.Requests == 0 && relay.GetStatistics("a1") is { PreloadedBytes: 4000, UpstreamBytes: 0 });
        var inside = await GetAsync(client, url, 1_010_000, 1_019_999);
        report.Check("preload: a seek into memory needs no node", inside is { Count: 10_000, Matches: true } && node.Requests == 0);
        var across = await GetAsync(client, url, 990_000, 1_099_999);
        report.Check("preload: memory and node are stitched", across is { Count: 110_000, Matches: true } && node.Requests == 2, $"requests={node.Requests}");
        var whole = await GetAsync(client, url);
        report.Check("preload: whole file", whole is { Count: FileLength, Matches: true });
    }

    private static async Task BackpressureAsync(Report report)
    {
        const long Big = 96L * 1024 * 1024;
        using var client = CreateClient();
        await using var node = new FakeNode("a", Big);
        await using var relay = LoopbackMediaRelay.Start(FastOptions);
        var url = relay.Register("v1", Registration(Upstream(node)));
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        var first = await ReadAsync(body, 0, 64 * 1024);
        await Task.Delay(1500);
        var held = node.Served;
        await Task.Delay(1000);
        report.Check("backpressure: a player that stops reading stops the relay", first.Matches && held < Big / 4 && node.Served == held, $"served {held / 1024} KiB of {Big / 1024} KiB");
        var failures = relay.GetStatistics("v1")?.Failovers;
        var more = await ReadAsync(body, 64 * 1024, 4 * 1024 * 1024);
        report.Check("backpressure: reading goes on", more is { Count: 4 * 1024 * 1024, Matches: true } && failures == 0);
        var rate = relay.GetStatistics("v1")?.BytesPerSecond ?? 0;
        report.Check("backpressure: rate is measured", rate > 100_000, $"{rate / 1024:0} KiB/s");
    }

    private static async Task LifetimeAsync(Report report)
    {
        using var client = CreateClient();
        await using var node = new FakeNode("a", FileLength) { Throttle = 300_000 };
        var relay = LoopbackMediaRelay.Start(FastOptions);
        var failures = new ConcurrentQueue<RelayUpstreamFailedEventArgs>();
        relay.UpstreamFailed += (_, e) => failures.Enqueue(e);
        var url = relay.Register("v1", Registration(Upstream(node), Upstream(node)));

        // The player closes its connection, as it does on every seek.
        using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            await using var body = await response.Content.ReadAsStreamAsync();
            await ReadAsync(body, 0, 50_000);
        }

        report.Check("lifetime: a closed player connection frees the node", await WaitAsync(() => node.Open == 0 && relay.GetStatistics("v1")?.ActiveRequests == 0), $"open={node.Open}");

        // Unregistering ends the response that is under way.
        using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            await using var body = await response.Content.ReadAsStreamAsync();
            await ReadAsync(body, 0, 50_000);
            var watch = Stopwatch.StartNew();
            report.Check("lifetime: unregister", relay.Unregister("v1") && !relay.Unregister("v1"));
            var rest = await ReadAsync(body, 50_000);
            report.Check("lifetime: unregister ends the response", rest.Count < FileLength - 50_000 && watch.ElapsedMilliseconds < 3000, $"{rest.Count} more bytes, {watch.ElapsedMilliseconds} ms");
        }

        using var gone = await client.GetAsync(url);
        report.Check("lifetime: unregistered key", gone.StatusCode == HttpStatusCode.NotFound && relay.GetStatistics("v1") is null && !relay.SwitchNode("v1", node.Url));
        report.Check("lifetime: the node was let go", await WaitAsync(() => node.Open == 0), $"open={node.Open}");

        // Disposing with responses under way.
        var second = relay.Register("v2", Registration(Upstream(node)));
        using var pending = await client.GetAsync(second, HttpCompletionOption.ResponseHeadersRead);
        await using var pendingBody = await pending.Content.ReadAsStreamAsync();
        await ReadAsync(pendingBody, 0, 50_000);
        using var idle = new TcpClient();
        await idle.ConnectAsync(IPAddress.Loopback, relay.Port);
        var port = relay.Port;
        var dispose = Stopwatch.StartNew();
        await relay.DisposeAsync();
        await relay.DisposeAsync();
        report.Check("lifetime: dispose ends everything quickly", dispose.ElapsedMilliseconds < 3000, dispose.ElapsedMilliseconds + " ms");
        var tail = await ReadAsync(pendingBody, 50_000);
        report.Check("lifetime: dispose ends the response", tail.Count < FileLength - 50_000);
        report.Check("lifetime: dispose frees the node", await WaitAsync(() => node.Open == 0), $"open={node.Open}");
        var refused = false;
        try
        {
            using var late = new TcpClient();
            await late.ConnectAsync(IPAddress.Loopback, port);
        }
        catch (SocketException)
        {
            refused = true;
        }

        report.Check("lifetime: the port is closed", refused);
        report.Equal("lifetime: ending a stream is no node failure", 0, failures.Count);
        var threw = false;
        try
        {
            relay.Register("v3", Registration(Upstream(node)));
        }
        catch (ObjectDisposedException)
        {
            threw = true;
        }

        report.Check("lifetime: no registration after dispose", threw);
    }

    private static async Task StressAsync(Report report)
    {
        using var client = CreateClient();
        var nodes = new[] { new FakeNode("n0", FileLength), new FakeNode("n1", FileLength), new FakeNode("n2", FileLength) };
        await using (var relay = LoopbackMediaRelay.Start(FastOptions))
        {
            var url = relay.Register("v1", Registration([.. nodes.Select(Upstream)]));
            using var stop = new CancellationTokenSource();
            var switcher = Task.Run(async () =>
            {
                var random = new Random(7);
                while (!stop.IsCancellationRequested)
                {
                    relay.SwitchNode("v1", nodes[random.Next(nodes.Length)].Url);
                    await Task.Delay(random.Next(1, 8));
                }
            });
            var seed = new Random(11);
            var jobs = Enumerable.Range(0, 24).Select(i =>
            {
                var from = seed.NextInt64(0, FileLength - 1);
                var abandon = i % 4 == 3;
                return Task.Run(async () =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new RangeHeaderValue(from, null);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    await using var body = await response.Content.ReadAsStreamAsync();
                    var result = await ReadAsync(body, from, abandon ? 20_000 : null, 7001);
                    return result.Matches && (abandon || result.Count == FileLength - from);
                });
            }).ToArray();
            var results = await Task.WhenAll(jobs);
            await stop.CancelAsync();
            await switcher;
            var stats = relay.GetStatistics("v1");
            report.Check("stress: 24 ranged responses under constant switching", results.All(ok => ok), $"handovers={stats?.Handovers} failovers={stats?.Failovers}");
            var idle = await WaitAsync(() => relay.GetStatistics("v1")?.ActiveRequests == 0 && nodes.All(node => node.Open == 0));
            report.Check("stress: nothing is left active", idle, $"active={relay.GetStatistics("v1")?.ActiveRequests} open={string.Join('/', nodes.Select(node => node.Open))}");
        }

        report.Check("stress: nodes are free after dispose", await WaitAsync(() => nodes.All(node => node.Open == 0)));
        foreach (var node in nodes)
        {
            await node.DisposeAsync();
        }
    }
}
