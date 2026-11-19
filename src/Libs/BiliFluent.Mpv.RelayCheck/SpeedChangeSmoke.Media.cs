// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using System.Text.Json;
using Richasy.MpvKernel.Relay;

namespace BiliFluent.Mpv.RelayCheck;

internal static partial class SpeedChangeSmoke
{
    private static readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/116.0.0.0 Safari/537.36",
        ["Referer"] = "https://www.bilibili.com",
    };

    private static async Task<Media> FetchMediaAsync()
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.bilibili.com/x/player/playurl?bvid=BV1vcsH6EEEr&cid=43620107071&fnval=4048&fourk=1&qn=80&try_look=1");
        foreach (var (name, value) in Headers) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await http.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (document.RootElement.GetProperty("code").GetInt32() != 0) throw new InvalidOperationException("Anonymous playurl failed.");
        var data = document.RootElement.GetProperty("data");
        var dash = data.GetProperty("dash");
        var video = ReadStreams(dash.GetProperty("video")).First(stream => stream.Id == 80 && stream.Codec == 7);
        var audio = ReadStreams(dash.GetProperty("audio")).OrderByDescending(stream => stream.Id).First();
        return new(video, audio, data.GetProperty("timelength").GetDouble() / 1000d);
    }

    private static List<MediaStream> ReadStreams(JsonElement list)
        => [.. list.EnumerateArray().Select(item =>
        {
            var urls = new List<string> { item.GetProperty("base_url").GetString()! };
            if (item.TryGetProperty("backup_url", out var backup) && backup.ValueKind == JsonValueKind.Array)
                urls.AddRange(backup.EnumerateArray().Select(url => url.GetString()!));
            return new MediaStream(item.GetProperty("id").GetInt32(), item.GetProperty("codecid").GetInt32(), item.GetProperty("bandwidth").GetInt64(),
                item.TryGetProperty("width", out var width) ? width.GetInt32() : 0,
                item.TryGetProperty("height", out var height) ? height.GetInt32() : 0,
                item.TryGetProperty("frame_rate", out var fps) ? fps.GetString() : null,
                [.. urls.Distinct().Select((url, index) => new RelayUpstream(url, "node" + index.ToString(CultureInfo.InvariantCulture)))]);
        })];

    private sealed record Media(MediaStream Video, MediaStream Audio, double Duration);
    private sealed record MediaStream(int Id, int Codec, long Bandwidth, int Width, int Height, string? FrameRate, List<RelayUpstream> Nodes);
}
