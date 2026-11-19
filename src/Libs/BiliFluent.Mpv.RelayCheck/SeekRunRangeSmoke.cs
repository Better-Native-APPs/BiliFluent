// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Core.Models;
using Richasy.MpvKernel.Relay;

namespace BiliFluent.Mpv.RelayCheck;

internal static partial class SeamlessSmoke
{
    /// <summary>Captures the relay's existing structured request/read trace for the two load positions.</summary>
    public static async Task RunSeekRunRangesAsync(Report report, Dictionary<string, string> options)
    {
        if (!options.TryGetValue("dll", out var dll) || !File.Exists(dll))
        {
            report.Check("seek-run-ranges: dll option is required", false);
            return;
        }

        var output = options.GetValueOrDefault("out", Path.Combine(Path.GetTempPath(), "bf-seekrun-ranges"));
        Directory.CreateDirectory(output);
        var media = await FetchSponsorMediaAsync("BV1vcsH6EEEr", "43620107071");
        var video = media.Video.Single(stream => stream.Id == 80 && stream.Codec == 7);
        var audio = media.Audio.OrderByDescending(stream => stream.Id).First();
        using var resolution = new TimerResolution();
        foreach (var start in new[] { 0d, 300d })
        {
            var name = "start-" + start.ToString("0", CultureInfo.InvariantCulture);
            var trace = new SafeTraceLogger();
            await using var relay = LoopbackMediaRelay.Start(new() { VerifyTls = true }, trace);
            await using var client = await MpvClient.CreateAsync(dll, new() { UseConfig = false, LoadScripts = false });
            foreach (var (key, value) in new (string, string)[]
            {
                ("vo", "null"), ("ao", "null"), ("hwdec", "no"), ("ytdl", "no"), ("cookies", "no"), ("terminal", "no"),
                ("idle", "yes"), ("keep-open", "yes"), ("cache", "yes"), ("tls-verify", "yes"),
                ("demuxer-max-bytes", "64MiB"), ("demuxer-readahead-secs", "60"), ("demuxer-max-back-bytes", "32MiB"),
                ("cache-pause", "yes"), ("cache-pause-wait", "3"), ("log-file", Path.Combine(output, name + ".mpv.log")), ("msg-level", "all=v"),
            })
            {
                if (client.SetOptionString(key, value) != MpvError.Success) throw new InvalidOperationException("Seek-run range option rejected.");
            }

            var videoUrl = relay.Register("seekrun-video", new() { Upstreams = video.Nodes, Headers = RequestHeaders });
            var audioUrl = relay.Register("seekrun-audio", new() { Upstreams = audio.Nodes, Headers = RequestHeaders });
            await client.PlayAsync(videoUrl, new MpvPlayOptions { StartPosition = start, AudioTracks = [audioUrl], EnableYtdl = false });
            var clock = Stopwatch.StartNew();
            RelayStreamStatistics? statistics = null;
            while (clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                statistics = relay.GetStatistics("seekrun-audio");
                if (Position(client) >= start + 5 && statistics is { Length: > 0, ActiveRequests: 0 } snapshot
                    && snapshot.ForwardBytes >= snapshot.Length.Value) break;
                await Task.Delay(30);
            }

            var position = Position(client);
            var loadStart = client.GetPropertyString("file-local-options/start");
            report.Check("seek-run-ranges " + name + ": exact file-local start is readable",
                double.TryParse(loadStart, NumberStyles.Float, CultureInfo.InvariantCulture, out var loadedStart) && loadedStart == start,
                "file-local-start=" + loadStart);
            report.Check("seek-run-ranges " + name + ": start and audio tail observed", position >= start + 5
                && statistics is { Length: > 0 } done && done.ForwardBytes >= done.Length.Value,
                string.Create(CultureInfo.InvariantCulture, $"position={position:0.000000} requests={statistics?.Requests} length={statistics?.Length}"));
            await File.WriteAllTextAsync(Path.Combine(output, name + ".statistics.json"), JsonSerializer.Serialize(statistics, JsonOptions));
            await client.DisposeAsync();
            await relay.DisposeAsync();
            await trace.WriteAsync(Path.Combine(output, name + ".relay.json"));
        }
    }
}
