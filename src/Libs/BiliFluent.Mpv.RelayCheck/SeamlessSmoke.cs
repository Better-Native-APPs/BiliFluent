// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Core.Enums;
using Richasy.MpvKernel.Dash;
using Richasy.MpvKernel.Player;
using Richasy.MpvKernel.Player.Models;
using Richasy.MpvKernel.Relay;

namespace BiliFluent.Mpv.RelayCheck;

/// <summary>Exercises the actual library orchestrator, without a window, account or audio device.</summary>
internal static partial class SeamlessSmoke
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/116.0.0.0 Safari/537.36";
    private const string Referer = "https://www.bilibili.com";
    private static readonly Dictionary<string, string> RequestHeaders = new() { ["User-Agent"] = UserAgent, ["Referer"] = Referer };
    private static readonly (int Quality, int Codec)[] VideoPlan = [(32, 7), (64, 12), (16, 7), (32, 12), (64, 7), (16, 12), (32, 7), (64, 12)];
    private static readonly int[] AudioPlan = [30216, 30232, 30280, 30216, 30232, 30280, 30216, 30232];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task RunAsync(Report report, Dictionary<string, string> options)
    {
        if (!options.TryGetValue("dll", out var dll) || !File.Exists(dll))
        {
            report.Check("seamless-smoke: dll option is required", false);
            return;
        }

        if (options.TryGetValue("tls", out var tls) && !string.Equals(tls, "on", StringComparison.OrdinalIgnoreCase))
        {
            report.Check("seamless-smoke: TLS verification cannot be disabled", false);
            return;
        }

        var passes = options.TryGetValue("passes", out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : 3;
        if (passes is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(options));
        var output = options.GetValueOrDefault("out");
        if (output is not null) Directory.CreateDirectory(output);
        report.Note("seamless-smoke: anonymous public DASH, TLS on, vo/ao null, 4 ms time-pos/audio-pts sampling; no rendered-black-frame or listening verdict");
        using var resolution = new TimerResolution();
        for (var pass = 1; pass <= passes; pass++)
        {
            try
            {
                var media = await FetchAsync(options.GetValueOrDefault("bvid", "BV1vcsH6EEEr"), options.GetValueOrDefault("cid", "43620107071"));
                if (options.ContainsKey("distance")) await RunDistancePassAsync(report, dll, media, pass, output, options);
                else await RunPassAsync(report, dll, media, pass, output, options.ContainsKey("trace"));
            }
            catch (Exception ex)
            {
                // Exception messages from HTTP/native loading can contain credentials or local paths.
                report.Check($"seamless pass {pass}: completed", false, ex.GetType().Name);
                break;
            }
        }
    }

    private static async Task RunPassAsync(Report report, string dll, Media media, int pass, string? output, bool trace)
    {
        var videos = VideoPlan
            .Select(pair => media.Video.First(stream => stream.Id == pair.Quality && stream.Codec == pair.Codec)).ToArray();
        var audios = AudioPlan
            .Select(id => media.Audio.First(stream => stream.Id == id)).ToArray();
        var initialVideo = media.Video.First(stream => stream.Id == 16 && stream.Codec == 7);
        var currentVideo = initialVideo;
        var initialAudio = media.Audio.First(stream => stream.Id == 30280);
        var logger = trace ? new SafeTraceLogger() : null;
        var client = await MpvClient.CreateAsync(dll, new() { UseConfig = false, LoadScripts = false }, logger);
        using var context = new HeadlessContext();
        var player = await context.CreateAsync(() => new MpvPlayer(client, new Resolver(initialVideo, initialAudio), logger: logger)
        {
            RelayOptions = new() { VerifyTls = true, ConnectTimeout = TimeSpan.FromSeconds(2), ResponseTimeout = TimeSpan.FromSeconds(3), ReadTimeout = TimeSpan.FromSeconds(3) },
        });
        await using var ownedPlayer = player;
        foreach (var (name, setting) in new (string, string)[]
        {
            ("vo", "null"), ("ao", "null"), ("hwdec", "no"), ("cache", "yes"),
            ("demuxer-max-bytes", "64MiB"), ("demuxer-max-back-bytes", "0"), ("demuxer-readahead-secs", "60"),
            ("cache-pause", "yes"), ("cache-pause-wait", "3"), ("keep-open", "yes"), ("tls-verify", "yes"),
        })
        {
            if (client.SetOptionString(name, setting) != MpvError.Success) throw new InvalidOperationException("Headless option rejected.");
        }

        await player.InitializeAsync();
        if (!await WaitAsync(() => Position(client) > 0.2 && Flag(client, "core-idle") == false, 20_000)) throw new InvalidOperationException("Headless playback did not begin.");
        using var sampler = new Sampler(client);
        if (logger is not null)
        {
            logger.BindClock(sampler.Now);
            player.SeamlessSwitchProgress += (_, e) => logger.RecordProgress(e);
        }
        var rows = new List<Row>();
        var baselineStart = sampler.Now;
        await Task.Delay(2000);
        var baseline = sampler.Measure(baselineStart, sampler.Now, 1);
        report.Check($"pass {pass}: 1x baseline", baseline.IsSmooth, baseline.ToString());
        rows.Add(new("baseline", 0, 0, 1, new(MpvSeamlessSwitchStatus.Switched, MpvSeamlessSwitchReason.None), baseline, 0));

        // Missing metadata/restricted formats are explicit no-op fallbacks, not hidden reloads.
        var selectedBefore = await player.GetSeamlessPlaybackStateAsync();
        var missing = await player.SwitchVideoAsync(new() { Upstreams = initialVideo.Nodes });
        var restricted = await player.SwitchAudioAsync(Target(initialAudio, restrictions: MpvSeamlessRestrictions.Lossless));
        report.Check($"pass {pass}: missing metadata/restricted format leave selection intact", missing.Status == MpvSeamlessSwitchStatus.NotSupported
            && restricted.Status == MpvSeamlessSwitchStatus.NotSupported && (await player.GetSeamlessPlaybackStateAsync()).Value.Video.TrackId == selectedBefore.Value.Video.TrackId);

        await client.PauseAsync();
        await Task.Delay(100);
        var pausedVideo = await player.SwitchVideoAsync(Target(videos[0]));
        var pausedAudio = await player.SwitchAudioAsync(Target(audios[0]));
        report.Check($"pass {pass}: paused requests are deterministic no-ops", pausedVideo is { Status: MpvSeamlessSwitchStatus.NotSupported, Reason: MpvSeamlessSwitchReason.Paused }
            && pausedAudio is { Status: MpvSeamlessSwitchStatus.NotSupported, Reason: MpvSeamlessSwitchReason.Paused });
        await client.ResumeAsync();
        await Task.Delay(300);

        for (var i = 0; i < audios.Length; i++)
        {
            var speed = i >= 6 ? 2d : 1d;
            client.SetPropertyString("speed", speed.ToString(CultureInfo.InvariantCulture));
            if (i >= 6)
            {
                var desired = i == 6 ? 305.5 : 335.5;
                client.Command(["seek", desired.ToString("0.000", CultureInfo.InvariantCulture), "absolute+exact"]);
                if (!await WaitAsync(() => Math.Abs(Position(client) - desired) < 3 && !Flag(client, "core-idle") && !Flag(client, "seeking")
                    && client.GetProperty("audio-pts", out double pts) == MpvError.Success && double.IsFinite(pts), 20_000))
                    throw new InvalidOperationException("Far audio position did not become ready.");
            }

            await Task.Delay(150);
            var start = sampler.Now;
            var result = await player.SwitchAudioAsync(Target(audios[i], badFirst: i == 3));
            await Task.Delay(1500);
            var metrics = sampler.Measure(start, sampler.Now, speed);
            rows.Add(new("audio", audios[i].Id, audios[i].Codec, speed, result, metrics, result.Preparation.TotalMilliseconds + result.BoundaryWait.TotalMilliseconds, start, sampler.Now));
            report.Check($"pass {pass}: audio {i + 1} id={audios[i].Id} speed={speed:0.0}", result.IsSuccess && metrics.IsSmooth, result.Status + "/" + result.Reason + " " + metrics);
            var state = await player.GetSeamlessPlaybackStateAsync();
            report.Check($"pass {pass}: selected audio key/memory signal {i + 1}", state.IsSuccess && state.Value.Audio is { IsExternal: true, MinimumSeekTimeSeconds: 0, RelayKey: not null, FragmentIndex: not null }
                && state.Value.Audio.TrackId == result.TrackId && state.Value.Audio.Statistics?.PreloadedBytes > 0);
            if (i == 3) report.Check($"pass {pass}: bad first audio node rotated during preparation", result.PreparationFailovers >= 1);
            if (i >= 6) report.Check($"pass {pass}: far audio {i - 5} uses bounded preload", result.IsSuccess && result.SelectionPositionSeconds > 300
                && result.AudioOpeningGapBytes > 165291 && result.AdditionalOpeningBytes == 0,
                $"gap={result.AudioOpeningGapBytes} fill={result.AdditionalOpeningBytes}");
        }

        client.SetPropertyString("speed", "1");
        await Task.Delay(150);
        // A pending, ready video is cancelled before it can select. Newer requests win globally.
        var superseded = await PendingActionAsync(player, Target(videos[0]), () => Task.CompletedTask, supersedeWith: Target(videos[1]));
        report.Check($"pass {pass}: rapid requests cancel the old ready track", superseded.First is { Status: MpvSeamlessSwitchStatus.Cancelled, Reason: MpvSeamlessSwitchReason.Superseded }
            && superseded.Second is { IsSuccess: true });
        if (superseded.Second?.IsSuccess == true) currentVideo = videos[1];
        await Task.Delay(500);

        var seeked = await PendingActionAsync(player, Target(videos[2]), () =>
        {
            client.Command(["seek", (Position(client) + 1).ToString("0.000", CultureInfo.InvariantCulture), "absolute+exact"]);
            return Task.CompletedTask;
        });
        report.Check($"pass {pass}: seek while waiting cancels without selecting target", seeked.First is { Status: MpvSeamlessSwitchStatus.Cancelled, Reason: MpvSeamlessSwitchReason.Seeked });
        if (!await WaitAsync(() => !Flag(client, "core-idle") && !Flag(client, "seeking"), 10_000)) throw new InvalidOperationException("Playback did not resume after the deliberate seek.");
        await Task.Delay(500);
        var pauseWhileWaiting = await PendingActionAsync(player, Target(videos[0]), async () => { await client.PauseAsync(); });
        report.Check($"pass {pass}: pause while waiting cancels", pauseWhileWaiting.First is { Status: MpvSeamlessSwitchStatus.Cancelled, Reason: MpvSeamlessSwitchReason.Paused });
        await client.ResumeAsync();
        await Task.Delay(300);
        var rateChanged = await PendingActionAsync(player, Target(videos[0]), () =>
        {
            client.SetPropertyString("speed", "2");
            return Task.CompletedTask;
        });
        report.Check($"pass {pass}: speed change while waiting cancels", rateChanged.First is { Status: MpvSeamlessSwitchStatus.Cancelled, Reason: MpvSeamlessSwitchReason.SpeedChanged });
        client.SetPropertyString("speed", "1");
        await Task.Delay(300);
        using (var cancellation = new CancellationTokenSource())
        {
            var calledOff = await PendingActionAsync(player, Target(videos[0]), () => { cancellation.Cancel(); return Task.CompletedTask; }, token: cancellation.Token);
            report.Check($"pass {pass}: caller cancellation while waiting", calledOff.First is { Status: MpvSeamlessSwitchStatus.Cancelled, Reason: MpvSeamlessSwitchReason.CallerCancelled });
        }

        await Task.Delay(300);
        for (var i = 0; i < videos.Length; i++)
        {
            var speed = i >= 5 ? 2d : 1d;
            client.SetPropertyString("speed", speed.ToString(CultureInfo.InvariantCulture));
            await Task.Delay(150);
            var start = sampler.Now;
            var result = await player.SwitchVideoAsync(Target(videos[i], badFirst: i == 4));
            if (result.IsSuccess) currentVideo = videos[i];
            await Task.Delay(1500);
            var metrics = sampler.Measure(start, sampler.Now, speed);
            rows.Add(new("video", videos[i].Id, videos[i].Codec, speed, result, metrics, result.Preparation.TotalMilliseconds + result.BoundaryWait.TotalMilliseconds, start, sampler.Now));
            report.Check($"pass {pass}: video {i + 1} qn={videos[i].Id} codec={videos[i].Codec} speed={speed:0.0}", result.IsSuccess && metrics.IsSmooth, result.Status + "/" + result.Reason + " " + metrics);
            var state = await player.GetSeamlessPlaybackStateAsync();
            report.Check($"pass {pass}: external video/earliest time/map signal {i + 1}", state.IsSuccess && state.Value.Video is { IsExternal: true, OffsetMap: not null, FragmentIndex: not null, RelayKey: not null }
                && state.Value.Video.TrackId == result.TrackId && state.Value.Video.MinimumSeekTimeSeconds == result.SwitchTimeSeconds);
            if (i == 4) report.Check($"pass {pass}: bad first video node rotated during preparation", result.PreparationFailovers >= 1);
        }

        var beforeNode = await player.GetSeamlessPlaybackStateAsync();
        var selectedVideo = currentVideo;
        var nodeStart = sampler.Now;
        report.Check($"pass {pass}: selected virtual node switch", selectedVideo.Nodes.Count > 1 && player.TrySwitchSelectedRelayNode(MpvRelayStreamKind.Video, selectedVideo.Nodes[1].Url));
        var refreshed = await player.TryUpdateSelectedRelayUrlsAsync(MpvRelayStreamKind.Video, new(selectedVideo.Nodes), RequestHeaders);
        await Task.Delay(1500);
        var afterNode = await player.GetSeamlessPlaybackStateAsync();
        var nodeMetrics = sampler.Measure(nodeStart, sampler.Now, 2);
        report.Check($"pass {pass}: selected virtual URL table keeps ID and timeline", refreshed == RelayUpdateResult.Updated && afterNode.Value.Video.TrackId == beforeNode.Value.Video.TrackId
            && afterNode.Value.Video.OffsetMap?.UpstreamLength == beforeNode.Value.Video.OffsetMap?.UpstreamLength && nodeMetrics.IsSmooth, nodeMetrics.ToString());

        await Task.Delay(4000);
        var endTracks = await client.GetMediaTracksAsync();
        report.Check($"pass {pass}: old external tracks retired; main video retained", endTracks.IsSuccess
            && endTracks.Value.Count(track => track.Type == MpvTrackType.Video && track.IsExternal) == 1
            && endTracks.Value.Count(track => track.Type == MpvTrackType.Audio) == 1
            && endTracks.Value.Any(track => track.Type == MpvTrackType.Video && !track.IsExternal));
        var switchedAudio = rows.Count(row => row.Kind == "audio" && row.Result.IsSuccess);
        var switchedVideo = rows.Count(row => row.Kind == "video" && row.Result.IsSuccess);
        report.Check($"pass {pass}: all required audio/video selections completed", switchedAudio == 8 && switchedVideo == 8);
        report.Note($"pass {pass}: attempted audio=8 video=8; switched audio={switchedAudio} video={switchedVideo} (+supersession), qualities=360/480/720 codecs=AVC/HEVC; paused/seek/rate/caller cancellation and bad-first nodes exercised");
        if (output is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(output, $"pass-{pass}.json"), JsonSerializer.Serialize(rows, JsonOptions));
            await sampler.WriteAsync(Path.Combine(output, $"pass-{pass}.samples.csv"));
            if (logger is not null) await logger.WriteAsync(Path.Combine(output, $"pass-{pass}.io.json"));
        }
    }

    private static async Task<(MpvSeamlessSwitchResult First, MpvSeamlessSwitchResult? Second)> PendingActionAsync(MpvPlayer player, MpvSeamlessTarget target,
        Func<Task> action, MpvSeamlessTarget? supersedeWith = null, CancellationToken token = default)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnProgress(object? sender, MpvSeamlessSwitchEventArgs e)
        {
            if (e.Phase == MpvSeamlessSwitchPhase.Waiting) ready.TrySetResult();
        }

        player.SeamlessSwitchProgress += OnProgress;
        try
        {
            var first = player.SwitchVideoAsync(target, token);
            var reached = await Task.WhenAny(ready.Task, first);
            if (reached == first) return (await first, null);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await action();
            var second = supersedeWith is null ? null : player.SwitchVideoAsync(supersedeWith);
            return (await first, second is null ? null : await second);
        }
        finally
        {
            player.SeamlessSwitchProgress -= OnProgress;
        }
    }

    private static double Position(MpvClient client) => client.GetProperty("time-pos", out double value) == MpvError.Success ? value : double.NaN;

    private static bool Flag(MpvClient client, string name) => client.GetProperty(name, out bool value) == MpvError.Success && value;

    private static async Task<bool> WaitAsync(Func<bool> condition, int timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeout) return false;
            await Task.Delay(10);
        }

        return true;
    }

    private static MpvSeamlessTarget Target(DashStream stream, bool badFirst = false, MpvSeamlessRestrictions restrictions = MpvSeamlessRestrictions.None)
        => new()
        {
            Upstreams = badFirst ? [new("https://127.0.0.1:9/unreachable", "bad"), .. stream.Nodes] : stream.Nodes,
            Headers = RequestHeaders, Initialization = stream.Initialization, IndexRange = stream.IndexRange,
            Title = $"BiliFluent q{stream.Id} c{stream.Codec}", Restrictions = restrictions,
        };

    private static async Task<Media> FetchAsync(string bvid, string cid)
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

        static List<DashStream> Read(JsonElement list)
            => [.. list.EnumerateArray().Select(item =>
            {
                var urls = new List<string> { item.GetProperty("base_url").GetString()! };
                if (item.TryGetProperty("backup_url", out var backup) && backup.ValueKind == JsonValueKind.Array)
                    urls.AddRange(backup.EnumerateArray().Select(url => url.GetString()!));
                var segment = item.GetProperty("segment_base");
                if (!DashByteRange.TryParse(segment.GetProperty("initialization").GetString(), out var initialization)
                    || !DashByteRange.TryParse(segment.GetProperty("index_range").GetString(), out var index))
                    throw new InvalidDataException("Anonymous stream has no usable segment_base.");
                return new DashStream(item.GetProperty("id").GetInt32(), item.GetProperty("codecid").GetInt32(),
                    [.. urls.Distinct().Select((url, i) => new RelayUpstream(url, "node" + i))], initialization, index);
            })];
    }

    private sealed record Media(List<DashStream> Video, List<DashStream> Audio);
    private sealed record DashStream(int Id, int Codec, List<RelayUpstream> Nodes, DashByteRange Initialization, DashByteRange IndexRange);
    private sealed record Row(string Kind, int Quality, int Codec, double Speed, MpvSeamlessSwitchResult Result, Metrics Metrics, double LatencyMs, double StartMs = 0, double EndMs = 0);

    private sealed class Resolver(DashStream video, DashStream audio) : IMpvMediaSourceResolver
    {
        public IMpvMediaSourceResolver Clone() => new Resolver(video, audio);

        public Task<MpvMediaSource> GetSourceAsync()
            => Task.FromResult(new MpvMediaSource(video.Nodes[0].Url, title: "BiliFluent anonymous seamless check", options: new()
            {
                EnableCookies = false, EnableYtdl = false, AudioTracks = [audio.Nodes[0].Url],
            })
            {
                Relay = new() { Video = new(video.Nodes), Audio = new(audio.Nodes), Headers = RequestHeaders },
            });
    }

    private sealed class HeadlessContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;

        public HeadlessContext()
        {
            _thread = new(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
            }) { IsBackground = true, Name = "seamless-check-context" };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            if (!_queue.IsAddingCompleted) _queue.Add((d, state));
        }

        public Task<MpvPlayer> CreateAsync(Func<MpvPlayer> create)
        {
            var result = new TaskCompletionSource<MpvPlayer>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ =>
            {
                try { result.SetResult(create()); }
                catch (Exception ex) { result.SetException(ex); }
            }, null);
            return result.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join();
            _queue.Dispose();
        }
    }

    private sealed class TimerResolution : IDisposable
    {
        private readonly bool _active = OperatingSystem.IsWindows() && timeBeginPeriod(1) == 0;
        public void Dispose() { if (_active) timeEndPeriod(1); }
    }

    [LibraryImport("winmm.dll")]
    private static partial uint timeBeginPeriod(uint period);

    [LibraryImport("winmm.dll")]
    private static partial uint timeEndPeriod(uint period);
}
