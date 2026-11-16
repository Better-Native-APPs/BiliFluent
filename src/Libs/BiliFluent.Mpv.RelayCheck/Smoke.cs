// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Richasy.MpvKernel.Relay;

namespace BiliFluent.Mpv.RelayCheck;

/// <summary>
/// Plays a public stream through the relay with a libmpv that has no window and no sound, and
/// watches the playback position while the relay changes the node behind it. No account is
/// used: the play URLs are the ones an anonymous visitor gets.
/// </summary>
internal static class Smoke
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/116.0.0.0 Safari/537.36 Edg/116.0.1938.69";
    private const string Referer = "https://www.bilibili.com";

    public static async Task RunAsync(Report report, Dictionary<string, string> options)
    {
        if (!options.TryGetValue("dll", out var dll) || !File.Exists(dll))
        {
            report.Check("smoke: dll=<path to libmpv-2.dll> is required", false);
            return;
        }

        var bvid = options.GetValueOrDefault("bvid", "BV1vcsH6EEEr");
        var cid = options.GetValueOrDefault("cid", "43620107071");
        var verifyTls = !string.Equals(options.GetValueOrDefault("tls", "on"), "off", StringComparison.OrdinalIgnoreCase);
        var headers = new Dictionary<string, string> { ["User-Agent"] = UserAgent, ["Referer"] = Referer };

        var media = await FetchAsync(bvid, cid);
        var video = media.Video.Where(stream => stream.Codec == 7).OrderByDescending(stream => stream.Id).First();
        var otherVideo = media.Video.First(stream => stream.Id != video.Id);
        var audio = media.Audio.OrderByDescending(stream => stream.Id).First();
        report.Note(string.Create(CultureInfo.InvariantCulture, $"video qn={video.Id} kbps={video.Bandwidth / 1000} nodes={video.Urls.Count}; audio id={audio.Id} nodes={audio.Urls.Count}; duration={media.Duration:0}s tls-verify={verifyTls}"));
        report.Check("smoke: the streams have more than one node", video.Urls.Count > 1 && audio.Urls.Count > 1);

        // The last address of each table cannot be connected to.
        var deadVideo = new RelayUpstream("https://127.0.0.1:9/dead-video", "dead");
        var deadAudio = new RelayUpstream("https://127.0.0.1:9/dead-audio", "dead");
        var videoNodes = video.Urls.Select((url, i) => new RelayUpstream(url, "v" + i)).Append(deadVideo).ToList();
        var audioNodes = audio.Urls.Select((url, i) => new RelayUpstream(url, "a" + i)).Append(deadAudio).ToList();

        await using var relay = LoopbackMediaRelay.Start(new() { VerifyTls = verifyTls });
        var failures = new ConcurrentQueue<RelayUpstreamFailedEventArgs>();
        relay.UpstreamFailed += (_, e) =>
        {
            failures.Enqueue(e);
            Console.WriteLine($"     relay: {e.Key} gave up {e.Failed.Label} ({e.Reason}), now {e.Current.Label}");
        };
        var videoUrl = relay.Register("v1", new() { Upstreams = videoNodes, Headers = headers });
        var audioUrl = relay.Register("a1", new() { Upstreams = audioNodes, Headers = headers });

        using var mpv = new Mpv(dll);
        foreach (var (name, value) in new (string, string)[]
        {
            ("vo", "null"), ("ao", "null"), ("idle", "yes"), ("ytdl", "no"), ("terminal", "no"), ("config", "no"),
            ("hwdec", "no"), ("cache", "yes"), ("demuxer-max-bytes", "24MiB"), ("demuxer-max-back-bytes", "0"),
            ("demuxer-readahead-secs", "15"), ("cache-pause-wait", "3"), ("keep-open", "yes"),

            // Twenty seconds ahead at most, so that both streams are still being read when their node changes.
            ("cache-secs", "20"),
        })
        {
            mpv.SetOption(name, value);
        }

        mpv.Initialize();
        mpv.Command("loadfile", videoUrl, "replace", "0", "audio-file=\"" + audioUrl + "\"");

        var started = Stopwatch.StartNew();
        report.Check("smoke: playback starts through the relay", await WaitAsync(() => mpv.GetDouble("time-pos") > 2, 40_000), $"{started.ElapsedMilliseconds} ms to time-pos 2");
        report.Check("smoke: both tracks are there", mpv.GetString("track-list/count") == "2", "tracks=" + mpv.GetString("track-list/count"));
        Describe(report, relay);

        // Seek: the player closes its connections and asks for other ranges.
        var requests = relay.GetStatistics("v1")?.Requests ?? 0;
        mpv.Command("seek", "200", "absolute");
        started.Restart();
        report.Check("smoke: seek", await WaitAsync(() => mpv.GetDouble("time-pos") is > 201 and < 215, 30_000), $"time-pos={mpv.GetDouble("time-pos"):0.00} after {started.ElapsedMilliseconds} ms");
        report.Check("smoke: the seek was a new range request", relay.GetStatistics("v1")?.Requests > requests);

        var baseline = await WatchAsync(mpv, 4000);
        report.Note("steady state: " + baseline);

        // The node behind the video changes, then the one behind the audio.
        await SwitchAsync(report, mpv, relay, "v1", videoNodes, baseline, "video");
        await SwitchAsync(report, mpv, relay, "a1", audioNodes, baseline, "audio");

        // A node that cannot be connected to: the relay leaves it by itself.
        var before = failures.Count;
        relay.SwitchNode("v1", deadVideo.Url);
        relay.SwitchNode("a1", deadAudio.Url);
        var dead = await WatchAsync(mpv, 8000);
        report.Check("smoke: a dead node is left without the player noticing", dead.IsSmooth(baseline) && failures.Count >= before + 2
            && relay.GetStatistics("v1")?.Current.Label != "dead" && relay.GetStatistics("a1")?.Current.Label != "dead", dead.ToString());

        // Play URLs that were issued again replace the table; another quality is not the same file.
        var again = await FetchAsync(bvid, cid);
        var sameVideo = again.Video.First(stream => stream.Id == video.Id && stream.Codec == video.Codec);
        var sameAudio = again.Audio.First(stream => stream.Id == audio.Id);
        report.Equal("smoke: another quality is refused as new addresses", RelayUpdateResult.LengthMismatch,
            await relay.UpdateUrlsAsync("v1", [.. otherVideo.Urls.Select((url, i) => new RelayUpstream(url, "x" + i))], 0, headers));
        var updateVideo = await relay.UpdateUrlsAsync("v1", [.. sameVideo.Urls.Select((url, i) => new RelayUpstream(url, "nv" + i))], 0, headers);
        var updateAudio = await relay.UpdateUrlsAsync("a1", [.. sameAudio.Urls.Select((url, i) => new RelayUpstream(url, "na" + i))], 0, headers);
        var updated = await WatchAsync(mpv, 4000);
        report.Check("smoke: fresh play urls replace the table", updateVideo == RelayUpdateResult.Updated && updateAudio == RelayUpdateResult.Updated
            && updated.IsSmooth(baseline) && relay.GetStatistics("v1")?.Current.Label.StartsWith("nv", StringComparison.Ordinal) == true, $"{updateVideo}/{updateAudio} {updated}");
        mpv.Command("seek", "420", "absolute");
        report.Check("smoke: seek after the update", await WaitAsync(() => mpv.GetDouble("time-pos") is > 421 and < 435, 30_000), $"time-pos={mpv.GetDouble("time-pos"):0.00}");
        Describe(report, relay);

        mpv.Command("stop");
        started.Restart();
        mpv.Dispose();
        await relay.DisposeAsync();
        report.Check("smoke: shutdown", started.ElapsedMilliseconds < 8000, started.ElapsedMilliseconds + " ms");
    }

    private static async Task SwitchAsync(Report report, Mpv mpv, LoopbackMediaRelay relay, string key, List<RelayUpstream> nodes, Watch baseline, string name)
    {
        var stats = relay.GetStatistics(key)!.Value;
        var current = nodes.FindIndex(node => node.Url == stats.Current.Url);
        var target = nodes[(current + 1) % (nodes.Count - 1)];
        var accepted = relay.SwitchNode(key, target.Url);
        var watch = await WatchAsync(mpv, 6000);
        var after = relay.GetStatistics(key)!.Value;
        report.Check($"smoke: {name} node switch is not noticed by the player", accepted && watch.IsSmooth(baseline), watch.ToString());
        var handedOver = after.Handovers > stats.Handovers;
        report.Check($"smoke: {name} stream goes on from the other node", after.Current.Label != stats.Current.Label && (handedOver || after.ActiveRequests == 0),
            string.Create(CultureInfo.InvariantCulture, $"{stats.Current.Label} -> {after.Current.Label} handover={handedOver} active={after.ActiveRequests} ttfb={after.FirstByteTime?.TotalMilliseconds:0}ms bytes+={(after.UpstreamBytes - stats.UpstreamBytes) / 1024}KiB"));
    }

    private static void Describe(Report report, LoopbackMediaRelay relay)
    {
        foreach (var key in new[] { "v1", "a1" })
        {
            if (relay.GetStatistics(key) is { } s)
            {
                report.Note(string.Create(CultureInfo.InvariantCulture, $"{key}: node={s.Current.Label} requests={s.Requests} active={s.ActiveRequests} bytes={s.UpstreamBytes / 1024}KiB rate={s.BytesPerSecond / 1024:0}KiB/s ttfb={s.FirstByteTime?.TotalMilliseconds:0}ms length={s.Length} failovers={s.Failovers} handovers={s.Handovers}"));
            }
        }
    }

    // Samples the position every 5 ms for a while.
    private static async Task<Watch> WatchAsync(Mpv mpv, int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        var first = mpv.GetDouble("time-pos");
        var last = first;
        var lastChange = 0d;
        var longest = 0d;
        var backwards = false;
        var paused = false;
        while (watch.Elapsed.TotalMilliseconds < milliseconds)
        {
            await Task.Delay(5);
            var now = watch.Elapsed.TotalMilliseconds;
            var position = mpv.GetDouble("time-pos");
            paused |= mpv.GetString("paused-for-cache") == "yes";
            if (position != last)
            {
                backwards |= position < last;
                longest = Math.Max(longest, now - lastChange);
                lastChange = now;
                last = position;
            }
        }

        longest = Math.Max(longest, watch.Elapsed.TotalMilliseconds - lastChange);
        return new(longest, last - first, watch.Elapsed.TotalSeconds, backwards, paused);
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > milliseconds)
            {
                return false;
            }

            await Task.Delay(20);
        }

        return true;
    }

    private static async Task<(List<DashStream> Video, List<DashStream> Audio, double Duration)> FetchAsync(string bvid, string cid)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.bilibili.com/x/player/playurl?bvid={bvid}&cid={cid}&fnval=4048&fourk=1&qn=127&try_look=1");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", Referer);
        using var response = await http.SendAsync(request);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (document.RootElement.GetProperty("code").GetInt32() != 0)
        {
            throw new InvalidOperationException("playurl code " + document.RootElement.GetProperty("code").GetInt32());
        }

        var data = document.RootElement.GetProperty("data");
        var dash = data.GetProperty("dash");
        return (Read(dash.GetProperty("video")), Read(dash.GetProperty("audio")), data.GetProperty("timelength").GetInt64() / 1000d);

        static List<DashStream> Read(JsonElement list)
            => [.. list.EnumerateArray().Select(item =>
            {
                var urls = new List<string> { item.GetProperty("base_url").GetString()! };
                if (item.TryGetProperty("backup_url", out var backup) && backup.ValueKind == JsonValueKind.Array)
                {
                    urls.AddRange(backup.EnumerateArray().Select(url => url.GetString()!));
                }

                return new DashStream(item.GetProperty("id").GetInt32(), item.GetProperty("codecid").GetInt32(), item.GetProperty("bandwidth").GetInt64(), [.. urls.Distinct()]);
            })];
    }

    private sealed record DashStream(int Id, int Codec, long Bandwidth, List<string> Urls);

    /// <summary>
    /// What the position did during a stretch of playback.
    /// </summary>
    private readonly record struct Watch(double LongestStallMs, double Advanced, double Seconds, bool Backwards, bool PausedForCache)
    {
        // The position moved on like the clock, never back, never paused for the cache, and it
        // never stood still noticeably longer than it does when nothing is switched.
        public bool IsSmooth(Watch baseline)
            => !Backwards && !PausedForCache && Math.Abs(Advanced - Seconds) < 0.25 && LongestStallMs <= Math.Max(100, baseline.LongestStallMs + 40);

        public override string ToString()
            => string.Create(CultureInfo.InvariantCulture, $"longest-stall={LongestStallMs:0}ms advanced={Advanced:0.000}s in {Seconds:0.000}s backwards={Backwards} paused-for-cache={PausedForCache}");
    }

    /// <summary>
    /// The few libmpv calls the smoke needs.
    /// </summary>
    private sealed unsafe class Mpv : IDisposable
    {
        private const string Library = "libmpv-2";
        private static nint _library;
        private nint _handle;

        public Mpv(string path)
        {
            if (_library == 0)
            {
                _library = NativeLibrary.Load(path);
                NativeLibrary.SetDllImportResolver(typeof(Mpv).Assembly, (name, _, _) => name == Library ? _library : 0);
            }

            _handle = mpv_create();
            if (_handle == 0)
            {
                throw new InvalidOperationException("mpv_create failed");
            }
        }

        public void SetOption(string name, string value) => Check(mpv_set_option_string(_handle, name, value), name);

        public void Initialize() => Check(mpv_initialize(_handle), "initialize");

        public void Command(params string[] args)
        {
            var pointers = new nint[args.Length + 1];
            try
            {
                for (var i = 0; i < args.Length; i++)
                {
                    pointers[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
                }

                fixed (nint* list = pointers)
                {
                    Check(mpv_command(_handle, list), args[0]);
                }
            }
            finally
            {
                foreach (var pointer in pointers)
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }

        public string? GetString(string name)
        {
            var pointer = mpv_get_property_string(_handle, name);
            if (pointer == 0)
            {
                return null;
            }

            var value = Marshal.PtrToStringUTF8(pointer);
            mpv_free(pointer);
            return value;
        }

        public double GetDouble(string name)
            => double.TryParse(GetString(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;

        public void Dispose()
        {
            if (_handle != 0)
            {
                mpv_terminate_destroy(_handle);
                _handle = 0;
            }
        }

        private static void Check(int error, string what)
        {
            if (error < 0)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"mpv {what} failed: {error}"));
            }
        }

        [DllImport(Library)]
        private static extern nint mpv_create();

        [DllImport(Library)]
        private static extern int mpv_initialize(nint handle);

        [DllImport(Library)]
        private static extern void mpv_terminate_destroy(nint handle);

        [DllImport(Library)]
        private static extern int mpv_set_option_string(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [DllImport(Library)]
        private static extern nint mpv_get_property_string(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Library)]
        private static extern void mpv_free(nint data);

        [DllImport(Library)]
        private static extern int mpv_command(nint handle, nint* args);
    }
}
