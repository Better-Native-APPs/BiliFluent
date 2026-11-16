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
            case "unit":
                UnitChecks.Run(report);
                break;
            case "relay":
                await RelayChecks.RunAsync(report);
                break;
            case "all":
                UnitChecks.Run(report);
                await RelayChecks.RunAsync(report);
                break;
            case "smoke":
                await Smoke.RunAsync(report, options);
                break;
            default:
                Console.WriteLine("usage: unit | relay | all | smoke [dll=<libmpv-2.dll>] [bvid=..] [cid=..] [tls=on|off]");
                return 2;
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"RESULT mode={mode} passed={report.Passed} failed={report.Failed}"));
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
