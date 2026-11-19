// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BiliCopilot.UI.Resolvers;
using Richasy.MpvKernel.Relay;

namespace BiliFluent.Mpv.RelayCheck;

/// <summary>Deterministic transfer boundaries and actual paced loopback upstream/player checks.</summary>
internal static class ThroughputChecks
{
    private const int EightMbps = 1_000_000;

    public static async Task RunAsync(Report report)
    {
        Boundaries(report);
        using var resolution = new CheckTimerResolution();
        await report.RunAsync("throughput: 8 Mbps upstream", () => LimitedAsync(report));
        await report.RunAsync("throughput: 4 Mbps player", () => SlowPlayerAsync(report));
        await report.RunAsync("throughput: pause and drain", () => PauseAsync(report));
        await report.RunAsync("throughput: short response", () => ShortAsync(report));
        await report.RunAsync("throughput: fast response", () => FastAsync(report));
        await report.RunAsync("throughput: fast loopback response", () => FastLoopbackAsync(report));
        await report.RunAsync("throughput: managed read suspension", () => SuspensionAsync(report));
        await report.RunAsync("throughput: changed upstream", () => SwitchAsync(report));
    }

    private static long Ticks(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000);

    private static void Boundaries(Report report)
    {
        var meter = new RelayThroughputMeter();
        meter.ReadCompleted(64 * 1024, Ticks(0), Ticks(100));
        report.Check("throughput: first body read and first-byte wait are excluded", meter.WriteCompleted(Ticks(100), Ticks(101)) is null);
        meter.ReadCompleted(256 * 1024, Ticks(101), Ticks(362));
        var first = meter.WriteCompleted(Ticks(362), Ticks(363));
        report.Check("throughput: cumulative network observation", first is { Bytes: 256 * 1024, IsNetworkLimited: true }
            && Math.Abs(first.BytesPerSecond - (256 * 1024 / 0.263)) < 1);
        meter.ReadCompleted(64 * 1024, Ticks(363), Ticks(428.536));
        var stopped = meter.WriteCompleted(Ticks(428.536), Ticks(3428.536));
        report.Check("throughput: slow write ends at its start, not after the pause", stopped is { Bytes: 320 * 1024 }
            && stopped.Duration.TotalMilliseconds < 329 && stopped.Sequence > first!.Sequence);
        meter.ReadCompleted(4 * 1024 * 1024, Ticks(3428.536), Ticks(3429));
        report.Check("throughput: accumulated socket bytes cannot restart a segment", meter.WriteCompleted(Ticks(3429), Ticks(3430)) is null);
        meter.ReadCompleted(64 * 1024, Ticks(3430), Ticks(3496));
        report.Check("throughput: drain-proving read itself is excluded", meter.WriteCompleted(Ticks(3496), Ticks(3497)) is null);
        meter.ReadCompleted(256 * 1024, Ticks(3497), Ticks(3759));
        var drained = meter.WriteCompleted(Ticks(3759), Ticks(3760));
        report.Check("throughput: a drained connection resumes at the real rate", drained is { Bytes: 256 * 1024, IsNetworkLimited: true }
            && drained.BytesPerSecond * 8 <= 8_000_000 * 1.15);

        var floor = new RelayThroughputMeter();
        floor.ReadCompleted(64 * 1024, Ticks(0), Ticks(1));
        RelayThroughputObservation? floorObservation = null;
        for (var i = 0; i < 12; i++)
        {
            var start = 1 + (i * 21d);
            floor.ReadCompleted(32 * 1024, Ticks(start), Ticks(start + 1));
            floorObservation = floor.WriteCompleted(Ticks(start + 1), Ticks(start + 21));
        }

        report.Check("throughput: sub-threshold player waits produce a floor, not a network measurement", floorObservation is { IsNetworkLimited: false });
        var shortResponse = new RelayThroughputMeter();
        shortResponse.ReadCompleted(64 * 1024, Ticks(0), Ticks(1));
        shortResponse.ReadCompleted(128 * 1024, Ticks(1), Ticks(501));
        report.Check("throughput: short response remains unobserved at end", shortResponse.End(Ticks(502)) is null);
        var fast = new RelayThroughputMeter();
        fast.ReadCompleted(64 * 1024, Ticks(0), Ticks(1));
        fast.ReadCompleted(16 * 1024 * 1024, Ticks(1), Ticks(99));
        var fastObservation = fast.WriteCompleted(Ticks(99), Ticks(100));
        report.Check("throughput: 16 MiB within 100 ms has a finite observation", fastObservation is { Bytes: 16 * 1024 * 1024 }
            && double.IsFinite(fastObservation.BytesPerSecond));
    }

    private static async Task LimitedAsync(Report report)
    {
        await using var node = Node("limited", 3 * 1024 * 1024, EightMbps);
        await using var relay = LoopbackMediaRelay.Start();
        var url = Register(relay, node);
        using var reader = await Reader.OpenAsync(url);
        var observations = await CaptureAsync(relay, () => reader.ReadAsync(node.Length));
        var latest = observations.LastOrDefault();
        report.Check("throughput: 8 Mbps within 10 percent and network-limited", latest is { IsNetworkLimited: true }
            && InRange(latest.BytesPerSecond, EightMbps, 0.10), Describe(latest));
        report.Check("throughput: evidence is published before the response ends", observations.Any(o => o.Bytes < node.Length / 2));
        report.Check("throughput: old rolling statistics still count the complete response", relay.GetStatistics("v1")?.UpstreamBytes == node.Length);
    }

    private static async Task SlowPlayerAsync(Report report)
    {
        await using var node = Node("fast", 8 * 1024 * 1024, 0);
        await using var relay = LoopbackMediaRelay.Start();
        using var reader = await Reader.OpenAsync(Register(relay, node));
        var observations = await CaptureAsync(relay, () => reader.ReadAsync(node.Length, throttle: 500_000));
        report.Check("throughput: 4 Mbps player never creates a network-limited observation", observations.All(o => !o.IsNetworkLimited),
            string.Create(CultureInfo.InvariantCulture, $"observations={observations.Count} network={observations.Count(o => o.IsNetworkLimited)}"));
    }

    private static async Task PauseAsync(Report report)
    {
        await using var node = Node("limited", 8 * 1024 * 1024, EightMbps);
        await using var relay = LoopbackMediaRelay.Start();
        using var reader = await Reader.OpenAsync(Register(relay, node));
        long beforeResume = 0;
        var observations = await CaptureAsync(relay, async () =>
        {
            await reader.ReadAsync(512 * 1024);
            await Task.Delay(3000);
            beforeResume = relay.GetStatistics("v1")?.ThroughputObservation?.Sequence ?? 0;
            await reader.ReadAsync(node.Length - (512 * 1024));
        });
        var after = observations.Where(o => o.Sequence > beforeResume).ToList();
        report.Check("throughput: pause resumes with new observations", after.Count > 0);
        report.Check("throughput: buffered burst after a three-second pause is never evidence above 9.2 Mbps",
            after.All(o => o.BytesPerSecond <= EightMbps * 1.15),
            string.Create(CultureInfo.InvariantCulture, $"after={after.Count} max-kbps={after.Select(o => o.BytesPerSecond * 8 / 1000).DefaultIfEmpty().Max():0}"));
    }

    private static async Task ShortAsync(Report report)
    {
        await using var node = Node("short", 128 * 1024, EightMbps);
        await using var relay = LoopbackMediaRelay.Start();
        using var reader = await Reader.OpenAsync(Register(relay, node));
        var observations = await CaptureAsync(relay, () => reader.ReadAsync(node.Length));
        report.Check("throughput: response below 256 KiB produces no observation", observations.Count == 0 && relay.GetStatistics("v1")?.ThroughputObservation is null);
    }

    private static async Task FastAsync(Report report)
    {
        var (latest, elapsed) = await CopyMemoryAsync(16 * 1024 * 1024, 30);
        report.Check("throughput: fast fixture delivers 16 MiB within 100 ms", elapsed <= 100,
            string.Create(CultureInfo.InvariantCulture, $"elapsed-ms={elapsed:0.0}"));
        report.Check("throughput: fast 16 MiB response is observed with a finite rate", latest is not null && double.IsFinite(latest.BytesPerSecond),
            Describe(latest));
    }

    private static async Task<(RelayThroughputObservation? Observation, double ElapsedMilliseconds)> CopyMemoryAsync(int length, int suspensionMilliseconds)
    {
        using var source = new MemoryStream(new byte[length], writable: false);
        var meter = new RelayThroughputMeter();
        var buffer = new byte[64 * 1024];
        RelayThroughputObservation? latest = null;
        var clock = Stopwatch.StartNew();
        long bytes = 0;
        while (bytes < source.Length)
        {
            var started = Stopwatch.GetTimestamp();
            // A real timed fake stream avoids measuring this host's loopback socket ceiling.
            if (bytes == length / 2) await Task.Delay(suspensionMilliseconds);
            var count = await source.ReadAsync(buffer);
            var completed = Stopwatch.GetTimestamp();
            meter.ReadCompleted(count, started, completed);
            var writeStarted = Stopwatch.GetTimestamp();
            await Stream.Null.WriteAsync(buffer.AsMemory(0, count));
            latest = meter.WriteCompleted(writeStarted, Stopwatch.GetTimestamp()) ?? latest;
            bytes += count;
        }

        latest = meter.End(Stopwatch.GetTimestamp()) ?? latest;
        return (latest, clock.Elapsed.TotalMilliseconds);
    }

    private static async Task FastLoopbackAsync(Report report)
    {
        await using var node = Node("fast-loopback", 8 * 1024 * 1024, 256 * 1024 * 1024, 1024 * 1024);
        await using var relay = LoopbackMediaRelay.Start();
        using var reader = await Reader.OpenAsync(Register(relay, node), receiveBuffer: 4 * 1024 * 1024);
        var clock = Stopwatch.StartNew();
        var observations = await CaptureAsync(relay, () => reader.ReadAsync(node.Length));
        var elapsed = node.BodyDuration.TotalMilliseconds;
        var detail = string.Create(CultureInfo.InvariantCulture,
            $"body-ms={elapsed:0.0} player-ms={clock.Elapsed.TotalMilliseconds:0.0} updates={observations.Count} segments={observations.DistinctBy(o => o.SegmentId).Count()} {Describe(observations.LastOrDefault())}");
        if (elapsed is < 20 or > 200)
        {
            report.NotApplicable("throughput: fast loopback response outside 20-200 ms", detail);
            return;
        }

        report.Check("throughput: 20-200 ms real loopback yields exactly one cumulative observation of at least 2 MiB",
            observations.DistinctBy(o => o.SegmentId).Count() == 1
            && observations.LastOrDefault() is { Bytes: >= 2 * 1024 * 1024 } latest
            && latest.Duration >= TimeSpan.FromMilliseconds(20) && latest.Duration < TimeSpan.FromMilliseconds(200), detail);
    }

    private static async Task SuspensionAsync(Report report)
    {
        var (prior, _) = await CopyMemoryAsync(16 * 1024 * 1024, 30);
        var policy = new AdaptiveQualityPolicy(new AdaptiveBandwidthHistory());
        policy.ObserveThroughput(TimeSpan.Zero, prior);
        var before = policy.GetEstimate(TimeSpan.Zero).BitsPerSecond;
        var (observation, elapsed) = await CopyMemoryAsync(4 * 1024 * 1024, 120);
        report.Check("throughput: a 120 ms managed read suspension cannot produce two-second network evidence",
            observation is not null && (!observation.IsNetworkLimited || observation.Duration < TimeSpan.FromSeconds(2)),
            string.Create(CultureInfo.InvariantCulture, $"elapsed-ms={elapsed:0.0} {Describe(observation)}"));
        policy.ObserveThroughput(TimeSpan.FromSeconds(1), observation);
        report.Check("throughput: the production adaptive policy does not lower existing evidence after a managed read suspension",
            before > 0 && policy.GetEstimate(TimeSpan.FromSeconds(1)).BitsPerSecond >= before,
            string.Create(CultureInfo.InvariantCulture, $"before-kbps={before / 1000:0} after-kbps={policy.GetEstimate(TimeSpan.FromSeconds(1)).BitsPerSecond / 1000:0}"));
    }

    private static async Task SwitchAsync(Report report)
    {
        await using var a = Node("a", 3 * 1024 * 1024, EightMbps);
        await using var b = Node("b", 3 * 1024 * 1024, 500_000);
        await using var relay = LoopbackMediaRelay.Start();
        using var reader = await Reader.OpenAsync(Register(relay, a, b));
        await reader.ReadAsync(512 * 1024);
        var first = relay.GetStatistics("v1")?.ThroughputObservation;
        report.Check("throughput: first upstream has evidence before changing", first is not null && InRange(first.BytesPerSecond, EightMbps, 0.10));
        relay.SwitchNode("v1", b.Url);
        report.Check("throughput: changing upstream immediately retires its observation", relay.GetStatistics("v1")?.ThroughputObservation is null);
        var observations = await CaptureAsync(relay, () => reader.ReadAsync(a.Length - (512 * 1024)));
        var latest = observations.LastOrDefault();
        report.Check("throughput: two upstreams never share measured bytes or duration", latest is not null
            && InRange(latest.BytesPerSecond, 500_000, 0.10) && latest.Bytes <= b.Served && latest.Sequence > first!.Sequence, Describe(latest));
    }

    private static FakeNode Node(string name, int length, int rate, int chunk = 32 * 1024)
        => new(name, length) { Throttle = rate, PacePrecisely = true, ChunkBytes = chunk, Content = new byte[length] };

    private static string Register(LoopbackMediaRelay relay, params FakeNode[] nodes)
        => relay.Register("v1", new() { Upstreams = [.. nodes.Select(node => new RelayUpstream(node.Url, node.Name))] });

    private static bool InRange(double value, double expected, double tolerance)
        => value >= expected * (1 - tolerance) && value <= expected * (1 + tolerance);

    private static string Describe(RelayThroughputObservation? observation)
        => observation is null ? "n/a" : string.Create(CultureInfo.InvariantCulture,
            $"kbps={observation.BytesPerSecond * 8 / 1000:0} bytes={observation.Bytes} ms={observation.Duration.TotalMilliseconds:0} kind={(observation.IsNetworkLimited ? "network" : "floor")}");

    private static async Task<List<RelayThroughputObservation>> CaptureAsync(LoopbackMediaRelay relay, Func<Task> read)
    {
        var observations = new List<RelayThroughputObservation>();
        var job = read();
        while (!job.IsCompleted)
        {
            Add();
            await Task.Delay(2);
        }

        await job;
        await Task.Delay(20);
        Add();
        return observations;

        void Add()
        {
            if (relay.GetStatistics("v1")?.ThroughputObservation is { } observation
                && (observations.Count == 0 || observations[^1].Sequence != observation.Sequence)) observations.Add(observation);
        }
    }

    private sealed class Reader(TcpClient client) : IDisposable
    {
        public static async Task<Reader> OpenAsync(string url, int receiveBuffer = 32 * 1024)
        {
            var address = new Uri(url);
            var client = new TcpClient { ReceiveBufferSize = receiveBuffer, NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, address.Port);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {address.PathAndQuery} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n"));
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one) == 0) throw new IOException("Fake player received no response head.");
                head.Append((char)one[0]);
            }

            return new(client);
        }

        public async Task ReadAsync(long bytes, int throttle = 0)
        {
            var buffer = new byte[64 * 1024];
            var clock = Stopwatch.StartNew();
            long total = 0;
            while (total < bytes)
            {
                var count = await client.GetStream().ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, bytes - total)));
                if (count == 0) throw new IOException("Fake player received a short response.");
                total += count;
                if (throttle > 0)
                {
                    var wait = TimeSpan.FromSeconds((double)total / throttle) - clock.Elapsed;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait);
                }
            }
        }

        public void Dispose() => client.Dispose();
    }
}
