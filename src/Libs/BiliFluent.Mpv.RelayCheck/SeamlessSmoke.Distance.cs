// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using System.Text.Json;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Player;

namespace BiliFluent.Mpv.RelayCheck;

internal static partial class SeamlessSmoke
{
    private static readonly double[] DistancePositions = [0.5, 5.5, 10.5, 15.5, 20.5, 25.5, 30.5, 40.5, 50.5, 70.5, 120.5, 180.5, 305.5, 335.5, 540.5];

    private static async Task RunDistancePassAsync(Report report, string dll, Media media, int pass, string? output, IReadOnlyDictionary<string, string> options)
    {
        // Keep network latency separate from the seek-distance measurement. All nodes remain
        // available for failover, but the last anonymous mirror is tried first in this probe.
        static DashStream Preferred(DashStream stream) => stream with { Nodes = [stream.Nodes[^1], .. stream.Nodes.Take(stream.Nodes.Count - 1)] };
        var video = Preferred(media.Video.First(stream => stream.Id == 16 && stream.Codec == 7));
        var audioId = options.TryGetValue("distance-audio", out var audioOption) ? int.Parse(audioOption, CultureInfo.InvariantCulture) : 30280;
        var initialAudio = Preferred(media.Audio.First(stream => stream.Id == (audioId == 30280 ? 30216 : 30280)));
        var targetAudio = Preferred(media.Audio.First(stream => stream.Id == audioId));
        var positions = options.TryGetValue("positions", out var positionOption)
            ? positionOption.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray() : DistancePositions;
        var logger = new SafeTraceLogger();
        var client = await MpvClient.CreateAsync(dll, new() { UseConfig = false, LoadScripts = false }, logger);
        using var context = new HeadlessContext();
        var player = await context.CreateAsync(() => new MpvPlayer(client, new Resolver(video, initialAudio), logger: logger)
        {
            RelayOptions = new() { VerifyTls = true, ConnectTimeout = TimeSpan.FromSeconds(2), ResponseTimeout = TimeSpan.FromSeconds(3), ReadTimeout = TimeSpan.FromSeconds(3) },
        });
        await using var ownedPlayer = player;
        foreach (var (name, value) in new (string, string)[]
        {
            ("vo", "null"), ("ao", "null"), ("hwdec", "no"), ("cache", "yes"),
            ("demuxer-max-bytes", "64MiB"), ("demuxer-max-back-bytes", "0"), ("demuxer-readahead-secs", "60"),
            ("cache-pause", "yes"), ("cache-pause-wait", "3"), ("keep-open", "yes"), ("tls-verify", "yes"),
        })
        {
            if (client.SetOptionString(name, value) != MpvError.Success) throw new InvalidOperationException("Distance probe option rejected.");
        }

        await player.InitializeAsync();
        if (!await WaitAsync(() => Position(client) > 0.2 && !Flag(client, "core-idle"), 20_000)) throw new InvalidOperationException("Distance probe playback did not begin.");
        using var sampler = new Sampler(client);
        logger.BindClock(sampler.Now);
        player.SeamlessSwitchProgress += (_, progress) => logger.RecordProgress(progress);
        var rows = new List<Row>();
        foreach (var desired in positions)
        {
            if (desired > 1)
            {
                client.Command(["seek", desired.ToString("0.000", CultureInfo.InvariantCulture), "absolute+exact"]);
                if (!await WaitAsync(() => Math.Abs(Position(client) - desired) < 2 && !Flag(client, "core-idle") && !Flag(client, "seeking")
                    && client.GetProperty("audio-pts", out double pts) == MpvError.Success && double.IsFinite(pts), 20_000))
                {
                    report.Check($"distance {desired:0.0}s: seek completed", false);
                    break;
                }

                await Task.Delay(150);
            }

            var start = sampler.Now;
            var result = await player.SwitchAudioAsync(Target(targetAudio));
            await Task.Delay(1100);
            var metrics = sampler.Measure(start, sampler.Now, 1);
            rows.Add(new("distance", targetAudio.Id, targetAudio.Codec, 1, result, metrics, result.Preparation.TotalMilliseconds, start, sampler.Now));
            report.Check($"distance {desired:0.0}s: added/selected", result.IsSuccess,
                string.Create(CultureInfo.InvariantCulture, $"gap={result.AudioOpeningGapBytes} fill={result.AdditionalOpeningBytes} selected={result.SelectionPositionSeconds:0.000}s core={metrics.CoreGapMs:0.0}ms audio={metrics.AudioGapMs:0.0}ms"));
        }

        if (output is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(output, $"distance-{pass}.json"), JsonSerializer.Serialize(rows, JsonOptions));
            await sampler.WriteAsync(Path.Combine(output, $"distance-{pass}.samples.csv"));
            await logger.WriteAsync(Path.Combine(output, $"distance-{pass}.io.json"));
        }
    }
}
