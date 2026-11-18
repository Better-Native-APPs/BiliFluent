// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Richasy.MpvKernel.Relay;

namespace BiliFluent.Mpv.RelayCheck;

internal static class MemoryRelayChecks
{
    private const long OriginalLength = (3 * 1024 * 1024) + 12345;
    private const long FragmentOffset = 1_000_000;
    private const int FragmentBytes = 25_600;
    private static readonly LoopbackMediaRelayOptions FastOptions = new()
    {
        ConnectTimeout = TimeSpan.FromMilliseconds(800),
        ResponseTimeout = TimeSpan.FromMilliseconds(800),
        ReadTimeout = TimeSpan.FromMilliseconds(800),
    };

    public static async Task RunAsync(Report report)
    {
        foreach (var virtualFile in new[] { false, true })
        {
            await report.RunAsync("memory shape ranges", () => RangesAsync(report, virtualFile));
            await report.RunAsync("memory shape handover", () => HandoverAsync(report, virtualFile));
            await report.RunAsync("memory shape failover/update", () => FailoverUpdateAsync(report, virtualFile));
        }
    }

    private static HttpClient Client() => new(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };

    private static byte[] Head(bool virtualFile)
    {
        var head = new byte[128];
        if (virtualFile)
        {
            for (var i = 0; i < head.Length; i++) head[i] = (byte)(i ^ 0xAB);
        }
        else
        {
            FakeNode.Fill(head, 0);
        }

        return head;
    }

    private static string Register(LoopbackMediaRelay relay, string key, bool virtualFile, params RelayUpstream[] nodes)
    {
        var fragments = new byte[FragmentBytes];
        FakeNode.Fill(fragments, FragmentOffset);
        return virtualFile
            ? relay.RegisterVirtualMp4(key, new()
            {
                Upstreams = nodes, Head = Head(true), Fragments = fragments,
                FirstFragmentOffset = FragmentOffset, UpstreamLength = OriginalLength,
            })
            : relay.RegisterPreloaded(key, new()
            {
                Upstreams = nodes, Length = OriginalLength, Preloaded = [new(0, Head(false)), new(FragmentOffset, fragments)],
            });
    }

    private static long Length(bool virtualFile) => virtualFile ? 128 + OriginalLength - FragmentOffset : OriginalLength;

    private static async Task<(long Count, bool Matches)> ReadAsync(Stream body, long offset, bool virtualFile, long? limit = null)
    {
        var buffer = new byte[7001];
        var head = Head(virtualFile);
        long count = 0;
        var matches = true;
        while (limit is null || count < limit)
        {
            var wanted = limit is { } end ? (int)Math.Min(buffer.Length, end - count) : buffer.Length;
            var read = await body.ReadAsync(buffer.AsMemory(0, wanted));
            if (read == 0) break;
            for (var i = 0; i < read; i++)
            {
                var at = offset + count + i;
                var expected = virtualFile && at < head.Length ? head[(int)at] : FakeNode.At(virtualFile ? FragmentOffset + at - head.Length : at);
                matches &= buffer[i] == expected;
            }

            count += read;
        }

        return (count, matches);
    }

    private static async Task<(HttpStatusCode Status, long Count, bool Matches, string? Range)> GetAsync(HttpClient client, string url, bool virtualFile, long? from = null, long? to = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (from is not null || to is not null) request.Headers.Range = new RangeHeaderValue(from, to);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        var result = await ReadAsync(body, response.Content.Headers.ContentRange?.From ?? 0, virtualFile);
        return (response.StatusCode, result.Count, result.Matches, response.Content.Headers.ContentRange?.ToString());
    }

    private static async Task RangesAsync(Report report, bool virtualFile)
    {
        var name = virtualFile ? "virtual" : "preloaded";
        using var client = Client();
        await using var node = new FakeNode("a", OriginalLength);
        await using var relay = LoopbackMediaRelay.Start(FastOptions);
        var url = Register(relay, "s1", virtualFile, new RelayUpstream(node.Url, "a"));
        var head = await GetAsync(client, url, virtualFile, 3, 99);
        report.Check(name + ": head memory hit", head is { Count: 97, Matches: true } && node.Requests == 0);
        var first = virtualFile ? 128 : FragmentOffset;
        var inside = await GetAsync(client, url, virtualFile, first + 7, first + 777);
        report.Check(name + ": fragment memory hit", inside is { Count: 771, Matches: true } && node.Requests == 0);
        report.Check(name + ": memory stats", relay.GetStatistics("s1") is { UpstreamBytes: 0, PreloadedBytes: 868, LastDeliveredAt: not null, LastByteReceivedAt: null });
        var across = await GetAsync(client, url, virtualFile, first - 5, first + FragmentBytes + 64);
        report.Check(name + ": range crosses memory and upstream boundaries", across is { Count: FragmentBytes + 70, Matches: true });
        report.Equal(name + ": client content-range uses client length", $"bytes {first - 5}-{first + FragmentBytes + 64}/{Length(virtualFile)}", across.Range);
        if (virtualFile)
        {
            report.Check(name + ": upstream receives translated range", node.Heads.Any(value => value.Contains($"Range: bytes={FragmentOffset + FragmentBytes}-{FragmentOffset + FragmentBytes + 64}\r\n", StringComparison.Ordinal)));
        }

        var tail = await GetAsync(client, url, virtualFile, first + FragmentBytes + 100, first + FragmentBytes + 999);
        report.Check(name + ": preload miss", tail is { Count: 900, Matches: true });
        var whole = await GetAsync(client, url, virtualFile);
        report.Check(name + ": full file", whole.Count == Length(virtualFile) && whole.Matches && whole.Status == HttpStatusCode.OK);
        var suffix = await GetAsync(client, url, virtualFile, null, 100);
        report.Check(name + ": suffix/seek", suffix is { Count: 100, Matches: true });
        var outside = await GetAsync(client, url, virtualFile, Length(virtualFile));
        report.Check(name + ": unsatisfiable range", outside.Status == HttpStatusCode.RequestedRangeNotSatisfiable && outside.Range == $"bytes */{Length(virtualFile)}");
        using var headResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
        report.Check(name + ": HEAD has virtual/full length", headResponse.Content.Headers.ContentLength == Length(virtualFile));
        var stats = relay.GetStatistics("s1");
        report.Check(name + ": forward/rate/last-byte stats", stats is { LastByteReceivedAt: not null, LastDeliveredAt: not null, DeliveredBytesPerSecond: > 0 }
            && stats.Value.ForwardBytes == Length(virtualFile) && stats.Value.ForwardUpstreamOffset == OriginalLength);

        if (!virtualFile)
        {
            var rejected = false;
            try
            {
                relay.RegisterPreloaded("bad", new()
                {
                    Upstreams = [new(node.Url, "a")], Length = OriginalLength,
                    Preloaded = [new(0, new byte[100]), new(99, new byte[2])],
                });
            }
            catch (ArgumentException)
            {
                rejected = true;
            }

            report.Check("preloaded: overlap rejected", rejected && relay.GetStatistics("bad") is null);
        }
    }

    private static async Task HandoverAsync(Report report, bool virtualFile)
    {
        var name = virtualFile ? "virtual" : "preloaded";
        using var client = Client();
        await using var a = new FakeNode("a", OriginalLength) { Throttle = 300_000 };
        await using var b = new FakeNode("b", OriginalLength);
        await using var relay = LoopbackMediaRelay.Start(FastOptions);
        var url = Register(relay, "s1", virtualFile, new(a.Url, "a"), new(b.Url, "b"));
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        var before = await ReadAsync(body, 0, virtualFile, 100_000);
        var accepted = relay.SwitchNode("s1", b.Url);
        var after = await ReadAsync(body, 100_000, virtualFile);
        report.Check(name + ": in-response node handover preserves all bytes", accepted && before.Matches && after.Matches && before.Count + after.Count == Length(virtualFile));
        report.Check(name + ": handover counted without failover", relay.GetStatistics("s1") is { Handovers: > 0, Failovers: 0 } s && s.Current.Label == "b");
    }

    private static async Task FailoverUpdateAsync(Report report, bool virtualFile)
    {
        var name = virtualFile ? "virtual" : "preloaded";
        using var client = Client();
        await using var bad = new FakeNode("bad", OriginalLength) { Mode = NodeMode.Forbidden };
        await using var good = new FakeNode("good", OriginalLength);
        await using var fresh = new FakeNode("fresh", OriginalLength);
        await using var different = new FakeNode("different", OriginalLength + 1);
        await using var relay = LoopbackMediaRelay.Start(FastOptions);
        var failures = new ConcurrentQueue<RelayUpstreamFailedEventArgs>();
        relay.UpstreamFailed += (_, e) => failures.Enqueue(e);
        var url = Register(relay, "s1", virtualFile, new(bad.Url, "bad"), new(good.Url, "good"));
        var first = await GetAsync(client, url, virtualFile);
        report.Check(name + ": bad initial node rotates with correct offsets", first.Count == Length(virtualFile) && first.Matches && failures.Any(e => e.Failed.Label == "bad"));
        report.Equal(name + ": replacement checks ORIGINAL length", RelayUpdateResult.LengthMismatch, await relay.UpdateUrlsAsync("s1", [new(different.Url, "different")]));
        report.Equal(name + ": replacement skips bad node", RelayUpdateResult.Updated, await relay.UpdateUrlsAsync("s1", [new(bad.Url, "bad"), new(fresh.Url, "fresh")]));
        var from = virtualFile ? 128 + FragmentBytes + 99 : FragmentOffset + FragmentBytes + 99;
        var tail = await GetAsync(client, url, virtualFile, from, from + 888);
        report.Check(name + ": seek after URL replacement", tail is { Count: 889, Matches: true } && relay.GetStatistics("s1")?.Current.Label == "fresh");

        // Both shapes must interrupt a connected-but-silent node, not just reject bad status codes.
        await using var stall = new FakeNode("stall", OriginalLength) { Mode = NodeMode.StallBody, Limit = 100_000 };
        await using var silentRelay = LoopbackMediaRelay.Start(FastOptions);
        var stalledUrl = Register(silentRelay, "s2", virtualFile, new(stall.Url, "stall"), new(good.Url, "good"));
        var watch = Stopwatch.StartNew();
        var result = await GetAsync(client, stalledUrl, virtualFile);
        report.Check(name + ": silent node rotates", result.Matches && result.Count == Length(virtualFile) && silentRelay.GetStatistics("s2")?.Failovers == 1 && watch.ElapsedMilliseconds < 5000);
    }
}
