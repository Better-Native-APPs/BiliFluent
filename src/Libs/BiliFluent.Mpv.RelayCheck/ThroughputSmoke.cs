// Copyright (c) BiliFluent. All rights reserved.

using System.Diagnostics;
using System.Globalization;
using BiliCopilot.UI.Resolvers;
using Microsoft.Extensions.Logging;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Core.Models;
using Richasy.MpvKernel.Relay;

namespace BiliFluent.Mpv.RelayCheck;

/// <summary>Actual null-output libmpv, relay measurements and the production adaptive evidence policy.</summary>
internal static class ThroughputSmoke
{
    public static async Task RunAsync(Report report, Dictionary<string, string> options)
    {
        if (!options.TryGetValue("dll", out var dll) || !File.Exists(dll))
        {
            report.Check("throughput-smoke: dll=<path to libmpv-2.dll> is required", false);
            return;
        }

        using var resolution = new CheckTimerResolution();
        var verifyTls = !string.Equals(options.GetValueOrDefault("tls", "on"), "off", StringComparison.OrdinalIgnoreCase);
        var media = await Smoke.FetchAsync("BV1vcsH6EEEr", "43620107071");
        var video = media.Video.Where(stream => stream.Codec == 7 && stream.Id <= 64).OrderByDescending(stream => stream.Id).First();
        var audio = media.Audio.OrderByDescending(stream => stream.Id).First();
        report.Note(string.Create(CultureInfo.InvariantCulture,
            $"BV1vcsH6EEEr cid=43620107071 qn={video.Id} v-kbps={video.Bandwidth / 1000} duration={media.Duration:0}s vo=null ao=null tls-verify={verifyTls}"));
        await report.RunAsync("throughput-smoke: public playback", () => PublicAsync(report, dll, video, audio, verifyTls));
        if (report.Failed > 0) return;

        var body = await DownloadAsync(video.Urls[0], verifyTls);
        report.Note(string.Create(CultureInfo.InvariantCulture, $"paced media bytes={body.Length} upstream-kbps=8000"));
        await report.RunAsync("throughput-smoke: paced playback", () => LimitedAsync(report, dll, body));
    }

    private static async Task PublicAsync(Report report, string dll, Smoke.DashStream video, Smoke.DashStream audio, bool verifyTls)
    {
        var counter = new FillCounter();
        await using var relay = LoopbackMediaRelay.Start(new() { VerifyTls = verifyTls }, counter);
        var videoUrl = relay.Register("v1", Registration(video));
        var audioUrl = relay.Register("a1", Registration(audio));
        await using var client = await CreateAsync(dll, 60);
        var probe = new Probe(relay);
        Check(client.Command(["loadfile", videoUrl, "replace", "0", "audio-file=\"" + audioUrl + "\""]), "loadfile");
        await probe.WatchAsync(TimeSpan.FromSeconds(3));
        report.Check("throughput-smoke: public playback starts with null video and audio output", Read(client, "time-pos") > 1);
        report.Check("throughput-smoke: public video produces an observation within two seconds",
            probe.FirstObservationMs is <= 2000,
            string.Create(CultureInfo.InvariantCulture, $"first-observation-ms={probe.FirstObservationMs:0.0}"));

        var initial = probe.Observations.FirstOrDefault();
        var filling = probe.Observations.LastOrDefault(observation => observation.SegmentId == initial?.SegmentId);
        var reference = counter.Snapshot();
        var ratio = filling is not null && reference.Rate > 0 ? filling.BytesPerSecond / reference.Rate : double.NaN;
        report.Check("throughput-smoke: observed throughput is 0.5-1.2 times independently counted cache-fill throughput",
            ratio is >= 0.5 and <= 1.2,
            string.Create(CultureInfo.InvariantCulture,
                $"observed-kbps={filling?.BytesPerSecond * 8 / 1000:0} reference-kbps={reference.Rate * 8 / 1000:0} reference-bytes={reference.Bytes} reference-ms={reference.Duration.TotalMilliseconds:0.0} ratio={ratio:0.000}"));
        var before = probe.Evidence;
        var beforeBytes = relay.GetStatistics("v1")?.UpstreamBytes ?? 0;
        probe.TrackMinimumEvidence();
        await probe.WatchAsync(TimeSpan.FromSeconds(30));
        var trickleBytes = (relay.GetStatistics("v1")?.UpstreamBytes ?? 0) - beforeBytes;
        var trickleRate = trickleBytes / 30d;
        report.Check("throughput-smoke: steady playback actually downloads a low-rate upstream trickle",
            trickleBytes > 0 && trickleRate < reference.Rate * 0.1,
            string.Create(CultureInfo.InvariantCulture, $"trickle-bytes={trickleBytes} trickle-kbps={trickleRate * 8 / 1000:0}"));
        report.Check("throughput-smoke: thirty seconds of steady trickle never lowers production adaptive evidence",
            before > 0 && probe.MinimumEvidence >= before,
            string.Create(CultureInfo.InvariantCulture, $"before-kbps={before / 1000:0} min-kbps={probe.MinimumEvidence / 1000:0} after-kbps={probe.Evidence / 1000:0} time-pos={Read(client, "time-pos"):0.0}"));
    }

    private static async Task LimitedAsync(Report report, string dll, byte[] body)
    {
        await using var node = new FakeNode("paced-media", body.Length) { Content = body, Throttle = 1_000_000, PacePrecisely = true, ChunkBytes = 32 * 1024 };
        await using var relay = LoopbackMediaRelay.Start();
        var url = relay.Register("v1", new() { Upstreams = [new(node.Url, node.Name)] });
        await using var client = await CreateAsync(dll, 30);
        var probe = new Probe(relay);
        Check(client.Command(["loadfile", url, "replace"]), "loadfile");
        await probe.WatchAsync(TimeSpan.FromSeconds(8));
        var filling = probe.Observations.LastOrDefault(observation => observation.IsNetworkLimited);
        report.Check("throughput-smoke: actual mpv cache fill on 8 Mbps is within 15 percent", filling is not null
            && filling.BytesPerSecond is >= 850_000 and <= 1_150_000,
            string.Create(CultureInfo.InvariantCulture, $"observed-kbps={filling?.BytesPerSecond * 8 / 1000:0} ms={filling?.Duration.TotalMilliseconds:0} time-pos={Read(client, "time-pos"):0.0}"));

        var position = Read(client, "time-pos");
        if (!double.IsFinite(position) || position < 3)
        {
            report.Check("throughput-smoke: paced playback has a cache-backed seek position", false);
            return;
        }

        var requests = relay.GetStatistics("v1")?.Requests;
        var beforeSeek = probe.Observations.Count;
        Check(client.Command(["seek", (position - 2).ToString(CultureInfo.InvariantCulture), "absolute+exact"]), "seek");
        await probe.WatchAsync(TimeSpan.FromSeconds(3));
        report.Check("throughput-smoke: the seek stays inside the existing mpv cache", relay.GetStatistics("v1")?.Requests == requests);
        report.Check("throughput-smoke: cache seek produces no network observation above 10.4 Mbps",
            probe.Observations.Skip(beforeSeek).All(observation => !observation.IsNetworkLimited || observation.BytesPerSecond <= 1_300_000),
            Maximum(probe.Observations.Skip(beforeSeek)));

        var beforeLimits = probe.Observations.Count;
        await client.UpdateCacheLimitsAsync(32 * 1024 * 1024, 120, 120);
        await probe.WatchAsync(TimeSpan.FromSeconds(10));
        report.Check("throughput-smoke: UpdateCacheLimitsAsync resumes upstream reads", probe.Observations.Count > beforeLimits);
        report.Check("throughput-smoke: increasing cache limits produces no network observation above 10.4 Mbps",
            probe.Observations.Skip(beforeLimits).All(observation => !observation.IsNetworkLimited || observation.BytesPerSecond <= 1_300_000),
            Maximum(probe.Observations.Skip(beforeLimits)));
    }

    private static string Maximum(IEnumerable<RelayThroughputObservation> observations)
        => string.Create(CultureInfo.InvariantCulture,
            $"network-max-kbps={observations.Where(observation => observation.IsNetworkLimited).Select(observation => observation.BytesPerSecond * 8 / 1000).DefaultIfEmpty().Max():0}");

    private static RelayStreamRegistration Registration(Smoke.DashStream stream)
        => new()
        {
            Upstreams = [.. stream.Urls.Select((url, index) => new RelayUpstream(url, "node" + index))],
            Headers = new Dictionary<string, string> { ["User-Agent"] = Smoke.UserAgent, ["Referer"] = Smoke.Referer },
        };

    private static async Task<MpvClient> CreateAsync(string dll, double cacheSeconds)
    {
        var client = await MpvClient.CreateAsync(dll, new MpvInitializeOptions { UseConfig = false, LoadScripts = false });
        foreach (var (name, value) in new (string, string)[]
        {
            ("vo", "null"), ("ao", "null"), ("idle", "yes"), ("ytdl", "no"), ("terminal", "no"), ("hwdec", "no"),
            ("cache", "yes"), ("keep-open", "yes"), ("cache-pause-wait", "1"), ("demuxer-max-back-bytes", "8MiB"),
        }) Check(client.SetOptionString(name, value), name);
        await client.UpdateCacheLimitsAsync(64 * 1024 * 1024, cacheSeconds, cacheSeconds);
        return client;
    }

    private static double Read(MpvClient client, string name)
        => client.GetProperty(name, out double value) == MpvError.Success ? value : double.NaN;

    private static void Check(MpvError error, string operation)
    {
        if (error != MpvError.Success) throw new InvalidOperationException($"mpv {operation} failed: {error}");
    }

    private static async Task<byte[]> DownloadAsync(string url, bool verifyTls)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false, UseCookies = false };
        if (!verifyTls)
        {
#pragma warning disable CA5359 // The smoke explicitly mirrors the application's diagnostic TLS setting.
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", Smoke.UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", Smoke.Referer);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }

    private sealed class Probe(LoopbackMediaRelay relay)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly AdaptiveQualityPolicy _policy = new(new AdaptiveBandwidthHistory());
        private bool _trackMinimum;
        public List<RelayThroughputObservation> Observations { get; } = [];
        public double? FirstObservationMs { get; private set; }
        public double Evidence => _policy.GetEstimate(_clock.Elapsed).BitsPerSecond;
        public double MinimumEvidence { get; private set; }

        public void TrackMinimumEvidence()
        {
            _trackMinimum = true;
            MinimumEvidence = Evidence;
        }

        public async Task WatchAsync(TimeSpan duration)
        {
            var until = _clock.Elapsed + duration;
            while (_clock.Elapsed < until)
            {
                Poll();
                await Task.Delay(20);
            }

            Poll();
        }

        private void Poll()
        {
            if (relay.GetStatistics("v1")?.ThroughputObservation is { } observation
                && (Observations.Count == 0 || observation.Sequence > Observations[^1].Sequence))
            {
                FirstObservationMs ??= _clock.Elapsed.TotalMilliseconds;
                Observations.Add(observation);
                _policy.ObserveThroughput(_clock.Elapsed, observation);
            }

            if (_trackMinimum) MinimumEvidence = Math.Min(MinimumEvidence, Evidence);
        }
    }

    /// <summary>Counts raw trace reads independently; no observation fields enter this reference.</summary>
    private sealed class FillCounter : ILogger
    {
        private readonly object _sync = new();
        private readonly Dictionary<long, FillWindow> _windows = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Trace;

        public (long Bytes, TimeSpan Duration, double Rate) Snapshot()
        {
            lock (_sync)
            {
                var best = _windows.Values.Where(window => window.Last > window.First).MaxBy(window => window.Bytes);
                if (best is null) return default;
                var duration = Stopwatch.GetElapsedTime(best.First, best.Last);
                return (best.Bytes, duration, best.Bytes / duration.TotalSeconds);
            }
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Trace || state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
            var values = fields.ToDictionary(pair => pair.Key, pair => pair.Value);
            if (values.GetValueOrDefault("Key") is not "v1" || values.GetValueOrDefault("Source") is not "upstream"
                || values.GetValueOrDefault("Count") is not int count || count == 0
                || values.GetValueOrDefault("Request") is not long request || values.GetValueOrDefault("DeliveryMs") is not double delivery) return;
            var completed = Stopwatch.GetTimestamp() - (long)(delivery * Stopwatch.Frequency / 1000);
            lock (_sync)
            {
                if (!_windows.TryGetValue(request, out var window)) _windows[request] = window = new() { First = completed };
                if (window.Ended) return;
                window.Last = completed;
                window.Bytes += count;
                window.Ended = delivery > 25;
            }
        }

        private sealed class FillWindow
        {
            public long First;
            public long Last;
            public long Bytes;
            public bool Ended;
        }
    }
}
