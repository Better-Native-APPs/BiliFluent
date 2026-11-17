#nullable enable

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace BiliCopilot.UI.Live;

internal readonly record struct LiveChatPreparation(bool Sessdata, bool BiliJct, bool Buvid,
    int? NavCode, bool? NavLogin, bool? NavMidMatch, int Hosts)
{
    internal static LiveChatPreparation Capture(string cookies, JsonElement navigation, long userId, int hosts)
    {
        int? code = LiveHttpApi.TryApiCode(navigation, out var value) ? value : null;
        bool? login = null;
        bool? midMatch = null;
        if (navigation.ValueKind == JsonValueKind.Object
            && navigation.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("isLogin", out var isLogin) && isLogin.ValueKind is JsonValueKind.True or JsonValueKind.False)
                login = isLogin.GetBoolean();
            if (data.TryGetProperty("mid", out var mid) && mid.ValueKind == JsonValueKind.Number && mid.TryGetInt64(out var navUserId))
                midMatch = navUserId == userId;
        }
        return new(HasCookie(cookies, "SESSDATA"), HasCookie(cookies, "bili_jct"), HasCookie(cookies, "buvid3"),
            code, login, midMatch, hosts);
    }

    private static bool HasCookie(string cookies, string name) => cookies.Split(';')
        .Any(cookie => cookie.TrimStart().StartsWith(name + "=", StringComparison.Ordinal));
}

internal static class LiveChatDiagnosticFacts
{
    internal static string Boolean(bool? value) => value.HasValue ? value.Value ? "true" : "false" : "unknown";
    internal static string Code(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    internal static int? AuthenticationCode(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty || body.Length > 16384) return null;
        try
        {
            using var document = JsonDocument.Parse(body, LiveProtocol.JsonOptions);
            return LiveHttpApi.TryApiCode(document.RootElement, out var code) ? code : null;
        }
        catch (JsonException) { return null; }
    }

    internal static string? CommandName(ReadOnlyMemory<byte> body, out bool danmaku)
    {
        danmaku = false;
        if (body.IsEmpty || body.Length > LiveProtocol.MaxInflatedBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(body, LiveProtocol.JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cmd", out var property)
                || property.ValueKind != JsonValueKind.String) return null;
            var command = property.GetString();
            if (string.IsNullOrEmpty(command)) return null;
            danmaku = command == "DANMU_MSG" || command.StartsWith("DANMU_MSG:", StringComparison.Ordinal);
            var colon = command.IndexOf(':');
            var length = Math.Min(40, colon < 0 ? command.Length : colon);
            Span<char> name = stackalloc char[length];
            var written = 0;
            for (var index = 0; index < length; index++)
            {
                var character = command[index];
                if (char.IsAsciiLetterOrDigit(character) || character == '_') name[written++] = character;
            }
            return written == 0 ? null : new string(name[..written]);
        }
        catch (JsonException) { return null; }
    }
}

internal sealed class LiveChatStatistics
{
    private readonly Dictionary<string, CommandCount> _commands = new(StringComparer.Ordinal);
    private CommandCount? _commandHead;
    private long _lastReportAt = Stopwatch.GetTimestamp();

    internal long WsMessages;
    internal long Packets;
    internal long Op5;
    internal long Danmaku;
    internal long Other;
    internal long ParseNull;
    internal long QueueIn;
    internal long QueueDrop;
    internal long Dispatched;
    internal long HeartbeatSent;
    internal long HeartbeatReply;

    // Only the existing receiver writes the dictionary. Readers traverse immutable links
    // published with Volatile and drain each count with Interlocked, without a new lock.
    internal void RecordCommand(string? command)
    {
        if (command is null) return;
        if (!_commands.TryGetValue(command, out var counter))
        {
            counter = new(command, _commandHead);
            _commands.Add(command, counter);
            Volatile.Write(ref _commandHead, counter);
        }
        Interlocked.Increment(ref counter.Count);
    }

    // Heartbeat and final reports are sequential: socket cleanup joins heartbeat first.
    internal LiveChatStatisticsSnapshot? TakeSnapshot(bool final)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastReportAt, now);
        if (!final && elapsed < TimeSpan.FromSeconds(30)) return null;
        _lastReportAt = now;
        var commands = new List<(string Name, long Count)>();
        for (var counter = Volatile.Read(ref _commandHead); counter is not null; counter = counter.Next)
        {
            var count = Interlocked.Exchange(ref counter.Count, 0);
            if (count > 0) commands.Add((counter.Name, count));
        }
        var topCommands = string.Join(",", commands.OrderByDescending(command => command.Count)
            .ThenBy(command => command.Name, StringComparer.Ordinal).Take(5)
            .Select(command => command.Name + ":" + command.Count.ToString(CultureInfo.InvariantCulture)));
        return new(elapsed.TotalSeconds, Interlocked.Exchange(ref WsMessages, 0), Interlocked.Exchange(ref Packets, 0),
            Interlocked.Exchange(ref Op5, 0), Interlocked.Exchange(ref Danmaku, 0), Interlocked.Exchange(ref Other, 0),
            Interlocked.Exchange(ref ParseNull, 0), Interlocked.Exchange(ref QueueIn, 0), Interlocked.Exchange(ref QueueDrop, 0),
            Interlocked.Exchange(ref Dispatched, 0), Interlocked.Exchange(ref HeartbeatSent, 0),
            Interlocked.Exchange(ref HeartbeatReply, 0), topCommands);
    }

    private sealed class CommandCount(string name, CommandCount? next)
    {
        internal readonly string Name = name;
        internal readonly CommandCount? Next = next;
        internal long Count;
    }
}

internal sealed record LiveChatStatisticsSnapshot(double Seconds, long WsMessages, long Packets, long Op5,
    long Danmaku, long Other, long ParseNull, long QueueIn, long QueueDrop, long Dispatched,
    long HeartbeatSent, long HeartbeatReply, string TopCommands);
