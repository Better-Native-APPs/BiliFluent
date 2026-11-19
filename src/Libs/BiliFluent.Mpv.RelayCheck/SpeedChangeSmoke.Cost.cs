// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using static BiliFluent.Mpv.RelayCheck.PlaybackSmokeSupport;

namespace BiliFluent.Mpv.RelayCheck;

internal static partial class SpeedChangeSmoke
{
    private static async Task RunCostsAsync(Report report, Settings settings, NativeMpv mpv, FrameProbe probe, CaseResult row, string directory)
    {
        Mark(mpv, "cost steady-30");
        var before = Snapshot.Read(mpv, probe);
        await DelayUntilAsync(probe, before.WallMs + 30000).ConfigureAwait(false);
        var steady = Finish(mpv, probe, before, settings.InitialSpeed, row.Transitions, "steady-30", 1);
        row.Observations.Add(steady);
        Persist(row, directory);
        Note(report, row.Key, steady);

        for (var iteration = 1; iteration <= 5; iteration++)
        {
            Mark(mpv, "pause-resume " + iteration.ToString(CultureInfo.InvariantCulture));
            RequireSuccess(mpv.SetProperty("pause", "yes").Error, "pause");
            await Task.Delay(1000).ConfigureAwait(false);
            var position = mpv.GetNumber("time-pos");
            var audio = mpv.GetNumber("audio-pts");
            var call = mpv.SetProperty("pause", "no");
            RequireSuccess(call.Error, "resume");
            var recovery = await WaitForProgressAsync(mpv, position, audio).ConfigureAwait(false);
            row.RecoveryChecks.Add(new("pause-resume", iteration, true, null, call.Milliseconds, recovery,
                mpv.GetNumber("avsync"), mpv.GetString("current-ao") == "wasapi" && mpv.GetString("mute") == "yes"));
            report.Check(FormattableString.Invariant($"speed-change: {row.Key} pause/resume {iteration} recovers real audio and video"),
                recovery is not null && row.RecoveryChecks[^1].MutedWasapi);
            Persist(row, directory);
            await Task.Delay(2000).ConfigureAwait(false);
        }

        for (var iteration = 1; iteration <= 5; iteration++)
        {
            Mark(mpv, "cached-seek " + iteration.ToString(CultureInfo.InvariantCulture));
            var position = mpv.GetNumber("time-pos") ?? throw new InvalidOperationException("The cached seek position is unavailable.");
            var target = position + (iteration % 2 == 0 ? -5 : 5);
            var rangesText = mpv.GetString("demuxer-cache-state") ?? throw new InvalidOperationException("The seekable cache state is unavailable.");
            using var cache = JsonDocument.Parse(rangesText);
            var cached = cache.RootElement.TryGetProperty("seekable-ranges", out var ranges) && ranges.EnumerateArray()
                .Any(range => range.GetProperty("start").GetDouble() <= target && range.GetProperty("end").GetDouble() > target);
            if (!cached) throw new InvalidOperationException("The diagnostic seek target is not in the existing cache.");
            var call = mpv.Command("seek", target.ToString("0.000000", CultureInfo.InvariantCulture), "absolute+exact");
            RequireSuccess(call.Error, "cached seek");
            var recovery = await WaitForSeekAsync(mpv, target).ConfigureAwait(false);
            row.RecoveryChecks.Add(new("cached-seek", iteration, cached, target, call.Milliseconds, recovery,
                mpv.GetNumber("avsync"), mpv.GetString("current-ao") == "wasapi" && mpv.GetString("mute") == "yes"));
            report.Check(FormattableString.Invariant($"speed-change: {row.Key} cached seek {iteration} recovers real audio and video"),
                recovery is not null && row.RecoveryChecks[^1].MutedWasapi);
            Persist(row, directory);
            await Task.Delay(2000).ConfigureAwait(false);
        }
    }

    private static async Task<double?> WaitForProgressAsync(NativeMpv mpv, double? position, double? audio)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started).TotalSeconds < 5)
        {
            if (position is { } videoBefore && audio is { } audioBefore && mpv.GetNumber("time-pos") > videoBefore + 0.01
                && mpv.GetNumber("audio-pts") > audioBefore + 0.01 && mpv.GetString("core-idle") == "no")
                return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            await Task.Delay(10).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task<double?> WaitForSeekAsync(NativeMpv mpv, double target)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started).TotalSeconds < 5)
        {
            if (mpv.GetString("seeking") == "no" && mpv.GetString("core-idle") == "no"
                && mpv.GetNumber("time-pos") is { } position && Math.Abs(position - target) < 1
                && mpv.GetNumber("audio-pts") is { } audio && Math.Abs(audio - target) < 1)
            {
                var remaining = await WaitForProgressAsync(mpv, position, audio).ConfigureAwait(false);
                return remaining is not null ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : null;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        return null;
    }

    private sealed record RecoveryCheck(string Kind, int Iteration, bool Cached, double? TargetPosition, double CommandMs,
        double? RecoveredMs, double? AvsyncSeconds, bool MutedWasapi);
}
