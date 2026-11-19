// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;

namespace BiliFluent.Mpv.RelayCheck;

/// <summary>
/// Runs the checks of the loopback media relay. See README.md.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "all";
        var options = args.Skip(1)
            .Select(arg => arg.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);
        var report = new Report();
        switch (mode)
        {
            case "seek-run-smoke": await SeamlessSmoke.RunSeekRunAsync(report, options); break;
            case "seek-run-ranges": await SeamlessSmoke.RunSeekRunRangesAsync(report, options); break;
            case "sponsor-cache-smoke": await SeamlessSmoke.RunSponsorCacheAsync(report, options); break;
            case "speed-change-smoke": await SpeedChangeSmoke.RunAsync(report, options); break;
            case "unit":
                UnitChecks.Run(report);
                break;
            case "relay":
                await RelayChecks.RunAsync(report);
                break;
            case "all":
                UnitChecks.Run(report);
                DashChecks.Run(report);
                await RelayChecks.RunAsync(report);
                await MemoryRelayChecks.RunAsync(report);
                await DisposeChecks.RunAsync(report);
                await ThroughputChecks.RunAsync(report);
                await RelayQueryChecks.RunAsync(report);
                break;
            case "throughput":
                await ThroughputChecks.RunAsync(report);
                await RelayQueryChecks.RunAsync(report);
                break;
            case "throughput-smoke":
                await ThroughputSmoke.RunAsync(report, options);
                break;
            case "drain":
                await DisposeChecks.RunAsync(report);
                break;
            case "smoke":
                await Smoke.RunAsync(report, options);
                break;
            case "seamless-smoke":
                await SeamlessSmoke.RunAsync(report, options);
                break;
            case "drain-smoke":
                await SeamlessSmoke.RunDrainAsync(report, options);
                break;
            case "audio-resume": await AudioResumeSmoke.RunAsync(report, options); break;
            default:
                Console.WriteLine("usage: unit | relay | all | drain | throughput | throughput-smoke [dll=..] [tls=on] | smoke [dll=..] | seamless-smoke [dll=..] [passes=3] [tls=on] [out=..] | drain-smoke [dll=..] [passes=3] [rounds=50] [baseline=1] [out=..] | seek-run-smoke [dll=..] [passes=3] [out=..] [report=..] [analyze=1] | speed-change-smoke dll=.. out=.. lock=.. [case=baseline|switch|startup|cost] [from=1] [to=2] [speed=1] [variant=default|fixed|pitch-off|buffer-50|buffer-10] [count=10] [pass=1]");
                return 2;
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"RESULT mode={mode} passed={report.Passed} failed={report.Failed}"));
        if (report.NotApplicableCount > 0) Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"NOT_APPLICABLE_COUNT={report.NotApplicableCount}"));
        return report.Failed == 0 ? 0 : 1;
    }
}

/// <summary>
/// Counts what passed and what failed, and prints each check as it ends.
/// </summary>
internal sealed class Report
{
    public int Passed { get; private set; }

    public int Failed { get; private set; }

    public int NotApplicableCount { get; private set; }

    public void Check(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            Passed++;
        }
        else
        {
            Failed++;
        }

        Console.WriteLine((condition ? "ok   " : "FAIL ") + name + (detail is null ? string.Empty : "  [" + detail + "]"));
    }

    public void Equal<T>(string name, T expected, T actual)
        => Check(name, EqualityComparer<T>.Default.Equals(expected, actual), string.Create(CultureInfo.InvariantCulture, $"expected={expected} actual={actual}"));

    public void Note(string text) => Console.WriteLine("     " + text);

    public void NotApplicable(string name, string detail)
    {
        NotApplicableCount++;
        Console.WriteLine("n/a  " + name + "  [" + detail + "]");
    }

    public async Task RunAsync(string name, Func<Task> check)
    {
        try
        {
            await check().WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (Exception ex)
        {
            Check(name, false, ex.GetType().Name + ": " + ex.Message);
        }
    }
}
