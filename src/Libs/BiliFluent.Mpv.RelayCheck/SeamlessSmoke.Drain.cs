// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Player;

namespace BiliFluent.Mpv.RelayCheck;

internal static partial class SeamlessSmoke
{
    private static readonly string[] DrainPhases = ["loading", "loaded", "playing", "seek", "track"];

    public static async Task RunDrainAsync(Report report, Dictionary<string, string> options)
    {
        if (!options.TryGetValue("dll", out var dll) || !File.Exists(dll))
        {
            report.Check("drain-smoke: dll option is required", false);
            return;
        }

        if (!string.Equals(options.GetValueOrDefault("tls", "on"), "on", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Drain smoke requires TLS verification.", nameof(options));
        var rounds = int.Parse(options.GetValueOrDefault("rounds", "50"), CultureInfo.InvariantCulture);
        var passes = int.Parse(options.GetValueOrDefault("passes", "3"), CultureInfo.InvariantCulture);
        if (rounds < 50 || passes is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(options));
        var baseline = options.ContainsKey("baseline");
        var output = options.GetValueOrDefault("out");
        if (output is not null) Directory.CreateDirectory(output);
        using var resolution = new TimerResolution();
        var rows = new List<DrainRow>();
        for (var pass = 1; pass <= passes; pass++)
        {
            var media = await FetchAsync(options.GetValueOrDefault("bvid", "BV1vcsH6EEEr"), options.GetValueOrDefault("cid", "43620107071"));
            var random = new Random(20261116 + pass);
            for (var round = 0; round < rounds; round++)
            {
                try
                {
                    var row = await RunDrainRoundAsync(dll, media, pass, round, random, baseline);
                    rows.Add(row);
                    report.Check($"drain pass {pass} round {round + 1} {row.Phase}: returned safely", row.LoopEnded
                        && (baseline || row.LateNotifications == 0), string.Create(CultureInfo.InvariantCulture,
                            $"close={row.CloseMs:0.00}ms late={row.LateNotifications} loop-ended={row.LoopEnded}"));
                }
                catch (Exception ex)
                {
                    report.Check($"drain pass {pass} round {round + 1}: completed", false, ex.GetType().Name);
                    return;
                }
            }

            var times = rows.Where(row => row.Pass == pass).Select(row => row.CloseMs).Order().ToArray();
            report.Note(string.Create(CultureInfo.InvariantCulture,
                $"drain pass={pass} baseline={baseline} rounds={times.Length} median={Median(times):0.00}ms max={times[^1]:0.00}ms"));
            if (output is not null)
                await File.WriteAllTextAsync(Path.Combine(output, $"drain-pass-{pass}.json"), JsonSerializer.Serialize(rows.Where(row => row.Pass == pass), JsonOptions));
        }

        var allTimes = rows.Select(row => row.CloseMs).Order().ToArray();
        report.Note(string.Create(CultureInfo.InvariantCulture,
            $"drain total baseline={baseline} rounds={allTimes.Length} median={Median(allTimes):0.00}ms max={allTimes[^1]:0.00}ms"));
    }

    private static async Task<DrainRow> RunDrainRoundAsync(string dll, Media media, int pass, int round, Random random, bool baseline)
    {
        var video = media.Video.First(stream => stream.Id == 16 && stream.Codec == 7);
        var audio = media.Audio.First(stream => stream.Id == 30280);
        var otherAudio = media.Audio.First(stream => stream.Id != audio.Id);
        var client = await MpvClient.CreateAsync(dll, new() { UseConfig = false, LoadScripts = false });
        using var context = new HeadlessContext();
        var player = await context.CreateAsync(() => new MpvPlayer(client, new Resolver(video, audio))
        {
            RelayOptions = new() { VerifyTls = true, ConnectTimeout = TimeSpan.FromSeconds(2), ResponseTimeout = TimeSpan.FromSeconds(3), ReadTimeout = TimeSpan.FromSeconds(3) },
        });
        var notifications = 0;
        player.PropertyChanged += (_, _) => Interlocked.Increment(ref notifications);
        player.TrackUpdated += (_, _) => Interlocked.Increment(ref notifications);
        player.RelayUpstreamFailed += (_, _) => Interlocked.Increment(ref notifications);
        player.SeamlessSwitchProgress += (_, _) => Interlocked.Increment(ref notifications);
        player.SeamlessRelayUpstreamFailed += (_, _) => Interlocked.Increment(ref notifications);
        client.DataNotify += (_, _) => Interlocked.Increment(ref notifications);
        client.ReachFileLoaded += (_, _) => Interlocked.Increment(ref notifications);
        client.ReachFileEnd += (_, _) => Interlocked.Increment(ref notifications);
        client.CacheStateChanged += (_, _) => Interlocked.Increment(ref notifications);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ReachFileLoaded += (_, _) => loaded.TrySetResult();
        foreach (var (name, setting) in new (string, string)[]
        {
            ("vo", "null"), ("ao", "null"), ("hwdec", "no"), ("cache", "yes"), ("keep-open", "yes"),
            ("demuxer-max-bytes", "64MiB"), ("demuxer-max-back-bytes", "0"), ("demuxer-readahead-secs", "60"),
            ("cache-pause", "yes"), ("cache-pause-wait", "3"), ("tls-verify", "yes"),
        })
        {
            if (client.SetOptionString(name, setting) != MpvError.Success) throw new InvalidOperationException("Headless option rejected.");
        }

        var phase = round % DrainPhases.Length;
        var delay = random.Next(phase == 2 ? 50 : 0, phase == 2 ? 600 : 16);
        var initialize = player.InitializeAsync();
        Task? pending = null;
        if (phase != 0)
        {
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(25));
            if (phase >= 2 && !await WaitAsync(() => Position(client) > 0.2 && !Flag(client, "core-idle"), 25_000))
                throw new InvalidOperationException("Headless playback did not begin.");
            if (phase == 3) pending = client.SetCurrentPositionAsync(Position(client) + 5);
            if (phase == 4) pending = client.AddAudioTrackAsync(otherAudio.Nodes[0].Url, select: true);
        }

        await Task.Delay(delay);
        var clock = Stopwatch.StartNew();
        await player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        var closeMs = clock.Elapsed.TotalMilliseconds;
        var atReturn = Volatile.Read(ref notifications);
        var loop = (Task?)typeof(MpvClient).GetField("_eventLoopTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client);
        var loopEnded = loop?.IsCompleted != false;
        // The observation window is not a completion proof: the loop task and self-check gates are.
        await Task.Delay(100);
        await context.CreateAsync(() => player);
        var late = Volatile.Read(ref notifications) - atReturn;
        await ObserveDrainOperationAsync(initialize, baseline);
        if (pending is not null) await ObserveDrainOperationAsync(pending, baseline);
        return new(pass, round + 1, DrainPhases[phase], delay, closeMs, late, loopEnded);
    }

    private static async Task ObserveDrainOperationAsync(Task operation, bool baseline)
    {
        try { await operation.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (Exception ex) when (baseline && ex is ObjectDisposedException or InvalidOperationException) { }
    }

    private static double Median(double[] sorted)
        => sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2];

    private sealed record DrainRow(int Pass, int Round, string Phase, int DelayMs, double CloseMs, int LateNotifications, bool LoopEnded);
}
