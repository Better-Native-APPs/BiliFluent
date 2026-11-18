// Copyright (c) Bili Copilot. All rights reserved.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Core.Enums;
using Richasy.MpvKernel.Core.Models;
using Richasy.MpvKernel.Dash;
using Richasy.MpvKernel.Relay;

namespace BiliFluent.Mpv.RelayCheck;

/// <summary>Measures muted WASAPI recovery after paused audio-output reconstruction.</summary>
internal static partial class AudioResumeSmoke
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/116.0.0.0 Safari/537.36";
    private static readonly Dictionary<string, string> Headers = new() { ["User-Agent"] = UserAgent, ["Referer"] = "https://www.bilibili.com" };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Variant[] Variants = [new("A", 3, 0), new("B", 3, 1), new("C", 9, 3), new("D", 120, 0), new("E", 120, 1), new("F", 120, 1, true)];

    public static async Task RunAsync(Report report, Dictionary<string, string> options)
    {
        if (!options.TryGetValue("dll", out var dll) || !File.Exists(dll))
        {
            report.Check("audio-resume: dll option is required", false);
            return;
        }

        var passes = int.Parse(options.GetValueOrDefault("passes", "3"), CultureInfo.InvariantCulture);
        if (passes is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(options));
        var firstPass = int.Parse(options.GetValueOrDefault("first-pass", "1"), CultureInfo.InvariantCulture);
        if (firstPass < 1) throw new ArgumentOutOfRangeException(nameof(options));
        var speeds = options.GetValueOrDefault("speeds", "1,2").Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        if (speeds.Any(speed => speed is not (1 or 2))) throw new ArgumentOutOfRangeException(nameof(options));
        var reloadCounts = options.GetValueOrDefault("reloads", "1,3,8,30").Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        if (reloadCounts.Any(count => count < 1)) throw new ArgumentOutOfRangeException(nameof(options));
        var variants = options.GetValueOrDefault("variants", "A,B,C,D,E,F").Split(',')
            .SelectMany(name => name == "C" ? reloadCounts.Select(count => new Variant("C" + count, checked(count * 3), count)) : [Variants.Single(variant => variant.Name == name)])
            .ToArray();
        var baseline = options.GetValueOrDefault("baseline") == "1";
        RunRecoveryStateChecks(report);
        var output = options.GetValueOrDefault("out", Path.Combine(Path.GetTempPath(), "bf-audioresume"));
        Directory.CreateDirectory(output);
        report.Note("audio-resume: anonymous dual DASH through the relay; WASAPI, mute=yes throughout, vo=null; 20 ms samples for 30 s; baseline=" + baseline);
        using var resolution = new TimerResolution();
        var rows = new List<Row>();
        for (var pass = firstPass; pass < checked(firstPass + passes); pass++)
        {
            foreach (var variant in variants)
            {
                // Each variant can include two long pauses; do not reuse expiring signed URLs
                // across the entire matrix. No account state is used for their renewal.
                Media media;
                try { media = await FetchAsync(options.GetValueOrDefault("bvid", "BV1vcsH6EEEr"), options.GetValueOrDefault("cid", "43620107071"), options.GetValueOrDefault("video", "lowest")); }
                catch (Exception ex)
                {
                    report.Check($"pass {pass} {variant.Name}: anonymous stream resolution", false, ex.GetType().Name);
                    continue;
                }

                foreach (var speed in speeds)
                {
                    try
                    {
                        var row = await RunCaseAsync(report, dll, media, variant, speed, pass, baseline, output, options);
                        rows.Add(row);
                        await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(rows, JsonOptions));
                    }
                    catch (Exception ex)
                    {
                        // Native/HTTP exception text can contain private paths or signed stream URLs.
                        report.Check($"pass {pass} {variant.Name} {speed}x: completed", false, ex.GetType().Name);
                    }
                }
            }
        }
    }

    private static async Task<Row> RunCaseAsync(Report report, string dll, Media media, Variant variant, int speed, int pass, bool baseline, string output, Dictionary<string, string> options)
    {
        var name = $"pass-{pass}-{variant.Name}-{speed}x";
        var startedUtc = DateTimeOffset.UtcNow;
        bool? exclusive = options.TryGetValue("exclusive", out var exclusivity) ? exclusivity == "1" : null;
        report.Note(name + ": begin");
        using var trace = new TraceLogger(Path.Combine(output, name + ".events.log"));
        await using var relay = LoopbackMediaRelay.Start(logger: trace);
        var videoUrl = relay.Register("video", new() { Upstreams = media.Video.Urls.Select((url, i) => new RelayUpstream(url, "v" + i)).ToArray(), Headers = Headers });
        var audioNodes = media.Audio.Urls.Select((url, i) => new RelayUpstream(url, "a" + i)).ToArray();
        await using var delayed = variant.StallAudio ? await DelayedAudioNode.CreateAsync(
            relay.Register("audio-source", new() { Upstreams = audioNodes, Headers = Headers }), media.Audio, speed) : null;
        var audioUrl = relay.Register("audio", new() { Upstreams = delayed is not null ? [new(delayed.Url, "delayed-audio"), .. audioNodes] : audioNodes, Headers = Headers });
        var videoIndex = options.GetValueOrDefault("index") == "1" ? await FetchIndexAsync(videoUrl, media.Video) : null;
        await using var client = await MpvClient.CreateAsync(dll, new() { UseConfig = false, LoadScripts = false }, trace);
        client.EnablePausedAudioOutputRecovery = !baseline && options.GetValueOrDefault("policy", "library") == "library";
        MpvCacheStateEventArgs? cacheState = null;
        client.CacheStateChanged += (_, state) => Volatile.Write(ref cacheState, state);
        var clock = new Stopwatch();
        var measuring = 0;
        var cacheAfter = new TaskCompletionSource<RecoveryCacheObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DataNotify += (_, args) =>
        {
            if (args.Id == MpvClientEventId.PlaybackRestart && Volatile.Read(ref measuring) != 0)
                cacheAfter.TrySetResult(CaptureRecoveryCache(client, relay, clock.Elapsed.TotalMilliseconds));
        };
        var nativeLog = Path.Combine(output, name + ".mpv.log");
        foreach (var (key, value) in new (string, string)[]
        {
            ("vo", "null"), ("ao", "wasapi"), ("mute", "yes"), ("volume", "0"), ("audio-exclusive", "no"),
            ("hwdec", "no"), ("ytdl", "no"), ("cookies", "no"), ("terminal", "no"), ("idle", "yes"),
            ("cache", "yes"), ("demuxer-max-bytes", options.GetValueOrDefault("cachebytes", "64MiB")),
            ("demuxer-max-back-bytes", options.GetValueOrDefault("backbytes", "0")), ("demuxer-readahead-secs", options.GetValueOrDefault("readahead", "60")),
            ("cache-pause", "yes"), ("cache-pause-wait", "3"), ("keep-open", "yes"), ("tls-verify", "yes"),
            ("log-file", nativeLog),
            ("msg-level", "all=warn,cplayer=v,ao=v,ao/wasapi=v,demux=v,ad=v,af=v,ffmpeg/audio=v"),
        })
        {
            Ensure(client.SetOptionString(key, value), key);
        }

        var audioBuffer = ReadDouble(client, "options/audio-buffer");
        await using var correction = new CorrectionProbe(client, relay, trace, options, audioBuffer, Path.Combine(output, name));
        report.Note(string.Create(CultureInfo.InvariantCulture, $"{name}: video-id={media.Video.Id} codec={media.Video.Codec} {media.Video.Width}x{media.Video.Height} bitrate={media.Video.Bandwidth} backbytes={options.GetValueOrDefault("backbytes", "0")}"));
        var requestedTimelineStarted = 1;
        client.SeekStarted += (_, _) => Interlocked.Exchange(ref requestedTimelineStarted, 1);
        client.ReachFileLoaded += (_, _) => Interlocked.Exchange(ref requestedTimelineStarted, 1);
        Ensure(await client.SetSpeedAsync(speed), "speed");
        var pauseTarget = PauseTarget(options, variant, pass);
        await client.PlayAsync(videoUrl, new()
        {
            AudioTracks = [audioUrl], EnableCookies = false, EnableYtdl = false,
            StartPosition = options.TryGetValue("start", out var start) ? double.Parse(start, CultureInfo.InvariantCulture)
                : pauseTarget is { } target ? target - 10 * speed : null,
        });
        if (!await WaitAsync(() => ReadDouble(client, "time-pos") > 0.2 && double.IsFinite(ReadDouble(client, "audio-pts")), 30_000))
            throw new InvalidOperationException("Muted playback did not start.");
        report.Check(name + ": real WASAPI is muted", client.GetPropertyString("current-ao") == "wasapi" && Flag(client, "mute"));
        await Task.Delay(10_000);
        var cacheGoal = double.Parse(options.GetValueOrDefault("cache-wait", "0"), CultureInfo.InvariantCulture);
        var cacheWait = Stopwatch.StartNew();
        if (cacheGoal > 0)
            await WaitAsync(() => ReadDouble(client, "demuxer-cache-duration") >= cacheGoal, 30_000);
        cacheWait.Stop();
        var cacheBeforePause = ReadDouble(client, "demuxer-cache-duration");
        if (cacheGoal > 0) trace.Mark(string.Create(CultureInfo.InvariantCulture, $"cache-before-pause secs={cacheBeforePause:0.000000} goal={cacheGoal:0.000000}"), client);
        Ensure(await client.PauseAsync(), "pause");
        trace.Mark("paused", client);
        correction.BeginPause();
        var paused = Stopwatch.StartNew();
        for (var reload = 0; reload < variant.Reloads; reload++)
        {
            await Task.Delay(3000);
            trace.Mark("ao-reload " + (reload + 1), client);
            correction.NoteControlledReload();
            Ensure(client.Command("ao-reload"), "ao-reload");
            Ensure(client.SetPropertyString("mute", "yes"), "mute");
        }

        var remaining = variant.PauseSeconds * 1000 - paused.ElapsedMilliseconds;
        if (remaining > 0) await Task.Delay((int)remaining);
        var rushMs = int.Parse(options.GetValueOrDefault("rush-ms", "0"), CultureInfo.InvariantCulture);
        if (rushMs > 0) await Task.Delay(rushMs);
        else if (options.GetValueOrDefault("settle") == "1") await correction.WaitUntilSettledAsync();
        trace.Mark("resume-request", client);
        delayed?.StallFor(TimeSpan.FromSeconds(5));
        var position = ReadDouble(client, "time-pos");
        var videoFootprint = videoIndex is not null ? CaptureFootprint(videoIndex, position, Volatile.Read(ref cacheState)) : null;
        var cacheBeforeCorrection = ReadDouble(client, "demuxer-cache-duration");
        var previousAudio = ReadDouble(client, "audio-pts");
        var resumeVideoRequests = relay.GetStatistics("video")?.Requests ?? 0;
        var resumeAudioRequests = relay.GetStatistics("audio")?.Requests ?? 0;
        if (options.ContainsKey("seek")) Interlocked.Exchange(ref requestedTimelineStarted, 0);
        clock.Start();
        Volatile.Write(ref measuring, 1);
        correction.BeginResume();
        var resume = ResumeAsync(client, baseline, options, videoUrl, audioUrl, correction);
        var samples = new List<Sample>(1501);
        double? firstAudioMs = null;
        double? firstFiniteAudioMs = null;
        double? firstVideoMs = null;
        int? startupVideoRequests = null;
        int? startupAudioRequests = null;
        var advancedBeforeAudio = 0d;
        var videoWithoutAudio = 0d;
        var maxVideoWithoutAudio = 0d;
        var videoWithMissingAudioPts = 0d;
        var maxVideoWithMissingAudioPts = 0d;
        double? missingPtsVideoStartMs = null;
        var missingPtsVideoEndMs = 0d;
        var maxMissingPtsWallMs = 0d;
        var previousPosition = position;
        var lastAudioChange = 0d;
        var longestAudioStall = 0d;
        var lastSampleMs = 0d;
        var maxSampleGapMs = 0d;
        var nextSampleMs = 0d;
        var muted = true;
        while (clock.ElapsedMilliseconds < 30_000)
        {
            var time = ReadDouble(client, "time-pos");
            var audio = ReadDouble(client, "audio-pts");
            var samplePaused = Flag(client, "pause");
            var sampleIdle = Flag(client, "core-idle");
            var sampleCached = Flag(client, "paused-for-cache");
            var sampleSeeking = Flag(client, "seeking");
            var sampleMuted = Flag(client, "mute");
            var sampleAo = client.GetPropertyString("current-ao");
            // Native property reads can block during a seek. Timestamp after the reads so
            // that a blocked core cannot accidentally turn a five-second wait into 0 ms.
            var elapsed = clock.Elapsed.TotalMilliseconds;
            maxSampleGapMs = Math.Max(maxSampleGapMs, elapsed - lastSampleMs);
            lastSampleMs = elapsed;
            var advancing = Volatile.Read(ref requestedTimelineStarted) != 0
                && !samplePaused && double.IsFinite(audio) && double.IsFinite(previousAudio) && audio > previousAudio + 0.0001;
            if (double.IsFinite(audio)) firstFiniteAudioMs ??= elapsed;
            if (Volatile.Read(ref requestedTimelineStarted) != 0 && !samplePaused && !sampleSeeking && !sampleIdle
                && double.IsFinite(time) && double.IsFinite(previousPosition) && time > previousPosition + 0.0001)
                firstVideoMs ??= elapsed;
            if (advancing)
            {
                videoWithoutAudio = 0;
                firstAudioMs ??= elapsed;
                longestAudioStall = Math.Max(longestAudioStall, elapsed - lastAudioChange);
                lastAudioChange = elapsed;
            }

            if (firstAudioMs is not null && firstVideoMs is not null && startupVideoRequests is null)
            {
                startupVideoRequests = (relay.GetStatistics("video")?.Requests ?? 0) - resumeVideoRequests;
                startupAudioRequests = (relay.GetStatistics("audio")?.Requests ?? 0) - resumeAudioRequests;
            }

            if (firstFiniteAudioMs is not null && firstVideoMs is not null && !cacheAfter.Task.IsCompleted)
                cacheAfter.TrySetResult(CaptureRecoveryCache(client, relay, elapsed));

            if (firstAudioMs is null && !samplePaused && !sampleSeeking && double.IsFinite(time) && double.IsFinite(previousPosition))
                advancedBeforeAudio += Math.Max(0, time - previousPosition);
            if (!advancing && !samplePaused && !sampleSeeking && double.IsFinite(time) && double.IsFinite(previousPosition))
            {
                videoWithoutAudio += Math.Max(0, time - previousPosition);
                maxVideoWithoutAudio = Math.Max(maxVideoWithoutAudio, videoWithoutAudio);
            }
            var visibleProgress = !samplePaused && !sampleSeeking && !sampleIdle && double.IsFinite(time)
                && double.IsFinite(previousPosition) && time > previousPosition + 0.0001;
            if (!double.IsFinite(audio) && visibleProgress)
            {
                missingPtsVideoStartMs ??= elapsed;
                missingPtsVideoEndMs = elapsed;
                videoWithMissingAudioPts += time - previousPosition;
                maxVideoWithMissingAudioPts = Math.Max(maxVideoWithMissingAudioPts, videoWithMissingAudioPts);
                maxMissingPtsWallMs = Math.Max(maxMissingPtsWallMs, missingPtsVideoEndMs - missingPtsVideoStartMs.Value + 20);
            }
            else if (double.IsFinite(audio) || samplePaused || sampleSeeking || sampleIdle)
            {
                missingPtsVideoStartMs = null;
                videoWithMissingAudioPts = 0;
            }
            muted &= sampleMuted;
            samples.Add(new(elapsed, time, audio, advancing, samplePaused, sampleIdle, sampleCached, sampleSeeking, sampleMuted, sampleAo));
            previousAudio = audio;
            previousPosition = time;
            nextSampleMs = Math.Max(nextSampleMs + 20, elapsed);
            var delay = nextSampleMs - clock.Elapsed.TotalMilliseconds;
            if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay));
        }

        longestAudioStall = Math.Max(longestAudioStall, clock.Elapsed.TotalMilliseconds - lastAudioChange);
        await resume.WaitAsync(TimeSpan.FromSeconds(10));
        trace.Mark("measured", client);
        await WriteSamplesAsync(Path.Combine(output, name + ".samples.csv"), samples);
        report.Check(name + ": mute remained enabled and WASAPI recovered", muted && client.GetPropertyString("current-ao") == "wasapi");
        report.Check(name + ": audio advances within 30 s", firstAudioMs is not null);
        report.Check(name + ": video advances within 30 s", firstVideoMs is not null);
        if (delayed is not null) report.Check(name + ": five-second audio-body stall was reached", delayed.StalledBodies > 0, "bodies=" + delayed.StalledBodies);
        var videoOnlyMs = firstAudioMs is not null && firstVideoMs is not null ? Math.Max(0, firstAudioMs.Value - firstVideoMs.Value) : (double?)null;
        var resumeStallMs = firstAudioMs is not null && firstVideoMs is not null ? Math.Max(firstAudioMs.Value, firstVideoMs.Value) : (double?)null;
        if (!baseline) report.Check(name + ": video does not advance with unavailable audio PTS beyond one 20 ms sample", maxMissingPtsWallMs <= 20,
            string.Create(CultureInfo.InvariantCulture, $"missing-pts-video-ms={maxMissingPtsWallMs:0.000} positive-confirmation-video-gap-ms={videoOnlyMs:0.000}"));
        var audioStats = relay.GetStatistics("audio");
        var videoStats = relay.GetStatistics("video");
        await correction.DisposeAsync();
        await client.DisposeAsync();
        var log = await File.ReadAllTextAsync(nativeLog);
        var invalidPts = log.Split('\n').Count(line => line.Contains("Invalid audio PTS:", StringComparison.Ordinal));
        var timestampResets = log.Split('\n').Count(line => line.Contains("Reset playback due to audio timestamp reset", StringComparison.Ordinal));
        var logLines = log.Split('\n');
        var resumeLine = Array.FindIndex(logLines, line => line.Contains("AUDIORESUME resume-request", StringComparison.Ordinal));
        var cacheSeekLines = logLines.Select((line, index) => (line, index)).Where(item => item.index > resumeLine
            && item.line.Contains("in-cache seek", StringComparison.Ordinal)).Select(item => item.index + 1).ToArray();
        var afterCorrection = cacheAfter.Task.IsCompletedSuccessfully ? cacheAfter.Task.Result : null;
        report.Check(name + ": no decoder PTS discontinuity", invalidPts == 0 && timestampResets == 0, $"invalid-pts={invalidPts} resets={timestampResets}");
        report.Check(name + ": paused correction preserves position and current frame", correction.Records.All(record => record.ResumedDuringCorrection
            || (record.Completed && record.PausedAtStart && record.PausedAtEnd && Math.Abs(record.PositionDelta ?? double.PositiveInfinity) <= 0.0001
                && record.SameFrame == true)));
        var row = new Row(pass, variant.Name, speed, baseline, firstAudioMs, advancedBeforeAudio, longestAudioStall,
            paused.Elapsed.TotalSeconds - clock.Elapsed.TotalSeconds, trace.AudioReconfigs, delayed?.StalledBodies ?? 0, audioStats?.Requests ?? 0, audioStats?.Failovers ?? 0, muted, exclusive, startedUtc, samples.Count, maxSampleGapMs,
            firstVideoMs, correction.Policy, correction.Threshold, rushMs, double.IsFinite(audioBuffer) ? audioBuffer : null, invalidPts, timestampResets, correction.Records.ToArray(),
            media.Video.Id, media.Video.Codec, media.Video.Width, media.Video.Height, media.Video.Bandwidth, options.GetValueOrDefault("backbytes", "0"), videoStats?.Requests ?? 0, videoStats?.Failovers ?? 0,
            startupVideoRequests, startupAudioRequests, videoOnlyMs, resumeStallMs, 20, maxVideoWithoutAudio / speed * 1000, maxVideoWithoutAudio, videoFootprint,
            firstFiniteAudioMs, firstFiniteAudioMs is not null && firstVideoMs is not null ? Math.Max(firstFiniteAudioMs.Value, firstVideoMs.Value) : null,
            maxMissingPtsWallMs, maxVideoWithMissingAudioPts / speed * 1000, Finite(cacheBeforePause), Finite(cacheBeforeCorrection), afterCorrection?.ForwardSeconds,
            afterCorrection?.VideoRequests - resumeVideoRequests, afterCorrection?.AudioRequests - resumeAudioRequests, cacheSeekLines,
            cacheGoal, cacheWait.Elapsed.TotalMilliseconds, cacheBeforePause >= cacheGoal, videoFootprint is not null ? Math.Max(0, position - videoFootprint.FragmentStart) : null);
        report.Note(string.Create(CultureInfo.InvariantCulture, $"MEASURE pass={pass} variant={variant.Name} speed={speed} baseline={baseline} policy={correction.Policy} silent-ms={firstAudioMs:0.0} video-start-ms={firstVideoMs:0.0} video-only-ms={videoOnlyMs:0.0} resume-stall-ms={resumeStallMs:0.0} video-before-audio-secs={advancedBeforeAudio:0.000} longest-audio-stall-ms={longestAudioStall:0.0} reconfigs={trace.AudioReconfigs} stalled-bodies={delayed?.StalledBodies ?? 0} invalid-pts={invalidPts} resets={timestampResets}"));
        return row;
    }

    private static Task ResumeAsync(MpvClient client, bool baseline, Dictionary<string, string> options, string videoUrl, string audioUrl, CorrectionProbe correction)
        => Task.Run(async () =>
        {
            // Optional native seeks are diagnostic controls, not a replacement for the library fix.
            if (options.TryGetValue("seek", out var target))
            {
                if (options.GetValueOrDefault("seekmode") == "reload")
                    await client.PlayAsync(videoUrl, new() { AudioTracks = [audioUrl], EnableCookies = false, EnableYtdl = false, StartPosition = double.Parse(target, CultureInfo.InvariantCulture) });
                else Ensure(client.Command("seek", target, "absolute+exact"), "diagnostic seek");
            }
            else if (options.GetValueOrDefault("realign") == "1") Ensure(client.Command("seek", "0", "relative+exact"), "diagnostic realignment");
            else await correction.RealignAtResumeAsync();
            if (baseline) Ensure(client.SetPropertyString("pause", "no"), "resume");
            else Ensure(await client.ResumeAsync(), "resume");
        });

    private static void Ensure(MpvError result, string operation)
    {
        if (result != MpvError.Success) throw new InvalidOperationException($"Native {operation} failed: {result}.");
    }

    private static void Ensure(MpvResult result, string operation)
    {
        if (result.IsFailed) throw new InvalidOperationException($"Player {operation} failed.");
    }

    private static double ReadDouble(MpvClient client, string name) => client.GetProperty(name, out double value) == MpvError.Success ? value : double.NaN;

    private static bool Flag(MpvClient client, string name) => client.GetProperty(name, out bool value) == MpvError.Success && value;

    private static async Task<bool> WaitAsync(Func<bool> condition, int timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds >= timeout) return false;
            await Task.Delay(20);
        }

        return true;
    }

    private static async Task WriteSamplesAsync(string path, List<Sample> samples)
    {
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        await writer.WriteLineAsync("elapsed-ms,time-pos,audio-pts,audio-advancing,pause,core-idle,paused-for-cache,seeking,mute,current-ao");
        foreach (var sample in samples)
            await writer.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{sample.Milliseconds:0.000},{sample.Position:0.000000},{sample.AudioPts:0.000000},{sample.AudioAdvancing},{sample.Paused},{sample.Idle},{sample.Cached},{sample.Seeking},{sample.Muted},{sample.Ao}"));
    }

    private static async Task<Media> FetchAsync(string bvid, string cid, string videoChoice)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.bilibili.com/x/player/playurl?bvid={Uri.EscapeDataString(bvid)}&cid={Uri.EscapeDataString(cid)}&fnval=4048&fourk=1&qn=127&try_look=1");
        foreach (var (name, value) in Headers) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (document.RootElement.GetProperty("code").GetInt32() != 0) throw new InvalidOperationException("Anonymous playurl failed.");
        var dash = document.RootElement.GetProperty("data").GetProperty("dash");
        var video = Read(dash.GetProperty("video")).ToArray();
        var selected = videoChoice switch
        {
            "highest" => video.OrderByDescending(stream => stream.Bandwidth).First(),
            "highest-avc" => video.Where(stream => stream.Codec == 7).OrderByDescending(stream => stream.Bandwidth).First(),
            "1080" => video.Where(stream => stream.Height == 1080 && stream.Codec == 7).OrderByDescending(stream => stream.Bandwidth).First(),
            "lowest" => video.Where(stream => stream.Codec == 7).OrderBy(stream => stream.Id).First(),
            _ => video.Where(stream => stream.Id == int.Parse(videoChoice, CultureInfo.InvariantCulture)).OrderByDescending(stream => stream.Bandwidth).First(),
        };
        return new(selected, Read(dash.GetProperty("audio")).OrderByDescending(stream => stream.Id).First());

        static IEnumerable<DashStream> Read(JsonElement list) => list.EnumerateArray().Select(item =>
        {
            var urls = new List<string> { item.GetProperty("base_url").GetString()! };
            if (item.TryGetProperty("backup_url", out var backup) && backup.ValueKind == JsonValueKind.Array)
                urls.AddRange(backup.EnumerateArray().Select(url => url.GetString()!));
            var segment = item.GetProperty("segment_base");
            if (!DashByteRange.TryParse(segment.GetProperty("initialization").GetString(), out var initialization)
                || !DashByteRange.TryParse(segment.GetProperty("index_range").GetString(), out var index)) throw new InvalidDataException("Anonymous stream has no DASH index.");
            return new DashStream(item.GetProperty("id").GetInt32(), item.GetProperty("codecid").GetInt32(), [.. urls.Distinct()], initialization, index,
                item.TryGetProperty("width", out var width) ? width.GetInt32() : 0, item.TryGetProperty("height", out var height) ? height.GetInt32() : 0,
                item.GetProperty("bandwidth").GetInt64());
        });
    }

    private static async Task<DashIndex> FetchIndexAsync(string url, DashStream stream)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new(0, stream.IndexRange.Last);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return DashIndex.Parse(bytes, stream.Initialization, stream.IndexRange);
    }

    private static double? PauseTarget(Dictionary<string, string> options, Variant variant, int pass)
    {
        if (variant.StallAudio) return null;
        if (options.TryGetValue("pause-positions", out var positions))
        {
            var values = positions.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            return values[(pass - 1) % values.Length];
        }
        return options.TryGetValue("pausepos", out var position) ? double.Parse(position, CultureInfo.InvariantCulture) : null;
    }

    private static RecoveryCacheObservation CaptureRecoveryCache(MpvClient client, LoopbackMediaRelay relay, double milliseconds)
        => new(milliseconds, Finite(ReadDouble(client, "demuxer-cache-duration")), relay.GetStatistics("video")?.Requests ?? 0,
            relay.GetStatistics("audio")?.Requests ?? 0);

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;

    private static VideoCacheFootprint CaptureFootprint(DashIndex index, double position, MpvCacheStateEventArgs? state)
    {
        var fragment = index.Fragments[index.FragmentAt(position)];
        var ranges = state?.SeekableRanges.Select(range => new CacheRange(range.Start, range.End)).ToArray() ?? [];
        return new(position, fragment.Time, fragment.Duration, fragment.Size, fragment.StartsWithKeyFrame,
            index.Fragments.Max(item => item.Size), index.Fragments.Max(item => item.Duration), index.Fragments.All(item => item.StartsWithKeyFrame),
            ranges, ranges.Any(range => range.Start <= position && range.End >= position), state?.FwBytes, state?.RawInputRate);
    }

    private sealed record Variant(string Name, int PauseSeconds, int Reloads, bool StallAudio = false);
    private sealed record DashStream(int Id, int Codec, List<string> Urls, DashByteRange Initialization, DashByteRange IndexRange, int Width, int Height, long Bandwidth);
    private sealed record Media(DashStream Video, DashStream Audio);
    private sealed record Row(int Pass, string Variant, int Speed, bool Baseline, double? SilentMs, double VideoBeforeAudioSeconds, double LongestAudioStallMs, double PauseSeconds, int AudioReconfigs, int StalledBodies, int AudioRequests, int AudioFailovers, bool Muted, bool? Exclusive, DateTimeOffset StartedUtc, int Samples, double MaxSampleGapMs,
        double? VideoStartMs, string Policy, double ThresholdSeconds, int RushMs, double? AudioBufferSeconds, int InvalidAudioPts, int AudioTimestampResets, CorrectionRecord[] Corrections,
        int VideoId, int VideoCodec, int VideoWidth, int VideoHeight, long VideoBitrate, string BackCacheBytes, int VideoRequests, int VideoFailovers,
        int? StartupVideoRequests, int? StartupAudioRequests, double? VideoOnlyMs, double? ResumeStallMs, int SamplePeriodMs,
        double MaxVideoOnlyMs, double MaxVideoOnlyContentSeconds, VideoCacheFootprint? VideoCacheFootprint,
        double? FirstFiniteAudioMs, double? ResumeBothStartMs, double MaxMissingPtsVideoWallMs, double MaxMissingPtsVideoMediaMs,
        double? CacheBeforePauseSeconds, double? CacheBeforeCorrectionSeconds, double? CacheAfterCorrectionSeconds,
        int? CorrectionVideoRequests, int? CorrectionAudioRequests, int[] InCacheSeekLogLines, double CacheGoalSeconds,
        double CacheWaitMs, bool CacheGoalReached, double? DistanceFromKeyframeSeconds);
    private sealed record RecoveryCacheObservation(double Milliseconds, double? ForwardSeconds, int VideoRequests, int AudioRequests);
    private sealed record CacheRange(double Start, double End);
    private sealed record VideoCacheFootprint(double Position, double FragmentStart, double FragmentDuration, uint FragmentBytes, bool FragmentStartsWithKeyframe,
        uint MaxFragmentBytes, double MaxFragmentDuration, bool AllFragmentsStartWithKeyframe, CacheRange[] SeekableRanges, bool CurrentPositionCached, long? ForwardBytes, long? RawInputRate);
    private readonly record struct Sample(double Milliseconds, double Position, double AudioPts, bool AudioAdvancing, bool Paused, bool Idle, bool Cached, bool Seeking, bool Muted, string? Ao);

    private sealed class TraceLogger(string path) : ILogger, IDisposable
    {
        private readonly StreamWriter _writer = new(path, false, Encoding.UTF8) { AutoFlush = true };
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _sync = new();
        private int _audioReconfigs;

        public event Action? AudioReconfigured;
        public int AudioReconfigs => Volatile.Read(ref _audioReconfigs);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            var reconfigured = text.Contains("EventAudioReconfig", StringComparison.Ordinal);
            if (reconfigured) Interlocked.Increment(ref _audioReconfigs);
            lock (_sync) _writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{_clock.Elapsed.TotalSeconds:0.000}] {text}"));
            if (reconfigured) AudioReconfigured?.Invoke();
        }

        public void Mark(string action, MpvClient client)
        {
            lock (_sync) _writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{_clock.Elapsed.TotalSeconds:0.000}] MARK {action} time-pos={ReadDouble(client, "time-pos"):0.000000} audio-pts={ReadDouble(client, "audio-pts"):0.000000} pause={Flag(client, "pause")}"));
            client.Command("print-text", "AUDIORESUME " + action);
        }

        public void Dispose() { lock (_sync) _writer.Dispose(); }
    }

    // The real anonymous audio bytes, with just enough initial data for ten seconds of playback.
    // A one-byte heartbeat prevents an unrelated relay timeout while paused. At resume the
    // connection remains open but the body supplies no data for five seconds. This prevents
    // eager demux prefetch from making the starvation injection an unexercised no-op.
    private sealed class DelayedAudioNode : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly byte[] _audio;
        private readonly long _prefixEnd;
        private readonly Task _loop;
        private long _stallUntil;
        private int _stalledBodies;

        private DelayedAudioNode(byte[] audio, long prefixEnd)
        {
            _audio = audio;
            _prefixEnd = prefixEnd;
            _listener.Start();
            Url = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/audio.m4s");
            _loop = AcceptAsync();
        }

        public string Url { get; }
        public int StalledBodies => Volatile.Read(ref _stalledBodies);
        public void StallFor(TimeSpan duration) => Interlocked.Exchange(ref _stallUntil, Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency));

        public static async Task<DelayedAudioNode> CreateAsync(string sourceUrl, DashStream audio, int speed)
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };
            // Seed through the same relay so a failing first CDN uses its ordinary failover.
            using var request = new HttpRequestMessage(HttpMethod.Get, sourceUrl);
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync();
            var index = DashIndex.Parse(bytes.AsMemory(0, checked((int)audio.IndexRange.Last + 1)), audio.Initialization, audio.IndexRange);
            return new(bytes, AudioPacketOffset(bytes, index, 10 * speed + 0.3));
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
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _connections.Add(ServeAsync(connection));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }

        private async Task ServeAsync(TcpClient connection)
        {
            try
            {
                using (connection)
                {
                    var token = _stop.Token;
                    var stream = connection.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    var first = await reader.ReadLineAsync(token);
                    if (first is null) return;
                    long from = 0;
                    var to = _audio.LongLength - 1;
                    while (await reader.ReadLineAsync(token) is { Length: > 0 } line)
                    {
                        if (!line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase)) continue;
                        var range = line[13..].Split('-');
                        from = long.Parse(range[0], CultureInfo.InvariantCulture);
                        if (range[1].Length > 0) to = Math.Min(to, long.Parse(range[1], CultureInfo.InvariantCulture));
                    }

                    var head = string.Create(CultureInfo.InvariantCulture, $"HTTP/1.1 206 Partial Content\r\nContent-Type: video/mp4\r\nContent-Length: {to - from + 1}\r\nContent-Range: bytes {from}-{to}/{_audio.LongLength}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head), token);
                    var offset = from;
                    while (offset <= to)
                    {
                        var until = Volatile.Read(ref _stallUntil);
                        if (offset >= _prefixEnd && until == 0)
                        {
                            await Task.Delay(1000, token);
                            if (Volatile.Read(ref _stallUntil) != 0) continue;
                            await stream.WriteAsync(_audio.AsMemory(checked((int)offset), 1), token);
                            offset++;
                            continue;
                        }

                        if (until != 0 && Stopwatch.GetTimestamp() < until)
                        {
                            Interlocked.Increment(ref _stalledBodies);
                            await Task.Delay(Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), until), token);
                        }

                        var count = (int)Math.Min(16 * 1024, to - offset + 1);
                        if (until == 0) count = (int)Math.Min(count, _prefixEnd - offset);
                        await stream.WriteAsync(_audio.AsMemory(checked((int)offset), count), token);
                        offset += count;
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or HttpRequestException) { }
        }

        private static long AudioPacketOffset(byte[] data, DashIndex index, double seconds)
        {
            var fragment = index.Fragments[index.FragmentAt(seconds)];
            var moof = FindBox(data.AsMemory(checked((int)fragment.Offset), checked((int)fragment.Size)), "moof"u8);
            var traf = FindBox(moof[8..], "traf"u8);
            var tfhd = FindBox(traf[8..], "tfhd"u8).Span;
            var tfdt = FindBox(traf[8..], "tfdt"u8).Span;
            var trun = FindBox(traf[8..], "trun"u8).Span;
            var flags = BinaryPrimitives.ReadUInt32BigEndian(tfhd[8..]) & 0xFFFFFF;
            var cursor = 16;
            var baseOffset = fragment.Offset;
            if ((flags & 1) != 0) { baseOffset = checked((long)BinaryPrimitives.ReadUInt64BigEndian(tfhd[cursor..])); cursor += 8; }
            if ((flags & 2) != 0) cursor += 4;
            var duration = (flags & 8) != 0 ? BinaryPrimitives.ReadUInt32BigEndian(tfhd[cursor..]) : 0;
            if ((flags & 8) != 0) cursor += 4;
            var size = (flags & 16) != 0 ? BinaryPrimitives.ReadUInt32BigEndian(tfhd[cursor..]) : 0;
            var time = tfdt[8] == 1 ? BinaryPrimitives.ReadUInt64BigEndian(tfdt[12..]) : BinaryPrimitives.ReadUInt32BigEndian(tfdt[12..]);
            flags = BinaryPrimitives.ReadUInt32BigEndian(trun[8..]) & 0xFFFFFF;
            var count = BinaryPrimitives.ReadUInt32BigEndian(trun[12..]);
            cursor = 16;
            if ((flags & 1) == 0) throw new InvalidDataException("Audio fixture requires a trun data offset.");
            var offset = baseOffset + BinaryPrimitives.ReadInt32BigEndian(trun[cursor..]);
            cursor += 4;
            if ((flags & 4) != 0) cursor += 4;
            for (var sample = 0; sample < count; sample++)
            {
                var sampleDuration = (flags & 0x100) != 0 ? BinaryPrimitives.ReadUInt32BigEndian(trun[cursor..]) : duration;
                if ((flags & 0x100) != 0) cursor += 4;
                var sampleSize = (flags & 0x200) != 0 ? BinaryPrimitives.ReadUInt32BigEndian(trun[cursor..]) : size;
                if ((flags & 0x200) != 0) cursor += 4;
                if ((flags & 0x400) != 0) cursor += 4;
                if ((flags & 0x800) != 0) cursor += 4;
                if (sampleDuration == 0 || sampleSize == 0) throw new InvalidDataException("Audio fixture has an empty sample.");
                if (time / (double)index.Timescale >= seconds) return offset;
                time += sampleDuration;
                offset += sampleSize;
            }

            return fragment.EndOffset;
        }

        private static ReadOnlyMemory<byte> FindBox(ReadOnlyMemory<byte> data, ReadOnlySpan<byte> type)
        {
            var offset = 0;
            while (offset <= data.Length - 8)
            {
                var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.Span[offset..]));
                if (size < 8 || size > data.Length - offset) throw new InvalidDataException("Audio fixture has an invalid box.");
                if (data.Span.Slice(offset + 4, 4).SequenceEqual(type)) return data.Slice(offset, size);
                offset += size;
            }

            throw new InvalidDataException("Audio fixture is missing a required box.");
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
