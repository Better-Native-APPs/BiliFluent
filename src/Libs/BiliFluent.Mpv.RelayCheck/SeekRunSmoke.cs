// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Core.Enums;
using Richasy.MpvKernel.Core.Models;
using Richasy.MpvKernel.Dash;
using Richasy.MpvKernel.Relay;

namespace BiliFluent.Mpv.RelayCheck;

internal static partial class SeamlessSmoke
{
    private static readonly Regex SeekRunLogTime = new(@"^\[\s*(\d+\.\d+)\]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex SeekRunStreamSeek = new(@"stream level seek from (\d+) to (\d+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly string[] SeekRunCsvHeader = ["wall_ms,phase,time_pos,audio_pts,video_forward,seeking,core_idle"];
    private static readonly JsonSerializerOptions SeekRunJsonOptions = new(JsonOptions)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static async Task RunSeekRunAsync(Report report, Dictionary<string, string> options)
    {
        if (!options.TryGetValue("dll", out var dll) || !File.Exists(dll))
        {
            report.Check("seek-run: dll option is required", false);
            return;
        }

        var passes = int.Parse(options.GetValueOrDefault("passes", "3"), CultureInfo.InvariantCulture);
        if (passes is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(options));
        var output = options.GetValueOrDefault("out", Path.Combine(Path.GetTempPath(), "bf-seekrun"));
        var reportPath = options.GetValueOrDefault("report", Path.Combine(output, "report.md"));
        Directory.CreateDirectory(output);
        var rows = new List<SeekRunRow>();
        if (options.GetValueOrDefault("analyze") == "1")
        {
            var recorded = JsonSerializer.Deserialize<List<SeekRunRow>>(await File.ReadAllTextAsync(Path.Combine(output, "results.json")), SeekRunJsonOptions)
                ?? throw new InvalidDataException("Seek-run results are missing.");
            foreach (var original in recorded)
            {
                var samples = (await File.ReadAllLinesAsync(Path.Combine(output, original.Samples!))).Skip(1).Select(line =>
                {
                    var values = line.Split(',');
                    return new SeekRunSample(double.Parse(values[0], CultureInfo.InvariantCulture), values[1],
                        double.Parse(values[2], CultureInfo.InvariantCulture), double.Parse(values[3], CultureInfo.InvariantCulture),
                        double.Parse(values[4], CultureInfo.InvariantCulture), bool.Parse(values[5]), bool.Parse(values[6]));
                }).ToList();
                var prerequisite = original.BeforePosition >= (original.Kind is "A" or "B" ? 306 : original.Kind == "D" ? 5 : 0.2)
                    && original.BeforeVideoForward >= (original.Kind == "C" ? 90 : original.Kind == "D" ? 0 : 60)
                    && (original.Kind == "D" ? original.BufferedEdge < 400 && original.Target >= 0
                        : original.AudioEnded && (original.Kind != "C" || original.BeforePosition < 30));
                var row = await AnalyzeSeekRunAsync(original with { Prerequisites = prerequisite }, output, samples);
                rows.Add(row);
                ReportSeekRunRow(report, row);
            }

            await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(rows, SeekRunJsonOptions));
            await WriteSeekRunReportAsync(reportPath, rows);
            return;
        }

        report.Note("seek-run: anonymous dual DASH qn=80, TLS on, vo/ao=null, 64 MiB forward / 60 s readahead / 32 MiB back; native all=v logs, 20 ms position samples");
        using var resolution = new TimerResolution();
        for (var pass = 1; pass <= passes; pass++)
        {
            foreach (var kind in new[] { "A", "B", "C", "D" })
            {
                SeekRunRow row;
                try
                {
                    var media = await FetchSponsorMediaAsync(options.GetValueOrDefault("bvid", "BV1vcsH6EEEr"), options.GetValueOrDefault("cid", "43620107071"));
                    var video = media.Video.Single(stream => stream.Id == 80 && stream.Codec == 7);
                    var audio = media.Audio.OrderByDescending(stream => stream.Id).First();
                    row = await SeekRunCaseAsync(dll, video, audio, kind, pass, output);
                    ReportSeekRunRow(report, row);
                }
                catch (Exception ex)
                {
                    // Native and HTTP messages can contain private paths or signed URLs.
                    row = new(pass, kind) { Detail = ex.GetType().Name };
                    report.Check($"seek-run pass {pass} {kind}: completed", false, row.Detail);
                }

                rows.Add(row);
                await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(rows, SeekRunJsonOptions));
                await WriteSeekRunReportAsync(reportPath, rows);
            }
        }
    }

    private static void ReportSeekRunRow(Report report, SeekRunRow row)
    {
        report.Check($"seek-run pass {row.Pass} {row.Kind}: prerequisites", row.Prerequisites, row.Detail);
        report.Check($"seek-run pass {row.Pass} {row.Kind}: matches expected {(row.Kind is "A" or "C" ? "clean" : "broken")}",
            row.Prerequisites && row.Clean == (row.Kind is "A" or "C"),
            string.Create(CultureInfo.InvariantCulture, $"invalid-pts={row.InvalidAudioPts} resets={row.AudioTimestampResets} max-position-jump={row.MaxPositionJump:0.000000} backwards={row.UnexpectedBackwardSteps} peak-stream-seeks={row.PeakVideoSeeks}/{row.PeakAudioSeeks}/{row.PeakUnknownSeeks}"));
    }

    private static async Task<SeekRunRow> SeekRunCaseAsync(string dll, SponsorStream video, SponsorStream audio, string kind, int pass, string output)
    {
        var name = $"pass-{pass}-{kind}";
        var nativeLog = Path.Combine(output, name + ".mpv.log");
        var samplesPath = Path.Combine(output, name + ".samples.csv");
        var samples = new List<SeekRunSample>();
        var clock = Stopwatch.StartNew();
        await using var relay = LoopbackMediaRelay.Start(new() { VerifyTls = true });
        var videoIndex = await FetchSeekRunIndexAsync(video);
        var audioIndex = await FetchSeekRunIndexAsync(audio);
        await using var client = await MpvClient.CreateAsync(dll, new() { UseConfig = false, LoadScripts = false });
        MpvCacheStateEventArgs? cache = null;
        client.CacheStateChanged += (_, value) => Volatile.Write(ref cache, value);
        foreach (var (key, value) in new (string, string)[]
        {
            ("vo", "null"), ("ao", "null"), ("hwdec", "no"), ("ytdl", "no"), ("cookies", "no"), ("terminal", "no"),
            ("idle", "yes"), ("keep-open", "yes"), ("cache", "yes"), ("tls-verify", "yes"),
            ("demuxer-max-bytes", "64MiB"), ("demuxer-readahead-secs", "60"), ("demuxer-max-back-bytes", "32MiB"),
            ("cache-pause", "yes"), ("cache-pause-wait", "3"), ("log-file", nativeLog), ("msg-level", "all=v"),
        })
        {
            if (client.SetOptionString(key, value) != MpvError.Success) throw new InvalidOperationException("Seek-run option rejected.");
        }

        var version = client.GetPropertyString("mpv-version");
        var ffmpeg = client.GetPropertyString("ffmpeg-version");
        var videoUrl = relay.Register("seekrun-video", new() { Upstreams = video.Nodes, Headers = RequestHeaders });
        var audioUrl = relay.Register("seekrun-audio", new() { Upstreams = audio.Nodes, Headers = RequestHeaders });
        var start = kind is "A" or "B" ? 300d : 0d;
        await client.PlayAsync(videoUrl, new()
        {
            AudioTracks = [audioUrl], EnableCookies = false, EnableYtdl = false, StartPosition = start,
        });

        double VideoForward() => Volatile.Read(ref cache)?.VideoCacheSeconds ?? Forward(client);
        bool AudioEnded() => relay.GetStatistics("seekrun-audio") is { Length: > 0 } statistics
            && (statistics.ForwardUpstreamOffset ?? statistics.ForwardBytes) >= statistics.Length.Value;
        async Task<bool> WaitForAsync(Func<bool> condition, string phase, int timeoutMs)
        {
            var waiting = Stopwatch.StartNew();
            while (waiting.ElapsedMilliseconds < timeoutMs)
            {
                samples.Add(CaptureSeekRunSample(client, clock, phase));
                if (condition()) return true;
                await Task.Delay(20);
            }

            return false;
        }

        var started = await WaitForAsync(() => Position(client) >= start + 0.2 && !Flag(client, "core-idle"), "start", 60_000);
        var ready = started && (kind == "D"
            ? await WaitForAsync(() => Position(client) >= 5, "warmup", 30_000)
            : await WaitForAsync(() => AudioEnded() && VideoForward() >= (kind == "C" ? 90 : 60)
                && (kind == "C" || Position(client) >= 306), "warmup", 90_000));
        var before = Position(client);
        var forward = VideoForward();
        var audioStats = relay.GetStatistics("seekrun-audio");
        var edge = before + forward;
        var target = kind switch { "A" => 305, "B" => 100, "C" => 20, _ => edge - 8 };
        ready &= double.IsFinite(before) && double.IsFinite(forward)
            && (kind != "C" || before < 30) && (kind != "D" || edge < 400 && target >= 0);
        var detail = string.Create(CultureInfo.InvariantCulture,
            $"before={before:0.000000} video-forward={forward:0.000000} audio-forward={audioStats?.ForwardUpstreamOffset ?? audioStats?.ForwardBytes}/{audioStats?.Length} audio-ended={AudioEnded()} E={edge:0.000000} target={target:0.000000}");

        if (ready)
        {
            client.Command("print-text", "SEEKRUN observation-begin");
            async Task<bool> SeekAsync(double position, string phase)
            {
                var restarted = 0;
                void OnRestart(object? sender, MpvClientNotifyEventArgs args)
                {
                    if (args.Id == MpvClientEventId.PlaybackRestart) Interlocked.Exchange(ref restarted, 1);
                }

                client.DataNotify += OnRestart;
                try
                {
                    client.Command("print-text", string.Create(CultureInfo.InvariantCulture, $"SEEKRUN seek target={position:0.000000}"));
                    if (client.Command("seek", position.ToString("0.000000", CultureInfo.InvariantCulture), "absolute+exact") != MpvError.Success)
                        throw new InvalidOperationException("Seek-run native seek rejected.");
                    // The broken control can reset its timeline before its first playback restart.
                    // Do not mistake that failure to retain the target for a missing prerequisite.
                    return await WaitForAsync(() => Volatile.Read(ref restarted) != 0 && !Flag(client, "seeking")
                        && !Flag(client, "core-idle"), phase, 30_000);
                }
                finally
                {
                    client.DataNotify -= OnRestart;
                }
            }

            if (kind is "C" or "D")
            {
                ready &= await SeekAsync(kind == "C" ? 30 : 400, "forward-seek");
                if (ready && kind == "D") await SampleSeekRunAsync(client, clock, samples, "forward-play", 5);
            }

            if (ready) ready &= await SeekAsync(target, "backward-seek");
            if (ready) await SampleSeekRunAsync(client, clock, samples, "observe", kind switch { "B" => 15, "D" => 20, _ => 30 });
            client.Command("print-text", "SEEKRUN observation-end");
        }

        await File.WriteAllLinesAsync(samplesPath, SeekRunCsvHeader
            .Concat(samples.Select(sample => string.Create(CultureInfo.InvariantCulture,
                $"{sample.WallMs:0.000},{sample.Phase},{sample.Position:0.000000},{sample.AudioPts:0.000000},{sample.VideoForward:0.000000},{sample.Seeking},{sample.Idle}"))));
        await client.DisposeAsync();
        return await AnalyzeSeekRunAsync(new(pass, kind)
        {
            Prerequisites = ready, Detail = detail, Version = version, Ffmpeg = ffmpeg,
            BeforePosition = before, BeforeVideoForward = forward, AudioEnded = audioStats is { Length: > 0 } stats
                && (stats.ForwardUpstreamOffset ?? stats.ForwardBytes) >= stats.Length.Value,
            BufferedEdge = edge, Target = target, NativeLog = name + ".mpv.log", Samples = name + ".samples.csv",
        }, output, samples, videoIndex, audioIndex);
    }

    private static async Task<SeekRunRow> AnalyzeSeekRunAsync(SeekRunRow row, string output, List<SeekRunSample> samples,
        DashIndex? videoIndex = null, DashIndex? audioIndex = null)
    {
        var lines = await File.ReadAllLinesAsync(Path.Combine(output, row.NativeLog!));
        var first = Array.FindIndex(lines, line => line.Contains("SEEKRUN observation-begin", StringComparison.Ordinal));
        var last = Array.FindLastIndex(lines, line => line.Contains("SEEKRUN observation-end", StringComparison.Ordinal));
        var observed = first >= 0 && last > first ? lines[(first + 1)..last] : [];
        var invalid = observed.Count(line => line.Contains("Invalid audio PTS", StringComparison.Ordinal));
        var resets = observed.Count(line => line.Contains("Reset playback due to audio timestamp reset", StringComparison.Ordinal));
        var seeks = new Dictionary<string, List<double>> { ["video"] = [], ["audio"] = [], ["unknown"] = [] };
        // Save only index offsets, not signed media URLs, so recorded runs can be reanalyzed.
        var offsetsPath = Path.Combine(output, "pass-" + row.Pass + "-" + row.Kind + ".offsets.json");
        var offsets = videoIndex is not null && audioIndex is not null
            ? new SeekRunOffsets(videoIndex.Fragments.Select(fragment => fragment.Offset).ToArray(), audioIndex.Fragments.Select(fragment => fragment.Offset).ToArray())
            : JsonSerializer.Deserialize<SeekRunOffsets>(await File.ReadAllTextAsync(offsetsPath))
                ?? throw new InvalidDataException("Seek-run index offsets are missing.");
        await File.WriteAllTextAsync(offsetsPath, JsonSerializer.Serialize(offsets, JsonOptions));
        var videoOffsets = offsets.Video.ToHashSet();
        var audioOffsets = offsets.Audio.ToHashSet();
        foreach (var line in observed)
        {
            var seek = SeekRunStreamSeek.Match(line);
            var time = SeekRunLogTime.Match(line);
            if (!seek.Success || !time.Success) continue;
            var destination = long.Parse(seek.Groups[2].Value, CultureInfo.InvariantCulture);
            var isVideo = videoOffsets.Contains(destination);
            var isAudio = audioOffsets.Contains(destination);
            seeks[isVideo && !isAudio ? "video" : isAudio && !isVideo ? "audio" : "unknown"]
                .Add(double.Parse(time.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        double maxJump = 0, maxSampleGap = 0;
        var backwards = 0;
        SeekRunSample? previous = null;
        bool? forwardSegment = null;
        foreach (var sample in samples.Where(sample => sample.Phase is "observe" or "forward-play" or "backward-seek" or "forward-seek"))
        {
            var isForward = sample.Phase is "forward-seek" or "forward-play";
            if (forwardSegment != isForward)
            {
                forwardSegment = isForward;
                previous = null;
            }

            var expectedTarget = isForward ? row.Kind == "C" ? 30 : 400 : row.Target;
            // A synchronous command can return before time-pos moves to its requested target.
            // Exclude that one deliberate relocation, not later resets while seeking is still true.
            if (previous is null && (!double.IsFinite(sample.Position) || Math.Abs(sample.Position - expectedTarget) >= 0.5)) continue;
            if (previous is { } prior && double.IsFinite(prior.Position) && double.IsFinite(sample.Position))
            {
                var delta = sample.Position - prior.Position;
                maxJump = Math.Max(maxJump, Math.Abs(delta));
                maxSampleGap = Math.Max(maxSampleGap, sample.WallMs - prior.WallMs);
                if (delta < -0.001) backwards++;
            }

            previous = sample;
        }

        var videoPeak = SeekRunPeak(seeks["video"]);
        var audioPeak = SeekRunPeak(seeks["audio"]);
        var unknownPeak = SeekRunPeak(seeks["unknown"]);
        var commands = observed.Where(line => line.Contains("SEEKRUN seek target=", StringComparison.Ordinal)).ToArray();
        var expectedCommands = row.Kind is "C" or "D" ? 2 : 1;
        var playing = samples.Where(sample => sample.Phase is "backward-seek" or "observe" && !sample.Idle).ToArray();
        var enoughPlayback = playing.Length > 1 && playing[^1].WallMs - playing[0].WallMs + 100
            >= (row.Kind switch { "B" => 15, "D" => 20, _ => 30 }) * 1000;
        var prerequisites = row.Prerequisites && commands.Length == expectedCommands && enoughPlayback
            && commands[^1].Contains(string.Create(CultureInfo.InvariantCulture, $"target={row.Target:0.000000}"), StringComparison.Ordinal);
        return row with
        {
            Prerequisites = prerequisites, InvalidAudioPts = invalid, AudioTimestampResets = resets,
            MaxPositionJump = maxJump, UnexpectedBackwardSteps = backwards, MaxSampleGapMs = maxSampleGap,
            PeakVideoSeeks = videoPeak, PeakAudioSeeks = audioPeak, PeakUnknownSeeks = unknownPeak,
            Clean = prerequisites && invalid == 0 && backwards == 0 && maxJump <= 2 && Math.Max(Math.Max(videoPeak, audioPeak), unknownPeak) < 5,
        };
    }

    private static async Task<DashIndex> FetchSeekRunIndexAsync(SponsorStream stream)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Get, stream.Nodes[0].Url);
        foreach (var (name, value) in RequestHeaders) request.Headers.TryAddWithoutValidation(name, value);
        request.Headers.Range = new RangeHeaderValue(0, stream.IndexRange.Last);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return DashIndex.Parse(await response.Content.ReadAsByteArrayAsync(), stream.Initialization, stream.IndexRange);
    }

    private static SeekRunSample CaptureSeekRunSample(MpvClient client, Stopwatch clock, string phase)
    {
        var position = Position(client);
        var audio = ReadNumber(client, "audio-pts");
        var forward = Forward(client);
        var seeking = Flag(client, "seeking");
        var idle = Flag(client, "core-idle");
        // Timestamp after synchronous native reads, which can block while a seek is landing.
        return new(clock.Elapsed.TotalMilliseconds, phase, position, audio, forward, seeking, idle);
    }

    private static async Task SampleSeekRunAsync(MpvClient client, Stopwatch clock, List<SeekRunSample> samples, string phase, int seconds)
    {
        var until = clock.Elapsed.TotalSeconds + seconds;
        while (clock.Elapsed.TotalSeconds < until)
        {
            samples.Add(CaptureSeekRunSample(client, clock, phase));
            await Task.Delay(20);
        }
    }

    private static int SeekRunPeak(List<double> times)
    {
        var peak = 0;
        var first = 0;
        for (var last = 0; last < times.Count; last++)
        {
            while (times[last] - times[first] > 1) first++;
            peak = Math.Max(peak, last - first + 1);
        }

        return peak;
    }

    private static Task WriteSeekRunReportAsync(string path, List<SeekRunRow> rows)
    {
        var text = new StringBuilder("# seekrun 报告\n\n## 真实 libmpv 冒烟\n\n");
        text.AppendLine("样本 BV1vcsH6EEEr / cid 43620107071，qn 80 AVC，最高音频；匿名取流、经中转、TLS 验证开启，vo=null、ao=null。缓存为应用默认的前向 64 MiB / 预读 60 秒 / 回退 32 MiB；速度 1×。原生日志 all=v，位置约每 20 ms 采样。每次使用新的 libmpv 客户端与中转。");
        if (rows.FirstOrDefault(row => row.Version is not null) is { } version)
            text.AppendLine($"\n版本：{version.Version}；ffmpeg：{version.Ffmpeg}。");
        text.AppendLine("\n判据从首个原生跳转到观察结束：Invalid audio PTS 为 0，非命令跳转的观察区间位置单调、相邻采样跳变不超过 2 秒，任何一路滚动 1 秒内流内跳转少于 5 次。流内跳转按 sidx 分片目标字节偏移区分视频/音频；无法区分者单列并同样计入失败判据。显式 seek 的落点移动不算异常位置跳变。\n");
        text.AppendLine("| 情形 | 遍 | 前置条件 | Invalid audio PTS | 音频时间戳重置 | 最大位置跳变（秒） | 非预期后退次数 | 1 秒最多流内跳转（视频/音频/未知） | 结果 | 与预期一致 | 日志 / 采样 |\n|---|---:|---|---:|---:|---:|---:|---|---|---|---|");
        foreach (var row in rows)
        {
            var expected = row.Kind is "A" or "C";
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {row.Kind} | {row.Pass} | {(row.Prerequisites ? "满足" : "未满足")} | {row.InvalidAudioPts} | {row.AudioTimestampResets} | {row.MaxPositionJump:0.000000} | {row.UnexpectedBackwardSteps} | {row.PeakVideoSeeks}/{row.PeakAudioSeeks}/{row.PeakUnknownSeeks} | {(row.Prerequisites ? row.Clean ? "干净" : "出问题" : "未验证")} | {(row.Prerequisites && row.Clean == expected ? "是" : "否")} | {row.NativeLog} / {row.Samples} |"));
        }

        text.AppendLine("\n### 前置条件与观测明细\n");
        foreach (var row in rows)
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- {row.Pass}-{row.Kind}: {row.Detail}；最大采样间隔 {row.MaxSampleGapMs:0.000} ms。"));
        text.AppendLine("\n## 中转按装载划分与应用改动\n\n尚未执行；冒烟结论与预期一致后才继续。\n\n## 检查\n\n尚未执行应用侧检查。\n\n## 限制\n\n未启动、未安装应用；界面行为未实测。空视频/音频输出只验证原生时间线与取流行为。");
        return File.WriteAllTextAsync(path, text.ToString());
    }

    private sealed record SeekRunRow(int Pass, string Kind)
    {
        public bool Prerequisites { get; init; }
        public string? Detail { get; init; }
        public string? Version { get; init; }
        public string? Ffmpeg { get; init; }
        public double BeforePosition { get; init; }
        public double BeforeVideoForward { get; init; }
        public bool AudioEnded { get; init; }
        public double BufferedEdge { get; init; }
        public double Target { get; init; }
        public int InvalidAudioPts { get; init; }
        public int AudioTimestampResets { get; init; }
        public double MaxPositionJump { get; init; }
        public int UnexpectedBackwardSteps { get; init; }
        public double MaxSampleGapMs { get; init; }
        public int PeakVideoSeeks { get; init; }
        public int PeakAudioSeeks { get; init; }
        public int PeakUnknownSeeks { get; init; }
        public bool Clean { get; init; }
        public string? NativeLog { get; init; }
        public string? Samples { get; init; }
    }

    private readonly record struct SeekRunSample(double WallMs, string Phase, double Position, double AudioPts, double VideoForward, bool Seeking, bool Idle);
    private sealed record SeekRunOffsets(long[] Video, long[] Audio);
}
