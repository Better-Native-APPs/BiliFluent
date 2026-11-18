// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using BiliCopilot.UI.Resolvers;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Core.Models;
using Richasy.MpvKernel.Dash;
using Richasy.MpvKernel.Relay;

namespace BiliFluent.Mpv.RelayCheck;

internal static partial class SeamlessSmoke
{
    public static async Task RunSponsorCacheAsync(Report report, Dictionary<string, string> options)
    {
        if (!options.TryGetValue("dll", out var dll) || !File.Exists(dll))
        {
            report.Check("sponsor-cache: dll option is required", false);
            return;
        }

        var output = options.GetValueOrDefault("out");
        if (output is not null) Directory.CreateDirectory(output);
        using var timer = new TimerResolution();
        try
        {
            var media = await FetchSponsorMediaAsync(options.GetValueOrDefault("bvid", "BV1vcsH6EEEr"), options.GetValueOrDefault("cid", "43620107071"));
            var video = media.Video.Where(stream => stream.Codec == 7).OrderByDescending(stream => stream.Id).First();
            var audio = media.Audio.OrderByDescending(stream => stream.Id).First();
            var bandwidth = video.Bandwidth + audio.Bandwidth;
            report.Note($"sponsor-cache: anonymous DASH, TLS on, vo/ao null; video-qn={video.Id} video-bps={video.Bandwidth} audio-bps={audio.Bandwidth}");
            if (options.ContainsKey("seekprobe"))
            {
                await ProbeSponsorSeeksAsync(report, dll, video, audio, output);
                return;
            }
            if (options.ContainsKey("probe"))
            {
                await ProbeSponsorLimitsAsync(report, dll, video, audio, bandwidth, output);
                return;
            }

            var passes = int.Parse(options.GetValueOrDefault("passes", "3"), CultureInfo.InvariantCulture);
            if (passes is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(options));
            var rows = new List<SponsorRow>();
            foreach (var speed in new[] { 1d, 2d })
            {
                for (var pass = 1; pass <= passes; pass++)
                {
                    var baseline = await SponsorSeekCaseAsync(report, dll, video, audio, bandwidth, speed, pass, "baseline", output);
                    var a = await SponsorSeekCaseAsync(report, dll, video, audio, bandwidth, speed, pass, "A", output);
                    var b = await SponsorSeekCaseAsync(report, dll, video, audio, bandwidth, speed, pass, "B", output);
                    rows.AddRange([baseline, a, b]);
                    report.Check($"sponsor-cache {speed:0}x pass {pass}: exact-seek baseline is really cached",
                        baseline.TargetCached && !baseline.PausedForCache && baseline.LongestStallMs <= 100,
                        string.Create(CultureInfo.InvariantCulture, $"target-cached={baseline.TargetCached} stall-ms={baseline.LongestStallMs:0.000}"));
                    report.Check($"sponsor-cache {speed:0}x pass {pass}: B matches cached exact seek and retains watchable cache",
                        baseline.TargetCached && baseline.LongestStallMs <= 100 && b.TargetCached
                        && !b.PausedForCache && b.LongestStallMs <= Math.Max(100, baseline.LongestStallMs + 50)
                        && b.AfterSeekForwardSeconds >= baseline.BeforeSeekForwardSeconds * 0.7,
                        string.Create(CultureInfo.InvariantCulture, $"B-stall={b.LongestStallMs:0.000} baseline-stall={baseline.LongestStallMs:0.000} retained={b.AfterSeekForwardSeconds / baseline.BeforeSeekForwardSeconds:0.000}"));
                    if (output is not null) await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(rows, JsonOptions));
                }
            }
        }
        catch (Exception ex)
        {
            // Native and HTTP exception messages can contain paths or signed URLs.
            report.Check("sponsor-cache: completed", false, ex.GetType().Name);
        }
    }

    private static async Task ProbeSponsorSeeksAsync(Report report, string dll, SponsorStream video, SponsorStream audio, string? output)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Get, video.Nodes[0].Url);
        foreach (var (name, value) in RequestHeaders) request.Headers.TryAddWithoutValidation(name, value);
        request.Headers.Range = new RangeHeaderValue(0, video.IndexRange.Last);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var index = DashIndex.Parse(await response.Content.ReadAsByteArrayAsync(), video.Initialization, video.IndexRange);
        var rows = new List<SponsorSeekProbeRow>();
        foreach (var dual in new[] { false, true })
        {
            foreach (var exact in new[] { false, true })
            {
                foreach (var advance in new[] { 1d, 10d })
                    rows.Add(await ProbeSponsorSeekCaseAsync(report, dll, video, audio, index, dual, exact, advance, false, output));
            }
        }

        foreach (var dual in new[] { false, true })
            rows.Add(await ProbeSponsorSeekCaseAsync(report, dll, video, audio, index, dual, true, 40, true, output));
        if (output is not null) await File.WriteAllTextAsync(Path.Combine(output, "seek-probe.json"), JsonSerializer.Serialize(rows, JsonOptions));
    }

    private static async Task<SponsorSeekProbeRow> ProbeSponsorSeekCaseAsync(Report report, string dll, SponsorStream video, SponsorStream audio,
        DashIndex index, bool dual, bool exact, double advance, bool enlarge, string? output)
    {
        var name = $"{(dual ? "dual" : "video")}-{(exact ? "exact" : "keyframes")}-{advance:0}";
        await using var relay = LoopbackMediaRelay.Start(new() { VerifyTls = true });
        var log = output is null ? null : Path.Combine(output, name + ".native.log");
        await using var client = await CreateSponsorClientAsync(dll, relay, video, audio, 1, 8 * SponsorCachePolicy.MiB,
            includeAudio: dual, nativeLog: log, nativeVerbosity: "all=trace");
        using var sampler = new SponsorSampler(client);
        if (!await WaitAsync(() => Position(client) >= 1 && !Flag(client, "core-idle"), 60_000)) throw new InvalidOperationException("Seek probe did not start.");
        if (enlarge)
        {
            var budget = SponsorCachePolicy.Calculate(ReadSponsorLimits(client), video.Bandwidth + (dual ? audio.Bandwidth : 0), Position(client), [new(25, 65)], true);
            await client.UpdateCacheLimitsAsync(budget.Limits.MaxBytes, budget.Limits.ReadAheadSeconds, budget.Limits.CacheSeconds);
        }

        if (!await WaitAsync(() => Position(client) >= 25, 60_000)) throw new InvalidOperationException("Seek probe did not reach position.");
        var position = Position(client);
        var target = enlarge ? 65 : position + advance;
        var keyframe = index.Fragments.Last(fragment => fragment.Time <= target && fragment.StartsWithKeyFrame).Time;
        var targetCached = sampler.Ranges.Any(range => range.Start <= target && target < range.End);
        var keyframeCached = sampler.Ranges.Any(range => range.Start <= keyframe && keyframe < range.End);
        var beforeState = client.GetPropertyString("demuxer-cache-state");
        var videoRequests = relay.GetStatistics("sponsor-video")?.Requests ?? 0;
        var audioRequests = relay.GetStatistics("sponsor-audio")?.Requests ?? 0;
        var actualOptions = "cache=" + client.GetPropertyString("options/cache")
            + " seekable=" + client.GetPropertyString("options/demuxer-seekable-cache")
            + " back-bytes=" + client.GetPropertyString("options/demuxer-max-back-bytes")
            + " max-bytes=" + client.GetPropertyString("options/demuxer-max-bytes")
            + " readahead=" + client.GetPropertyString("options/demuxer-readahead-secs")
            + " cache-secs=" + client.GetPropertyString("options/cache-secs");
        var from = sampler.Now;
        var flags = exact ? "absolute+exact" : "absolute+keyframes";
        if (client.Command("seek", target.ToString("0.000", CultureInfo.InvariantCulture), flags) != MpvError.Success)
            throw new InvalidOperationException("Seek probe command rejected.");
        if (!await WaitAsync(() => !Flag(client, "seeking") && !Flag(client, "core-idle") && Position(client) >= keyframe, 45_000))
            throw new InvalidOperationException("Seek probe did not land.");
        var latency = sampler.Now - from;
        var afterState = client.GetPropertyString("demuxer-cache-state");
        await Task.Delay(2000);
        var metrics = sampler.Measure(from - 100, sampler.Now);
        var row = new SponsorSeekProbeRow(name, dual, flags, position, target, keyframe, targetCached, keyframeCached,
            latency, metrics.Stall, metrics.Cache, metrics.Idle, actualOptions, beforeState, afterState,
            (relay.GetStatistics("sponsor-video")?.Requests ?? 0) - videoRequests, (relay.GetStatistics("sponsor-audio")?.Requests ?? 0) - audioRequests);
        report.Note(string.Create(CultureInfo.InvariantCulture, $"seek-probe name={name} position={position:0.000000} target={target:0.000000} nearest-keyframe={keyframe:0.000000} target-cached={targetCached} keyframe-cached={keyframeCached} seek-ms={latency:0.000} stall-ms={metrics.Stall:0.000} cache={metrics.Cache} idle={metrics.Idle} new-requests={row.NewVideoRequests}/{row.NewAudioRequests} {actualOptions}"));
        report.Note("seek-probe before-state=" + beforeState);
        report.Note("seek-probe after-state=" + afterState);
        if (output is not null) await sampler.WriteAsync(Path.Combine(output, name + ".samples.csv"));
        return row;
    }

    private static async Task ProbeSponsorLimitsAsync(Report report, string dll, SponsorStream video, SponsorStream audio, long bandwidth, string? output)
    {
        await using var relay = LoopbackMediaRelay.Start(new() { VerifyTls = true });
        await using var client = await CreateSponsorClientAsync(dll, relay, video, audio, 1, 8 * SponsorCachePolicy.MiB);
        using var sampler = new SponsorSampler(client);
        if (!await WaitAsync(() => Position(client) >= 12, 60_000)) throw new InvalidOperationException("Probe did not start.");
        if (!await WaitAsync(() => Flag(client, "demuxer-cache-idle"), 40_000)) throw new InvalidOperationException("Byte probe did not fill.");
        var baseline = ReadSponsorLimits(client);
        var before = Forward(client);
        report.Note(string.Create(CultureInfo.InvariantCulture, $"limit-probe: cache={client.GetPropertyString("options/cache")} seekable={client.GetPropertyString("options/demuxer-seekable-cache")} max-bytes={baseline.MaxBytes} readahead={baseline.ReadAheadSeconds:0.000} cache-secs={baseline.CacheSeconds:0.000} fw-secs={before:0.000} fw-bytes={sampler.Bytes}"));
        await client.UpdateCacheLimitsAsync(baseline.MaxBytes, 5, baseline.CacheSeconds);
        await Task.Delay(2000);
        var lowReadAhead = Forward(client);
        report.Note(string.Create(CultureInfo.InvariantCulture, $"limit-probe: runtime-readahead=5 fw-secs={lowReadAhead:0.000} fw-bytes={sampler.Bytes}"));
        var position = Position(client);
        var budget = SponsorCachePolicy.Calculate(baseline, bandwidth, position, [new(position + 13, position + 53)], true);
        await client.UpdateCacheLimitsAsync(budget.Limits.MaxBytes, budget.Limits.ReadAheadSeconds, budget.Limits.CacheSeconds);
        await Task.Delay(8000);
        var enlarged = Forward(client);
        var shrink = sampler.Now;
        await client.UpdateCacheLimitsAsync(baseline.MaxBytes, baseline.ReadAheadSeconds, baseline.CacheSeconds);
        var immediate = Forward(client);
        await Task.Delay(2000);
        var after = Forward(client);
        var metrics = sampler.Measure(shrink, sampler.Now);
        report.Note(string.Create(CultureInfo.InvariantCulture, $"limit-probe: extra-bytes={budget.ExtraBytes} skip-secs={budget.SkipSeconds:0.000} enlarged-fw={enlarged:0.000} immediate-shrink-fw={immediate:0.000} after-2s-fw={after:0.000} shrink-stall-ms={metrics.Stall:0.000} cache={metrics.Cache} idle={metrics.Idle}"));
        report.Check("limit-probe: runtime enlargement resumes forward reading", budget.ExtraBytes > 0 && enlarged > before + 20);
        report.Check("limit-probe: shrinking preserves forward packets and playback", immediate >= enlarged - 1 && after >= immediate - 3 && !metrics.Cache && !metrics.Idle);
        if (output is not null) await sampler.WriteAsync(Path.Combine(output, "limits.samples.csv"));

        // A second core establishes that the seconds ceiling can bind independently of bytes.
        await using var secondsRelay = LoopbackMediaRelay.Start(new() { VerifyTls = true });
        await using var secondsClient = await CreateSponsorClientAsync(dll, secondsRelay, video, audio, 1, 64 * SponsorCachePolicy.MiB, 10);
        using var secondsSampler = new SponsorSampler(secondsClient);
        if (!await WaitAsync(() => Position(secondsClient) >= 8, 60_000)) throw new InvalidOperationException("Seconds probe did not start.");
        var secondsBefore = Forward(secondsClient);
        await secondsClient.UpdateCacheLimitsAsync(64 * SponsorCachePolicy.MiB, 30, 30);
        await Task.Delay(5000);
        var secondsAfter = Forward(secondsClient);
        report.Note(string.Create(CultureInfo.InvariantCulture, $"seconds-probe: max-mb=64 limits=10->30 fw-secs={secondsBefore:0.000}->{secondsAfter:0.000} fw-bytes={secondsSampler.Bytes}"));
        report.Check("seconds-probe: seconds can bind and runtime changes resume reading", secondsBefore < 15 && secondsAfter > secondsBefore + 12);
    }

    private static async Task<SponsorRow> SponsorSeekCaseAsync(Report report, string dll, SponsorStream video, SponsorStream audio,
        long bandwidth, double speed, int pass, string kind, string? output)
    {
        await using var relay = LoopbackMediaRelay.Start(new() { VerifyTls = true });
        var nativeLog = output is null ? null : Path.Combine(output, $"{kind}-{speed:0}x-{pass}.native.log");
        await using var client = await CreateSponsorClientAsync(dll, relay, video, audio, speed, 8 * SponsorCachePolicy.MiB, nativeLog: nativeLog);
        using var sampler = new SponsorSampler(client);
        if (!await WaitAsync(() => Position(client) >= 1 && !Flag(client, "core-idle"), 60_000)) throw new InvalidOperationException("Case did not start.");
        var baseline = ReadSponsorLimits(client);
        var budget = SponsorCachePolicy.Calculate(baseline, bandwidth, Position(client), [new(25, 65)], kind == "B");
        if (kind == "B") await client.UpdateCacheLimitsAsync(budget.Limits.MaxBytes, budget.Limits.ReadAheadSeconds, budget.Limits.CacheSeconds);
        if (!await WaitAsync(() => Position(client) >= 25, 60_000)) throw new InvalidOperationException("Case did not reach the skip.");
        var before = Forward(client);
        // With no back cache, +1s can need a keyframe that has already been discarded.
        // Ten seconds ahead keeps the exact-seek keyframe inside the forward cache.
        var target = kind == "baseline" ? Position(client) + 10 : 65;
        var cachedTarget = sampler.Ranges.Any(range => range.Start <= target && target < range.End);
        var ranges = string.Join(",", sampler.Ranges.Select(range => string.Create(CultureInfo.InvariantCulture, $"{range.Start:0.000}-{range.End:0.000}")));
        var videoRequests = relay.GetStatistics("sponsor-video")?.Requests ?? 0;
        var audioRequests = relay.GetStatistics("sponsor-audio")?.Requests ?? 0;
        var from = sampler.Now;
        if (client.Command("seek", target.ToString("0.000", CultureInfo.InvariantCulture), "absolute+exact") != MpvError.Success)
            throw new InvalidOperationException("Exact seek rejected.");
        if (!await WaitAsync(() => Position(client) >= target && !Flag(client, "seeking"), 45_000)) throw new InvalidOperationException("Seek did not land.");
        var latency = sampler.Now - from;
        var instant = Forward(client);
        var bytes = sampler.Bytes;
        if (kind == "B") await client.UpdateCacheLimitsAsync(baseline.MaxBytes, baseline.ReadAheadSeconds, baseline.CacheSeconds);
        await Task.Delay(2000);
        var metrics = sampler.Measure(from - 100, sampler.Now);
        var row = new SponsorRow(kind, pass, speed, baseline.MaxBytes, budget.ExtraBytes, before, instant, bytes,
            latency, metrics.Stall, metrics.Cache, metrics.Idle, metrics.SampleGap, Forward(client), cachedTarget, ranges,
            (relay.GetStatistics("sponsor-video")?.Requests ?? 0) - videoRequests, (relay.GetStatistics("sponsor-audio")?.Requests ?? 0) - audioRequests);
        report.Note(string.Create(CultureInfo.InvariantCulture, $"sponsor-row kind={kind} pass={pass} speed={speed:0} base-bytes={row.BaseBytes} extra-bytes={row.ExtraBytes} before-fw={before:0.000000} after-fw={instant:0.000000} after-fw-bytes={bytes} seek-ms={latency:0.000} stall-ms={metrics.Stall:0.000} paused-for-cache={metrics.Cache} core-idle={metrics.Idle} sample-gap-ms={metrics.SampleGap:0.000} restored-fw={row.RestoredForwardSeconds:0.000000} target-cached={cachedTarget} ranges={ranges} new-requests={row.NewVideoRequests}/{row.NewAudioRequests}"));
        if (output is not null) await sampler.WriteAsync(Path.Combine(output, $"{kind}-{speed:0}x-{pass}.samples.csv"));
        return row;
    }

    private static async Task<MpvClient> CreateSponsorClientAsync(string dll, LoopbackMediaRelay relay,
        SponsorStream video, SponsorStream audio, double speed, long maxBytes, double? seconds = null,
        bool includeAudio = true, string? nativeLog = null, string nativeVerbosity = "all=v")
    {
        var client = await MpvClient.CreateAsync(dll, new() { UseConfig = false, LoadScripts = false });
        foreach (var (name, value) in new (string, string)[]
        {
            ("vo", "null"), ("ao", "null"), ("hwdec", "no"), ("idle", "yes"), ("keep-open", "yes"), ("terminal", "no"),
            ("demuxer-max-bytes", maxBytes.ToString(CultureInfo.InvariantCulture)), ("demuxer-max-back-bytes", "0"),
            ("demuxer-readahead-secs", (seconds ?? 60).ToString(CultureInfo.InvariantCulture)),
            ("cache-pause", "yes"), ("cache-pause-wait", "3"), ("tls-verify", "yes"),
        })
        {
            if (client.SetOptionString(name, value) != MpvError.Success) throw new InvalidOperationException("Headless option rejected.");
        }

        if (seconds.HasValue && client.SetProperty("cache-secs", seconds.Value) != MpvError.Success)
            throw new InvalidOperationException("Seconds option rejected.");
        if (client.SetProperty("speed", speed) != MpvError.Success) throw new InvalidOperationException("Speed option rejected.");
        if (nativeLog is not null)
        {
            if (client.SetOptionString("log-file", nativeLog) != MpvError.Success || client.SetOptionString("msg-level", nativeVerbosity) != MpvError.Success)
                throw new InvalidOperationException("Native trace option rejected.");
        }

        var videoUrl = relay.Register("sponsor-video", new() { Upstreams = video.Nodes, Headers = RequestHeaders });
        var audioUrl = includeAudio ? relay.Register("sponsor-audio", new() { Upstreams = audio.Nodes, Headers = RequestHeaders }) : null;
        var load = audioUrl is null ? client.Command("loadfile", videoUrl, "replace")
            : client.Command("loadfile", videoUrl, "replace", "0", "audio-file=\"" + audioUrl + "\"");
        if (load != MpvError.Success)
            throw new InvalidOperationException("Headless load rejected.");
        return client;
    }

    private static SponsorCacheLimits ReadSponsorLimits(MpvClient client)
        => new((long)ReadNumber(client, "options/demuxer-max-bytes"), ReadNumber(client, "options/demuxer-readahead-secs"), ReadNumber(client, "options/cache-secs"));

    private static double ReadNumber(MpvClient client, string name)
        => client.GetProperty(name, out double value) == MpvError.Success ? value : double.NaN;

    private static double Forward(MpvClient client) => ReadNumber(client, "demuxer-cache-duration");

    private static async Task<SponsorMedia> FetchSponsorMediaAsync(string bvid, string cid)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.bilibili.com/x/player/playurl?bvid={Uri.EscapeDataString(bvid)}&cid={Uri.EscapeDataString(cid)}&fnval=4048&fourk=1&qn=127&try_look=1");
        foreach (var (name, value) in RequestHeaders) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (document.RootElement.GetProperty("code").GetInt32() != 0) throw new InvalidOperationException("Anonymous playurl failed.");
        var dash = document.RootElement.GetProperty("data").GetProperty("dash");
        return new(Read(dash.GetProperty("video")), Read(dash.GetProperty("audio")));

        static List<SponsorStream> Read(JsonElement list)
            => [.. list.EnumerateArray().Select(item =>
            {
                var urls = new List<string> { item.GetProperty("base_url").GetString()! };
                if (item.TryGetProperty("backup_url", out var backup) && backup.ValueKind == JsonValueKind.Array)
                    urls.AddRange(backup.EnumerateArray().Select(url => url.GetString()!));
                var segment = item.GetProperty("segment_base");
                if (!DashByteRange.TryParse(segment.GetProperty("initialization").GetString(), out var initialization)
                    || !DashByteRange.TryParse(segment.GetProperty("index_range").GetString(), out var indexRange))
                    throw new InvalidDataException("Anonymous stream has no DASH index.");
                return new SponsorStream(item.GetProperty("id").GetInt32(), item.GetProperty("codecid").GetInt32(), item.GetProperty("bandwidth").GetInt64(),
                    [.. urls.Distinct().Select((url, i) => new RelayUpstream(url, "node" + i))], initialization, indexRange);
            })];
    }

    private sealed record SponsorMedia(List<SponsorStream> Video, List<SponsorStream> Audio);
    private sealed record SponsorStream(int Id, int Codec, long Bandwidth, List<RelayUpstream> Nodes, DashByteRange Initialization, DashByteRange IndexRange);
    private sealed record SponsorSeekProbeRow(string Name, bool Dual, string Flags, double Position, double Target, double NearestKeyframe,
        bool TargetCached, bool KeyframeCached, double LatencyMs, double StallMs, bool PausedForCache, bool CoreIdle,
        string ActualOptions, string? BeforeState, string? AfterState, long NewVideoRequests, long NewAudioRequests);
    private sealed record SponsorRow(string Kind, int Pass, double Speed, long BaseBytes, long ExtraBytes, double BeforeSeekForwardSeconds,
        double AfterSeekForwardSeconds, long AfterSeekForwardBytes, double SeekLatencyMs, double LongestStallMs,
        bool PausedForCache, bool CoreIdle, double SampleGapMs, double RestoredForwardSeconds, bool TargetCached, string Ranges,
        long NewVideoRequests, long NewAudioRequests);
    private readonly record struct SponsorSample(double WallMs, double Position, double Forward, long Bytes, bool Cache, bool Idle);

    private sealed class SponsorSampler : IDisposable
    {
        private static readonly string[] CsvHeader = ["wall_ms,time_pos,fw_seconds,fw_bytes,paused_for_cache,core_idle"];
        private readonly MpvClient _client;
        private readonly ConcurrentQueue<SponsorSample> _samples = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Thread _thread;
        private bool _stop;
        private long _bytes = -1;
        private MpvSeekableRange[] _ranges = [];

        public SponsorSampler(MpvClient client)
        {
            _client = client;
            client.CacheStateChanged += OnCacheStateChanged;
            _thread = new(() =>
            {
                while (!Volatile.Read(ref _stop))
                {
                    var start = Now;
                    _samples.Enqueue(new(Now, Position(client), Forward(client), Bytes, Flag(client, "paused-for-cache"), Flag(client, "core-idle")));
                    while (!Volatile.Read(ref _stop) && Now - start < 4) Thread.SpinWait(64);
                }
            }) { IsBackground = true, Name = "sponsor-cache-check-sampler" };
            _thread.Start();
        }

        public double Now => _clock.Elapsed.TotalMilliseconds;
        public long Bytes => Interlocked.Read(ref _bytes);
        public MpvSeekableRange[] Ranges => Volatile.Read(ref _ranges);

        private void OnCacheStateChanged(object? sender, MpvCacheStateEventArgs e)
        {
            Interlocked.Exchange(ref _bytes, e.FwBytes);
            Volatile.Write(ref _ranges, e.SeekableRanges.ToArray());
        }

        public (double Stall, bool Cache, bool Idle, double SampleGap) Measure(double from, double to)
        {
            var samples = _samples.Where(sample => sample.WallMs >= from && sample.WallMs <= to).ToArray();
            if (samples.Length < 2) return (double.NaN, true, true, double.NaN);
            var changed = samples[0].WallMs;
            double gap = 0, sampleGap = 0;
            var previous = samples[0];
            foreach (var sample in samples.Skip(1))
            {
                sampleGap = Math.Max(sampleGap, sample.WallMs - previous.WallMs);
                gap = Math.Max(gap, sample.WallMs - changed);
                if (double.IsFinite(sample.Position) && Math.Abs(sample.Position - previous.Position) > 0.00001) changed = sample.WallMs;
                previous = sample;
            }

            return (gap, samples.Any(sample => sample.Cache), samples.Any(sample => sample.Idle), sampleGap);
        }

        public Task WriteAsync(string path)
            => File.WriteAllLinesAsync(path, CsvHeader.Concat(_samples.Select(sample =>
                string.Create(CultureInfo.InvariantCulture, $"{sample.WallMs:0.000},{sample.Position:0.000000},{sample.Forward:0.000000},{sample.Bytes},{sample.Cache},{sample.Idle}"))));

        public void Dispose()
        {
            Volatile.Write(ref _stop, true);
            _thread.Join();
            _client.CacheStateChanged -= OnCacheStateChanged;
        }
    }
}
