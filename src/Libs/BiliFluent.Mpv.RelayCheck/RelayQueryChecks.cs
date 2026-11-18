// Copyright (c) BiliFluent. All rights reserved.

using System.Reflection;
using Microsoft.Extensions.Logging;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Core.Models;
using Richasy.MpvKernel.Player;
using Richasy.MpvKernel.Player.Models;
using Richasy.MpvKernel.Relay;

namespace BiliFluent.Mpv.RelayCheck;

internal static class RelayQueryChecks
{
    private static readonly string[] ProxyNames = ["http_proxy", "HTTP_PROXY", "all_proxy", "ALL_PROXY"];

    public static async Task RunAsync(Report report)
    {
        var saved = ProxyNames.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in ProxyNames) Environment.SetEnvironmentVariable(name, null);
            foreach (var name in new[] { "http_proxy", "all_proxy" })
            {
                Environment.SetEnvironmentVariable(name, "http://127.0.0.1:9");
                var log = new QueryLogger();
                await using var player = CreatePlayer(log, new());
                report.Check($"relay query: {name} selects direct without starting", !player.WillRelayNextOnDemandLoad()
                    && GetRelay(player) is null && !player.WillRelayNextOnDemandLoad());
                var source = Source();
                report.Check($"relay query: {name} load remains direct with one relay-off log", Prepare(player, source).Url == source.Url
                    && log.Messages.Count(message => message.Contains("relay off reason=env-proxy", StringComparison.Ordinal)) == 1);
                Environment.SetEnvironmentVariable(name, null);
            }

            var failedLog = new QueryLogger();
            await using (var failed = CreatePlayer(failedLog, new() { ConnectTimeout = TimeSpan.Zero }))
            {
                report.Check("relay query: injected startup failure is retained", !failed.WillRelayNextOnDemandLoad() && !failed.WillRelayNextOnDemandLoad());
                var source = Source();
                report.Check("relay query: startup failure uses direct with one relay-off log", Prepare(failed, source).Url == source.Url
                    && failedLog.Messages.Count(message => message.Contains("relay off reason=start-failed", StringComparison.Ordinal)) == 1);
            }

            var startedLog = new QueryLogger();
            await using (var ready = CreatePlayer(startedLog, new()))
            {
                report.Check("relay query: normal setup answers relayed", ready.WillRelayNextOnDemandLoad());
                var first = GetRelay(ready);
                var again = ready.WillRelayNextOnDemandLoad();
                var prepared = Prepare(ready, Source());
                report.Check("relay query: PrepareLoad uses exactly the instance started by the query", first is not null && again
                    && ReferenceEquals(first, GetRelay(ready)) && new Uri(prepared.Url).IsLoopback
                    && startedLog.Messages.Count(message => message == "Playback | relay started") == 1);
                Invoke(ready, "HandleRelayFileFailed");
                report.Check("relay query: an unreachable current media answers direct on the existing retry", !ready.WillRelayNextOnDemandLoad());
                Invoke(ready, "ReleaseRelayStreams");
                report.Check("relay query: a new media clears its one-media skip but reuses the relay", ready.WillRelayNextOnDemandLoad() && ReferenceEquals(first, GetRelay(ready)));
            }

            var warmLog = new QueryLogger();
            await using (var warm = CreatePlayer(warmLog, new()))
            {
                warm.WillRelayNextOnDemandLoad();
                Environment.SetEnvironmentVariable("all_proxy", "http://127.0.0.1:9");
                report.Check("relay query: a proxy added after startup also selects direct", !warm.WillRelayNextOnDemandLoad() && !warm.WillRelayNextOnDemandLoad());
                var source = Source();
                report.Check("relay query: a warm proxy query and PrepareLoad agree with one relay-off log", Prepare(warm, source).Url == source.Url
                    && warmLog.Messages.Count(message => message.Contains("relay off reason=env-proxy", StringComparison.Ordinal)) == 1);
                Environment.SetEnvironmentVariable("all_proxy", null);
            }
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static MpvPlayer CreatePlayer(ILogger logger, LoopbackMediaRelayOptions options)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        try { return new(new MpvClient(new MpvCoreHandle(() => { })), new SourceResolver(), logger: logger) { RelayOptions = options }; }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static MpvMediaSource Source()
        => new("https://127.0.0.1:9/query-media")
        {
            Relay = new() { Video = new([new("https://127.0.0.1:9/query-media", "fake")]) },
        };

    private static LoopbackMediaRelay? GetRelay(MpvPlayer player)
        => (LoopbackMediaRelay?)typeof(MpvPlayer).GetField("_relay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player);

    private static (string Url, MpvPlayOptions Options) Prepare(MpvPlayer player, MpvMediaSource source)
        => ((string, MpvPlayOptions))Invoke(player, "PrepareLoad", source)!;

    private static object? Invoke(MpvPlayer player, string name, params object?[] arguments)
        => typeof(MpvPlayer).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(player, arguments);

    private sealed class SourceResolver : IMpvMediaSourceResolver
    {
        public Task<MpvMediaSource> GetSourceAsync() => Task.FromResult(Source());
        public IMpvMediaSourceResolver Clone() => new SourceResolver();
    }

    private sealed class QueryLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add(formatter(state, exception));
        }
    }
}
