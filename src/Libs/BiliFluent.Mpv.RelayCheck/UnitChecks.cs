// Copyright (c) Bili Copilot. All rights reserved.

using System.Text;
using Richasy.MpvKernel.Relay;

namespace BiliFluent.Mpv.RelayCheck;

/// <summary>
/// The parts of the relay that need no socket: ranges, the token, the address table, the rate.
/// </summary>
internal static class UnitChecks
{
    public static void Run(Report report)
    {
        Ranges(report);
        Tokens(report);
        Tables(report);
        Rates(report);
        Heads(report);
    }

    private static void Ranges(Report report)
    {
        report.Equal("range: missing header", null, RelayByteRange.Parse(null));
        report.Equal("range: empty header", null, RelayByteRange.Parse("  "));
        report.Equal("range: open end", new RelayRangeRequest(100, null, false), RelayByteRange.Parse("bytes=100-"));
        report.Equal("range: closed", new RelayRangeRequest(100, 199, false), RelayByteRange.Parse("bytes=100-199"));
        report.Equal("range: case and spaces", new RelayRangeRequest(0, 0, false), RelayByteRange.Parse(" Bytes= 0 - 0 "));
        report.Equal("range: suffix", new RelayRangeRequest(500, null, true), RelayByteRange.Parse("bytes=-500"));
        report.Equal("range: several ranges are ignored", null, RelayByteRange.Parse("bytes=0-1,5-6"));
        report.Equal("range: other unit is ignored", null, RelayByteRange.Parse("items=0-1"));
        report.Equal("range: end before start is ignored", null, RelayByteRange.Parse("bytes=9-3"));
        report.Equal("range: negative start is ignored", null, RelayByteRange.Parse("bytes=-5-9"));
        report.Equal("range: garbage is ignored", null, RelayByteRange.Parse("bytes=a-b"));
        report.Equal("range: overflow is ignored", null, RelayByteRange.Parse("bytes=99999999999999999999-"));
        report.Equal("range: largest offset", new RelayRangeRequest(long.MaxValue, null, false), RelayByteRange.Parse("bytes=9223372036854775807-"));

        report.Equal("resolve: whole", new RelayResolvedRange(RelayRangeKind.Full, 0, 999), RelayByteRange.Resolve(null, 1000));
        report.Equal("resolve: whole length", 1000L, RelayByteRange.Resolve(null, 1000).Length);
        report.Equal("resolve: empty resource", 0L, RelayByteRange.Resolve(null, 0).Length);
        report.Equal("resolve: open end", new RelayResolvedRange(RelayRangeKind.Partial, 100, 999), RelayByteRange.Resolve(new(100, null, false), 1000));
        report.Equal("resolve: closed", new RelayResolvedRange(RelayRangeKind.Partial, 100, 199), RelayByteRange.Resolve(new(100, 199, false), 1000));
        report.Equal("resolve: end clamped", new RelayResolvedRange(RelayRangeKind.Partial, 900, 999), RelayByteRange.Resolve(new(900, 5000, false), 1000));
        report.Equal("resolve: last byte", new RelayResolvedRange(RelayRangeKind.Partial, 999, 999), RelayByteRange.Resolve(new(999, null, false), 1000));
        report.Equal("resolve: start at end", RelayRangeKind.Unsatisfiable, RelayByteRange.Resolve(new(1000, null, false), 1000).Kind);
        report.Equal("resolve: unsatisfiable has no length", 0L, RelayByteRange.Resolve(new(1000, null, false), 1000).Length);
        report.Equal("resolve: suffix", new RelayResolvedRange(RelayRangeKind.Partial, 500, 999), RelayByteRange.Resolve(new(500, null, true), 1000));
        report.Equal("resolve: suffix longer than resource", new RelayResolvedRange(RelayRangeKind.Partial, 0, 999), RelayByteRange.Resolve(new(5000, null, true), 1000));
        report.Equal("resolve: empty suffix", RelayRangeKind.Unsatisfiable, RelayByteRange.Resolve(new(0, null, true), 1000).Kind);
        report.Equal("resolve: range of empty resource", RelayRangeKind.Unsatisfiable, RelayByteRange.Resolve(new(0, null, false), 0).Kind);
        report.Equal("content-range", "bytes 100-199/1000", RelayByteRange.FormatContentRange(100, 199, 1000));
        report.Equal("content-range: unsatisfied", "bytes */1000", RelayByteRange.FormatUnsatisfied(1000));
    }

    private static void Tokens(Report report)
    {
        var token = RelayAccessToken.Create();
        var other = RelayAccessToken.Create();
        report.Equal("token: length", 2 * RelayDefaults.TokenBytes, token.Value.Length);
        report.Check("token: random", token.Value != other.Value);
        report.Check("token: not revealed by ToString", !token.ToString().Contains(token.Value, StringComparison.Ordinal));
        var path = token.BuildPath("v1");
        report.Check("token: path accepted", token.TryGetKey(path, out var key) && key == "v1");
        report.Check("token: query ignored", token.TryGetKey(path + "?x=1", out key) && key == "v1");
        report.Check("token: other token rejected", !other.TryGetKey(path, out _));
        report.Check("token: prefix rejected", !token.TryGetKey("/" + token.Value[..^1] + "/v1", out _));
        report.Check("token: longer rejected", !token.TryGetKey("/" + token.Value + "0/v1", out _));
        var upper = token.Value.ToUpperInvariant();
        report.Check("token: upper case rejected", string.Equals(upper, token.Value, StringComparison.Ordinal) || !token.TryGetKey("/" + upper + "/v1", out _));
        report.Check("token: missing key rejected", !token.TryGetKey("/" + token.Value, out _) && !token.TryGetKey("/" + token.Value + "/", out _));
        report.Check("token: nested path rejected", !token.TryGetKey(path + "/x", out _));
        report.Check("token: traversal rejected", !token.TryGetKey("/" + token.Value + "/..", out _));
        report.Check("token: absolute form rejected", !token.TryGetKey("http://127.0.0.1" + path, out _));
        report.Check("token: empty rejected", !token.TryGetKey(string.Empty, out _) && !token.TryGetKey(null, out _) && !token.TryGetKey("/", out _));
        report.Check("token: non-ascii rejected", !token.TryGetKey("/" + new string('é', token.Value.Length) + "/v1", out _));
        report.Check("key: valid", RelayAccessToken.IsValidKey("a-B_9"));
        report.Check("key: invalid", !RelayAccessToken.IsValidKey("a/b") && !RelayAccessToken.IsValidKey(string.Empty) && !RelayAccessToken.IsValidKey(new string('a', 65)) && !RelayAccessToken.IsValidKey("a b"));
        var threw = false;
        try
        {
            token.BuildPath("a/b");
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        report.Check("key: invalid key cannot be registered", threw);
    }

    private static void Tables(Report report)
    {
        var a = new RelayUpstream("https://a.example/f?sign=1", "kind-a");
        var b = new RelayUpstream("https://b.example/f?sign=2", "kind-b");
        var c = new RelayUpstream("http://c.example/f?sign=3", "kind-c");
        report.Equal("upstream: ToString is the label", "kind-a", a.ToString());
        report.Check("upstream: scheme", RelayUpstream.IsSupported(a.Url) && !RelayUpstream.IsSupported("ftp://a/f") && !RelayUpstream.IsSupported("/f") && !RelayUpstream.IsSupported(null));
        var message = string.Empty;
        try
        {
            _ = new RelayUpstream("memory://secret-token", "x");
        }
        catch (ArgumentException ex)
        {
            message = ex.Message;
        }

        report.Check("upstream: rejected address is not in the message", message.Length > 0 && !message.Contains("secret-token", StringComparison.Ordinal));

        var table = new RelayUpstreamTable([a, b, c]);
        report.Equal("table: starts at first", a, table.Current.Upstream);
        report.Equal("table: index of", 2, table.IndexOf(c.Url));
        report.Equal("table: index of unknown", -1, table.IndexOf("https://d.example/f"));
        var version = table.Version;
        report.Check("table: select same keeps version", table.Select(0) && table.Version == version);
        report.Check("table: select other changes version", table.Select(2) && table.Version != version && table.Current.Upstream == c);
        report.Check("table: select out of range", !table.Select(3) && !table.Select(-1) && table.Current.Upstream == c);

        var seen = table.Current;
        report.Check("table: rotate wraps", table.TryRotate(seen.Version, out var next) && next.Upstream == a && next.Index == 0);
        report.Check("table: stale rotate does nothing", !table.TryRotate(seen.Version, out var now) && now.Upstream == a && table.Current.Upstream == a);
        report.Check("table: rotate in order", table.TryRotate(table.Version, out next) && next.Upstream == b && table.TryRotate(table.Version, out next) && next.Upstream == c);

        var single = new RelayUpstreamTable([a]);
        var before = single.Version;
        report.Check("table: single address rotates onto itself", single.TryRotate(before, out next) && next.Upstream == a && single.Version != before);

        version = table.Version;
        table.Replace([b, a], 1);
        report.Check("table: replace", table.Count == 2 && table.Current.Upstream == a && table.Version != version && table.IndexOf(c.Url) == -1);
        var rejected = 0;
        foreach (var action in new Action[]
        {
            () => _ = new RelayUpstreamTable([]),
            () => _ = new RelayUpstreamTable([a], 1),
            () => table.Replace([], 0),
            () => table.Replace([a, b], 2),
        })
        {
            try
            {
                action();
            }
            catch (ArgumentException)
            {
                rejected++;
            }
        }

        report.Equal("table: invalid input rejected", 4, rejected);
        report.Check("table: failed replace keeps the table", table.Count == 2 && table.Current.Upstream == a);
    }

    private static void Rates(Report report)
    {
        var meter = new RelayRateMeter(TimeSpan.FromSeconds(3));
        report.Equal("rate: nothing yet", 0d, meter.GetRate(1000));
        for (var t = 0; t < 6000; t += 100)
        {
            meter.Add(10_000 + t, 10_000);
        }

        var steady = meter.GetRate(10_000 + 5990);
        report.Check("rate: steady", steady is > 90_000 and < 110_000, steady.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
        var later = meter.GetRate(10_000 + 5990 + 1500);
        report.Check("rate: decays", later < steady && later > 0, later.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
        report.Equal("rate: window passed", 0d, meter.GetRate(10_000 + 5990 + 4000));

        var fresh = new RelayRateMeter(TimeSpan.FromSeconds(3));
        fresh.Add(50_000, 100_000);
        fresh.Add(50_500, 100_000);
        var early = fresh.GetRate(51_000);
        report.Check("rate: young stream is not averaged over the whole window", early is > 150_000 and < 250_000, early.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void Heads(Report report)
    {
        var bytes = Encoding.ASCII.GetBytes("GET /t/k HTTP/1.1\r\nHost: 127.0.0.1\r\nrange:  bytes=5-\r\nCookie: x\r\n\r\nrest");
        var end = RelayRequestHead.FindEnd(bytes);
        report.Equal("head: end", bytes.Length - 4, end);
        report.Equal("head: incomplete", -1, RelayRequestHead.FindEnd(bytes.AsSpan(0, 20)));
        report.Check("head: parsed", RelayRequestHead.TryParse(bytes.AsSpan(0, end), out var head) && head is { Method: "GET", Target: "/t/k", Range: "bytes=5-" });
        report.Check("head: no range", RelayRequestHead.TryParse("HEAD /a HTTP/1.0\r\n\r\n"u8, out head) && head is { Method: "HEAD", Range: null });
        report.Check("head: not http", !RelayRequestHead.TryParse("hello\r\n\r\n"u8, out _) && !RelayRequestHead.TryParse("GET /a SPDY/3\r\n\r\n"u8, out _) && !RelayRequestHead.TryParse("GET  HTTP/1.1\r\n\r\n"u8, out _));
    }
}
