// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Richasy.MpvKernel.Relay;
using static BiliFluent.Mpv.RelayCheck.PlaybackSmokeSupport;

namespace BiliFluent.Mpv.RelayCheck;

/// <summary>Measures native speed changes with offscreen GPU video and muted, real WASAPI audio.</summary>
internal static partial class SpeedChangeSmoke
{
    private const double StartPosition = 60;
    private const double ChangeSpacingMs = 9000;
    private const double BeforeMs = 500;
    private const double AfterMs = 3000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static async Task RunAsync(Report report, Dictionary<string, string> options)
    {
        if (!OperatingSystem.IsWindows() || !options.TryGetValue("dll", out var dll) || !File.Exists(dll)
            || !options.TryGetValue("out", out var output) || !options.TryGetValue("lock", out var lease))
        {
            report.Check("speed-change: Windows, dll=, out=, and lock= are required", false);
            return;
        }

        Settings settings;
        try { settings = ReadSettings(options); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            report.Check("speed-change: unsupported case, speed, variant, count, or pass", false);
            return;
        }

        var directory = Path.Combine(output, settings.Key);
        Directory.CreateDirectory(directory);
        if (File.Exists(Path.Combine(directory, "result.json")))
        {
            report.Check("speed-change: this pass already has data; choose a new pass", false);
            return;
        }

        var row = new CaseResult
        {
            Key = settings.Key, Case = settings.Case, From = settings.From, To = settings.To, Speed = settings.Speed,
            Variant = settings.Variant, Iterations = settings.Count, Pass = settings.Pass,
            StartedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        try
        {
            var owner = await File.ReadAllTextAsync(Path.Combine(lease, "owner")).ConfigureAwait(false);
            if (!owner.StartsWith("speedchange " + settings.Key + " ", StringComparison.Ordinal))
                throw new InvalidOperationException("The exclusive smoke lease does not belong to this case.");
            var media = await FetchMediaAsync().ConfigureAwait(false);
            row.Environment["runtime"] = RuntimeInformation.FrameworkDescription;
            row.Environment["source"] = "BV1vcsH6EEEr / 43620107071 / anonymous qn=80 AVC";
            row.Environment["source-dimensions"] = FormattableString.Invariant($"{media.Video.Width}x{media.Video.Height}");
            row.Environment["source-fps"] = media.Video.FrameRate;
            row.Environment["video-bps"] = media.Video.Bandwidth.ToString(CultureInfo.InvariantCulture);
            row.Environment["audio-bps"] = media.Audio.Bandwidth.ToString(CultureInfo.InvariantCulture);
            row.Environment["source-duration-seconds"] = media.Duration.ToString("0.000", CultureInfo.InvariantCulture);
            row.Environment["sample-period-ms"] = "4";
            row.Environment["change-spacing-ms"] = ChangeSpacingMs.ToString(CultureInfo.InvariantCulture);
            row.Environment["lag-conversion"] = "Wall gap minus integral(dPTS/speed), split at the synchronous pre-write position when speed changes.";
            Persist(row, directory);
            await RunCaseAsync(report, settings, dll, media, row, directory).ConfigureAwait(false);
            row.Status = "complete";
            report.Check("speed-change: " + settings.Key + " complete", true);
        }
        catch (Exception ex)
        {
            row.Status = "halted";
            // Native and HTTP messages can contain signed URLs or private paths.
            row.Failure = ex is InvalidOperationException ? ex.Message : ex.GetType().Name;
            report.Check("speed-change: " + settings.Key + " halted", false, row.Failure);
        }
        finally
        {
            row.EndedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            Persist(row, directory);
        }
    }

    private static async Task RunCaseAsync(Report report, Settings settings, string dll, Media media, CaseResult row, string directory)
    {
        await using var relay = LoopbackMediaRelay.Start(new() { VerifyTls = true });
        using var window = new OffscreenWindow();
        using var mpv = new NativeMpv(dll);
        Configure(mpv, settings, window, Path.Combine(directory, "mpv.log"));
        mpv.Initialize();
        var videoUrl = relay.Register("speed-video", new() { Upstreams = media.Video.Nodes, Headers = Headers });
        var audioUrl = relay.Register("speed-audio", new() { Upstreams = media.Audio.Nodes, Headers = Headers });
        CaptureEnvironment(mpv, window, row);
        using var probe = new FrameProbe(mpv);
        row.ProbeOrigin = probe.Origin;
        try
        {
            if (settings.Case == "startup")
            {
                await RunStartupAsync(report, settings, mpv, window, probe, videoUrl, audioUrl, row, directory).ConfigureAwait(false);
            }
            else
            {
                RequireSuccess(Load(mpv, videoUrl, audioUrl, settings.InitialSpeed).Error, "loadfile");
                await WaitForReadyAsync(mpv).ConfigureAwait(false);
                await VerifyOutputAsync(report, mpv, window).ConfigureAwait(false);
                row.Environment["timing-start-forward-cache-seconds"] = Number(mpv.GetNumber("demuxer-cache-duration"));
                CaptureEnvironment(mpv, window, row);
                if (settings.Case == "baseline") await RunBaselineAsync(report, settings, mpv, probe, row, directory).ConfigureAwait(false);
                else if (settings.Case == "cost") await RunCostsAsync(report, settings, mpv, probe, row, directory).ConfigureAwait(false);
                else await RunSwitchesAsync(report, settings, mpv, probe, row, directory).ConfigureAwait(false);
            }

            window.Verify();
            report.Check("speed-change: " + settings.Key + " WASAPI remained muted",
                mpv.GetString("current-ao") == "wasapi" && mpv.GetString("mute") == "yes");
        }
        finally
        {
            probe.Dispose();
            row.ProbeSamples = probe.SampleCount;
            row.ProbeLoopAllocatedBytes = probe.LoopAllocatedBytes;
            probe.WriteCsv(Path.Combine(directory, "samples.csv"));
            mpv.Dispose();
            var log = await File.ReadAllTextAsync(Path.Combine(directory, "mpv.log")).ConfigureAwait(false);
            row.InvalidAudioPts = log.Split('\n').Count(line => line.Contains("Invalid audio PTS", StringComparison.Ordinal));
            row.AudioTimestampResets = log.Split('\n').Count(line => line.Contains("Reset playback due to audio timestamp reset", StringComparison.Ordinal));
            row.AudioUnderruns = log.Split('\n').Count(line => line.Contains("Audio device underrun detected", StringComparison.Ordinal));
            var lines = log.Split('\n');
            row.MpvErrors = lines.Count(IsNativeError);
            var costStart = Array.FindIndex(lines, line => line.Contains("SPEEDCHANGE cost steady-30", StringComparison.Ordinal));
            row.RecoveryMpvErrors = costStart >= 0 ? lines.Skip(costStart).Count(IsNativeError) : null;
            if (settings.Case == "cost") report.Check("speed-change: " + settings.Key + " recovery log has no audio PTS errors, underruns, or native errors",
                row.InvalidAudioPts == 0 && row.AudioTimestampResets == 0 && row.AudioUnderruns == 0 && row.RecoveryMpvErrors == 0);
            report.Check("speed-change: " + settings.Key + " allocation-free probe loop", row.ProbeLoopAllocatedBytes == 0,
                "allocated-bytes=" + row.ProbeLoopAllocatedBytes.ToString(CultureInfo.InvariantCulture));
            Persist(row, directory);
        }
    }

    private static void Configure(NativeMpv mpv, Settings settings, OffscreenWindow window, string log)
    {
        foreach (var (name, value) in new (string, string)[]
        {
            ("config", "no"), ("load-scripts", "no"), ("terminal", "no"),
            ("vo", "gpu-next"), ("gpu-api", "d3d11"), ("gpu-context", "auto"), ("hwdec", "auto"),
            ("ao", "wasapi"), ("audio-exclusive", "no"), ("mute", "yes"), ("volume", "100"),
            ("cache", "yes"), ("demuxer-max-bytes", "64MiB"), ("demuxer-max-back-bytes", "32MiB"),
            ("demuxer-readahead-secs", "60"), ("cache-pause", "yes"), ("cache-pause-wait", "3"),
            ("keep-open", "yes"), ("idle", "yes"), ("ytdl", "no"), ("cookies", "no"), ("tls-verify", "yes"),
            ("target-colorspace-hint", "auto"), ("target-colorspace-hint-mode", "target"), ("target-colorspace-hint-strict", "yes"),
            ("d3d11-output-format", "auto"), ("d3d11-output-csp", "auto"),
            ("speed", settings.InitialSpeed.ToString(CultureInfo.InvariantCulture)), ("log-file", log), ("msg-level", "all=v"),
            ("wid", window.Handle.ToString(CultureInfo.InvariantCulture)),
        }) mpv.SetOption(name, value);
        if (settings.Variant == "fixed") mpv.SetOption("af", "scaletempo2");
        if (settings.Variant == "pitch-off") mpv.SetOption("audio-pitch-correction", "no");
        if (settings.Variant == "buffer-50") mpv.SetOption("audio-buffer", "0.05");
        if (settings.Variant == "buffer-10") mpv.SetOption("audio-buffer", "0.01");
    }

    private static CallResult Load(NativeMpv mpv, string videoUrl, string audioUrl, double speed)
        => mpv.Command("loadfile", videoUrl, "replace", "0",
            FormattableString.Invariant($"start={StartPosition:0.###},speed={speed:0.##},audio-file=\"{audioUrl}\""));

    private static async Task RunBaselineAsync(Report report, Settings settings, NativeMpv mpv, FrameProbe probe, CaseResult row, string directory)
    {
        var next = probe.Now;
        for (var iteration = 1; iteration <= settings.Count; iteration++)
        {
            await DelayUntilAsync(probe, next).ConfigureAwait(false);
            Mark(mpv, "baseline " + iteration.ToString(CultureInfo.InvariantCulture));
            var before = Snapshot.Read(mpv, probe);
            await DelayUntilAsync(probe, before.WallMs + BeforeMs + AfterMs).ConfigureAwait(false);
            var observation = Finish(mpv, probe, before, settings.InitialSpeed, row.Transitions, "baseline", iteration);
            row.Observations.Add(observation);
            Persist(row, directory);
            Note(report, row.Key, observation);
            next = before.WallMs + ChangeSpacingMs;
        }
    }

    private static async Task RunSwitchesAsync(Report report, Settings settings, NativeMpv mpv, FrameProbe probe, CaseResult row, string directory)
    {
        var previousWrite = probe.Now - ChangeSpacingMs;
        for (var iteration = 1; iteration <= settings.Count; iteration++)
        {
            await DelayUntilAsync(probe, previousWrite + ChangeSpacingMs).ConfigureAwait(false);
            Mark(mpv, FormattableString.Invariant($"switch {iteration} {settings.From:0.##}->{settings.To:0.##}"));
            var before = Snapshot.Read(mpv, probe);
            await DelayUntilAsync(probe, before.WallMs + BeforeMs).ConfigureAwait(false);
            var change = ChangeSpeed(mpv, settings.From, settings.To, true);
            row.Transitions.Add(change);
            await DelayUntilAsync(probe, probe.Wall(change.Call.Ended) + AfterMs).ConfigureAwait(false);
            var observation = Finish(mpv, probe, before, settings.InitialSpeed, row.Transitions, "switch", iteration);
            row.Observations.Add(observation);
            row.Writes.Add(new(iteration, probe.Wall(change.Call.Started), change.Call.Milliseconds, change.Position, change.From, change.To, true));
            report.Check(FormattableString.Invariant($"speed-change: {row.Key} switch {iteration} took effect"),
                mpv.GetNumber("speed") is { } speed && Math.Abs(speed - settings.To) < 0.000001);
            Persist(row, directory);
            Note(report, row.Key, observation);
            if (iteration == settings.Count) break;
            await DelayUntilAsync(probe, probe.Wall(change.Call.Ended) + ChangeSpacingMs).ConfigureAwait(false);
            Mark(mpv, "reset " + iteration.ToString(CultureInfo.InvariantCulture));
            var reset = ChangeSpeed(mpv, settings.To, settings.From, false);
            row.Transitions.Add(reset);
            row.Writes.Add(new(iteration, probe.Wall(reset.Call.Started), reset.Call.Milliseconds, reset.Position, reset.From, reset.To, false));
            previousWrite = probe.Wall(reset.Call.Ended);
        }
    }

    private static SpeedTransition ChangeSpeed(NativeMpv mpv, double from, double to, bool measured)
    {
        var position = mpv.GetNumber("time-pos") ?? throw new InvalidOperationException("The speed boundary position is unavailable.");
        var call = mpv.SetSpeed(to);
        RequireSuccess(call.Error, "speed");
        return new(from, to, position, call, measured);
    }

    private static async Task RunStartupAsync(Report report, Settings settings, NativeMpv mpv, OffscreenWindow window,
        FrameProbe probe, string videoUrl, string audioUrl, CaseResult row, string directory)
    {
        var next = probe.Now;
        for (var iteration = 1; iteration <= settings.Count; iteration++)
        {
            await DelayUntilAsync(probe, next).ConfigureAwait(false);
            Mark(mpv, FormattableString.Invariant($"startup {iteration} {settings.Speed:0.##}x"));
            var call = Load(mpv, videoUrl, audioUrl, settings.Speed);
            RequireSuccess(call.Error, "loadfile");
            var from = probe.Wall(call.Started);
            await WaitForStartupAsync(mpv, settings.Speed).ConfigureAwait(false);
            var before = Snapshot.Read(mpv, probe);
            await DelayUntilAsync(probe, before.WallMs + AfterMs).ConfigureAwait(false);
            var progress = probe.FirstProgress(from, StartPosition, settings.Speed);
            row.Startups.Add(new(iteration, from, call.Milliseconds, progress.VideoMs, progress.AudioMs,
                progress.VideoMs is { } video && progress.AudioMs is { } audio ? Math.Max(video, audio) : null));
            row.Observations.Add(Finish(mpv, probe, before, settings.Speed, row.Transitions, "startup", iteration));
            report.Check(FormattableString.Invariant($"speed-change: {row.Key} startup {iteration} loaded at requested speed"),
                mpv.GetNumber("speed") is { } speed && Math.Abs(speed - settings.Speed) < 0.000001
                && progress.VideoMs is not null && progress.AudioMs is not null);
            window.Verify();
            CaptureEnvironment(mpv, window, row);
            Persist(row, directory);
            Note(report, row.Key, row.Observations[^1]);
            next = from + ChangeSpacingMs;
        }

        await VerifyOutputAsync(report, mpv, window).ConfigureAwait(false);
    }

    private static async Task WaitForReadyAsync(NativeMpv mpv)
    {
        var started = Stopwatch.GetTimestamp();
        while (mpv.GetNumber("time-pos") is not >= StartPosition + 2 || mpv.GetNumber("audio-pts") is null
            || mpv.GetNumber("demuxer-cache-duration") is not >= 30 || mpv.GetString("core-idle") != "no")
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 90) throw new InvalidOperationException("Playback did not settle with real audio and at least 30 seconds of forward cache.");
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    private static async Task WaitForStartupAsync(NativeMpv mpv, double speed)
    {
        var started = Stopwatch.GetTimestamp();
        while (mpv.GetNumber("time-pos") is not { } position || position < StartPosition || position >= StartPosition + speed * 2
            || mpv.GetNumber("audio-pts") is not { } audio || audio < StartPosition - 0.1 || audio >= StartPosition + speed * 2
            || mpv.GetString("core-idle") != "no")
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 30) throw new InvalidOperationException("Playback did not begin at the requested load position and speed.");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private static async Task VerifyOutputAsync(Report report, NativeMpv mpv, OffscreenWindow window)
    {
        var positionBefore = mpv.GetNumber("time-pos");
        var dropsBefore = mpv.GetCounter("frame-drop-count");
        await Task.Delay(2000).ConfigureAwait(false);
        var positionAfter = mpv.GetNumber("time-pos");
        var fps = mpv.GetNumber("estimated-vf-fps");
        var drops = mpv.GetCounter("frame-drop-count") - dropsBefore;
        var expectedFrames = (positionAfter - positionBefore) * fps;
        window.Verify();
        if (mpv.GetString("current-vo") != "gpu-next" || fps is not > 0 || positionAfter is null || positionBefore is null
            || positionAfter <= positionBefore || (expectedFrames is > 1 && drops >= expectedFrames - 1))
            throw new InvalidOperationException("Offscreen gpu-next is not producing measurable video; do not substitute an onscreen window.");
        if (mpv.GetString("current-ao") != "wasapi" || mpv.GetString("mute") != "yes")
            throw new InvalidOperationException("Muted real WASAPI output is unavailable; do not substitute ao=null.");
        report.Check("speed-change: offscreen gpu-next produces video and real WASAPI is muted", true);
    }

    private static void CaptureEnvironment(NativeMpv mpv, OffscreenWindow window, CaseResult row)
    {
        foreach (var property in new[] { "mpv-version", "current-vo", "current-ao", "hwdec-current", "display-fps", "estimated-vf-fps",
            "video-sync", "options/profile", "options/cache-secs", "options/audio-buffer", "audio-delay", "options/af",
            "options/audio-pitch-correction", "audio-params/samplerate", "audio-out-params/samplerate", "audio-out-params/channels" })
            row.Environment[property] = mpv.GetString(property);
        row.WindowRectangle = window.WindowRectangle;
        row.DesktopRectangle = window.DesktopRectangle;
        row.Environment["desktop-refresh-hz"] = Number(window.DesktopHertz);
        row.Environment["monitor-count"] = window.MonitorCount.ToString(CultureInfo.InvariantCulture);
        row.Environment["window-contract"] = "WS_POPUP; WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE; SW_SHOWNOACTIVATE; 2560x1440; outside virtual desktop";
    }

    private static Observation Finish(NativeMpv mpv, FrameProbe probe, Snapshot before, double initialSpeed,
        IReadOnlyList<SpeedTransition> transitions, string kind, int iteration)
    {
        var after = Snapshot.Read(mpv, probe);
        return new(kind, iteration, before.WallMs, after.WallMs, probe.Measure(before.WallMs, after.WallMs, initialSpeed, transitions),
            after.Counters.Minus(before.Counters), after.AudioReconfigs - before.AudioReconfigs, after.PlaybackRestarts - before.PlaybackRestarts,
            after.GcPauseMs - before.GcPauseMs, after.Gen2 - before.Gen2, before.ForwardCacheSeconds, after.ForwardCacheSeconds,
            after.CpuMs - before.CpuMs, after.AudioDelaySeconds);
    }

    private static void Mark(NativeMpv mpv, string action) => RequireSuccess(mpv.Command("print-text", "SPEEDCHANGE " + action).Error, "marker");

    private static async Task DelayUntilAsync(FrameProbe probe, double milliseconds)
    {
        var remaining = milliseconds - probe.Now;
        if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining)).ConfigureAwait(false);
    }

    private static Settings ReadSettings(Dictionary<string, string> options)
    {
        var name = options.GetValueOrDefault("case", "switch");
        var from = double.Parse(options.GetValueOrDefault("from", "1"), CultureInfo.InvariantCulture);
        var to = double.Parse(options.GetValueOrDefault("to", "2"), CultureInfo.InvariantCulture);
        var speed = double.Parse(options.GetValueOrDefault("speed", "1"), CultureInfo.InvariantCulture);
        var variant = options.GetValueOrDefault("variant", "default");
        var count = int.Parse(options.GetValueOrDefault("count", "10"), CultureInfo.InvariantCulture);
        var pass = int.Parse(options.GetValueOrDefault("pass", "1"), CultureInfo.InvariantCulture);
        if (name is not ("baseline" or "switch" or "startup" or "cost") || variant is not ("default" or "fixed" or "pitch-off" or "buffer-50" or "buffer-10")
            || from is not (1 or 1.5 or 2 or 3) || to is not (1 or 1.5 or 2 or 3) || speed is not (1 or 1.5 or 2 or 3)
            || (name == "switch" && from == to) || count is < 1 or > 10 || pass is < 1 or > 99)
            throw new ArgumentException("Unsupported measurement settings.");
        if (name == "cost" && speed != 1) throw new ArgumentException("Costs must be measured at normal speed.");
        return new(name, from, to, speed, variant, count, pass);
    }

    private static void Persist(CaseResult row, string directory)
    {
        var temporary = Path.Combine(directory, "result.tmp.json");
        File.WriteAllText(temporary, JsonSerializer.Serialize(row, JsonOptions));
        File.Move(temporary, Path.Combine(directory, "result.json"), true);
    }

    private static void Note(Report report, string key, Observation observation)
        => report.Note(string.Create(CultureInfo.InvariantCulture,
            $"SPEEDCHANGE key={key} iteration={observation.Iteration} read-ms={observation.Metrics.MaximumReadMs:0.000} lag-ms={observation.Metrics.MaximumLagMs:0.000} frame-gap-ms={observation.Metrics.MaximumFrameGapMs:0.000} audio-gap-ms={observation.Metrics.MaximumAudioGapMs:0.000} missing-audio-ms={observation.Metrics.MaximumMissingAudioPtsMs:0.000} reconfigs={observation.AudioReconfigs} gc-pause-ms={observation.GcPauseMs:0.000}"));

    private static string? Number(double? value) => value?.ToString("0.000000", CultureInfo.InvariantCulture);

    private static bool IsNativeError(string line) => line.Contains("][e][", StringComparison.Ordinal) || line.Contains("][f][", StringComparison.Ordinal);

    private sealed record Settings(string Case, double From, double To, double Speed, string Variant, int Count, int Pass)
    {
        internal double InitialSpeed => Case == "switch" ? From : Speed;
        internal string Key => Case == "switch" ? FormattableString.Invariant($"switch-{From:0.##}-to-{To:0.##}-{Variant}-p{Pass}")
            : FormattableString.Invariant($"{Case}-{Speed:0.##}x-{Variant}-p{Pass}");
    }

    private sealed record Snapshot(double WallMs, Counters Counters, double GcPauseMs, int Gen2, int AudioReconfigs,
        int PlaybackRestarts, double? ForwardCacheSeconds, double CpuMs, double? AudioDelaySeconds)
    {
        internal static Snapshot Read(NativeMpv mpv, FrameProbe probe)
        {
            using var process = Process.GetCurrentProcess();
            var counters = Counters.Read(mpv);
            var cache = mpv.GetNumber("demuxer-cache-duration");
            var delay = mpv.GetNumber("audio-delay");
            return new(probe.Now, counters, GC.GetTotalPauseDuration().TotalMilliseconds, GC.CollectionCount(2), mpv.AudioReconfigs,
                mpv.PlaybackRestarts, cache, process.TotalProcessorTime.TotalMilliseconds, delay);
        }
    }

    private sealed record Observation(string Kind, int Iteration, double StartMs, double EndMs, WindowMetrics Metrics,
        Counters Counters, int AudioReconfigs, int PlaybackRestarts, double GcPauseMs, int Gen2,
        double? BeforeForwardCacheSeconds, double? AfterForwardCacheSeconds, double ProcessCpuMs, double? AudioDelaySeconds);
    private sealed record SpeedWrite(int Iteration, double StartMs, double CallMs, double Position, double From, double To, bool Measured);
    private sealed record Startup(int Iteration, double StartMs, double CallMs, double? FirstVideoMs, double? FirstAudioMs, double? BothStartedMs);

    private sealed class CaseResult
    {
        public string Key { get; set; } = string.Empty;
        public string Case { get; set; } = string.Empty;
        public double From { get; set; }
        public double To { get; set; }
        public double Speed { get; set; }
        public string Variant { get; set; } = string.Empty;
        public int Iterations { get; set; }
        public int Pass { get; set; }
        public string StartedUtc { get; set; } = string.Empty;
        public string? EndedUtc { get; set; }
        public string Status { get; set; } = "in-progress";
        public string? Failure { get; set; }
        public Dictionary<string, string?> Environment { get; set; } = new(StringComparer.Ordinal);
        public Rectangle? WindowRectangle { get; set; }
        public Rectangle? DesktopRectangle { get; set; }
        public long ProbeOrigin { get; set; }
        public int ProbeSamples { get; set; }
        public long ProbeLoopAllocatedBytes { get; set; }
        public int InvalidAudioPts { get; set; }
        public int AudioTimestampResets { get; set; }
        public int AudioUnderruns { get; set; }
        public int MpvErrors { get; set; }
        public int? RecoveryMpvErrors { get; set; }
        public List<SpeedTransition> Transitions { get; set; } = [];
        public List<SpeedWrite> Writes { get; set; } = [];
        public List<Startup> Startups { get; set; } = [];
        public List<Observation> Observations { get; set; } = [];
        public List<RecoveryCheck> RecoveryChecks { get; set; } = [];
    }
}
