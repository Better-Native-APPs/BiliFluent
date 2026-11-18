// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using System.Text.Json;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Core.Enums;
using Richasy.MpvKernel.Core.Models;
using Richasy.MpvKernel.Relay;

namespace BiliFluent.Mpv.RelayCheck;

internal static partial class AudioResumeSmoke
{
    private static async Task RunNativeResumeIntentChecksAsync(Report report, string dll, string output, Dictionary<string, string> options)
    {
        var media = await FetchAsync(options.GetValueOrDefault("bvid", "BV1vcsH6EEEr"), options.GetValueOrDefault("cid", "43620107071"), "80");
        using var trace = new TraceLogger(Path.Combine(output, "native-resume-intent.events.log"));
        await using var relay = LoopbackMediaRelay.Start(logger: trace);
        var videoUrl = relay.Register("video", new() { Upstreams = media.Video.Urls.Select((url, i) => new RelayUpstream(url, "v" + i)).ToArray(), Headers = Headers });
        var audioNodes = media.Audio.Urls.Select((url, i) => new RelayUpstream(url, "a" + i)).ToArray();
        var audioSource = relay.Register("audio-source", new() { Upstreams = audioNodes, Headers = Headers });
        await using var delayed = await DelayedAudioNode.CreateAsync(audioSource, media.Audio, 1);
        var audioUrl = relay.Register("audio", new() { Upstreams = audioNodes, Headers = Headers });
        var seekAudioUrl = relay.Register("seek-audio", new() { Upstreams = [new(delayed.Url, "delayed-audio")], Headers = Headers });
        await using var client = await MpvClient.CreateAsync(dll, new() { UseConfig = false, LoadScripts = false }, trace);
        client.EnablePausedAudioOutputRecovery = true;
        foreach (var (name, value) in new (string, string)[]
        {
            ("vo", "null"), ("ao", "wasapi"), ("mute", "yes"), ("volume", "0"), ("audio-exclusive", "no"),
            ("hwdec", "no"), ("ytdl", "no"), ("cookies", "no"), ("terminal", "no"), ("idle", "yes"),
            ("cache", "yes"), ("demuxer-max-bytes", "64MiB"), ("demuxer-max-back-bytes", "0"), ("demuxer-readahead-secs", "60"),
            ("cache-pause", "yes"), ("cache-pause-wait", "3"), ("keep-open", "yes"), ("tls-verify", "yes"),
            ("log-file", Path.Combine(output, "native-resume-intent.mpv.log")),
            ("msg-level", "all=warn,cplayer=v,ao=v,ao/wasapi=v,demux=v,ad=v,af=v,ffmpeg/audio=v"),
        }) Ensure(client.SetOptionString(name, value), name);

        MpvCacheStateEventArgs? cache = null;
        var seekCount = 0;
        var restartCount = 0;
        var resumeOnSeek = 0;
        var resumeSeekingError = MpvError.Success;
        var resumeSeeking = false;
        var resumeBeforeRestart = false;
        var requestedResume = new TaskCompletionSource<Task<MpvResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.CacheStateChanged += (_, state) => Volatile.Write(ref cache, state);
        client.DataNotify += (_, args) =>
        {
            if (args.Id == MpvClientEventId.PlaybackRestart) Interlocked.Increment(ref restartCount);
        };
        client.SeekStarted += (_, _) =>
        {
            Interlocked.Increment(ref seekCount);
            if (Interlocked.Exchange(ref resumeOnSeek, 0) == 0) return;
            resumeSeekingError = client.GetProperty("seeking", out resumeSeeking);
            resumeBeforeRestart = Volatile.Read(ref restartCount) == 0;
            requestedResume.TrySetResult(client.ResumeAsync());
        };
        await client.PlayAsync(videoUrl, new() { AudioTracks = [audioUrl], EnableCookies = false, EnableYtdl = false });
        if (!await WaitAsync(() => ReadDouble(client, "time-pos") > 0.2 && double.IsFinite(ReadDouble(client, "audio-pts"))
            && Volatile.Read(ref restartCount) > 0 && Volatile.Read(ref cache) is not null, 30_000))
            throw new InvalidOperationException("Muted dual-stream fixture did not start.");
        report.Check("native intent: real dual DASH starts on muted WASAPI", Flag(client, "mute") && client.GetPropertyString("current-ao") == "wasapi");

        Ensure(await client.PauseAsync(), "native intent pause");
        var pausedPosition = ReadDouble(client, "time-pos");
        Ensure(client.Command("ao-reload"), "native intent output reload");
        if (!await WaitAsync(() => client.NeedsPausedAudioOutputRecovery && !double.IsFinite(ReadDouble(client, "audio-pts")), 5_000))
            throw new InvalidOperationException("Paused output reconstruction was not observed.");
        var rebuildSeekingError = client.GetProperty("seeking", out bool rebuildSeeking);
        report.Note($"NATIVE_INTENT paused-rebuild seeking-error={rebuildSeekingError} seeking={rebuildSeeking}");
        report.Check("native intent: paused output reconstruction has completed seeking", rebuildSeekingError == MpvError.Success && !rebuildSeeking);
        if (rebuildSeekingError != MpvError.Success || rebuildSeeking) return;
        var beforeRebuildSeek = Volatile.Read(ref seekCount);
        var beforeRebuildRestart = Volatile.Read(ref restartCount);
        Ensure(await client.ResumeAsync(), "native intent rebuild resume");
        var rebuilt = await WaitAsync(() => Volatile.Read(ref restartCount) > beforeRebuildRestart && !Flag(client, "seeking") && ReadDouble(client, "time-pos") > pausedPosition + 0.1
            && double.IsFinite(ReadDouble(client, "audio-pts")), 30_000);
        var rebuildSeeks = Volatile.Read(ref seekCount) - beforeRebuildSeek;
        report.Check("native intent: paused reconstruction still issues one synchronized seek", rebuilt && rebuildSeeks == 1);

        // Only the unfinished-seek fixture withholds audio. The ordinary reconstruction
        // control must not inherit an upstream that is intentionally waiting for release.
        Ensure(await client.PauseAsync(), "native intent seek pause");
        var beforeSeekLoadRestart = Volatile.Read(ref restartCount);
        Volatile.Write(ref cache, null);
        await client.PlayAsync(videoUrl, new() { AudioTracks = [seekAudioUrl], EnableCookies = false, EnableYtdl = false });
        Ensure(await client.ResumeAsync(), "native intent load seek fixture");
        if (!await WaitAsync(() => ReadDouble(client, "time-pos") > 0.2 && double.IsFinite(ReadDouble(client, "audio-pts"))
            && Volatile.Read(ref restartCount) > beforeSeekLoadRestart && Volatile.Read(ref cache) is not null, 30_000))
            throw new InvalidOperationException("Muted unfinished-seek fixture did not start.");
        Ensure(await client.PauseAsync(), "native intent seek fixture pause");
        const double target = 550;
        var ranges = Volatile.Read(ref cache)?.SeekableRanges;
        var outsideCache = ranges is not null && !ranges.Any(range => range.Start <= target && range.End >= target);
        report.Check("native intent: absolute seek target is outside the video cache", outsideCache);
        if (!outsideCache) return;
        var beforeAbsoluteSeek = Volatile.Read(ref seekCount);
        Interlocked.Exchange(ref restartCount, 0);
        Interlocked.Exchange(ref resumeOnSeek, 1);
        delayed.StallFor(TimeSpan.FromSeconds(5));
        trace.Mark("absolute-seek-resume target=550", client);
        Ensure(client.Command("seek", target.ToString(CultureInfo.InvariantCulture), "absolute+exact"), "native intent absolute seek");
#pragma warning disable VSTHRD003 // The fixture owns this callback's exact resume operation and waits without a UI context.
        var resume = await requestedResume.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(await resume.WaitAsync(TimeSpan.FromSeconds(5)), "native intent resume during seek");
#pragma warning restore VSTHRD003
        report.Note($"NATIVE_INTENT resume-during-seek seeking-error={resumeSeekingError} seeking={resumeSeeking} before-restart={resumeBeforeRestart}");
        report.Check("native intent: resume is requested during the unfinished native seek", resumeBeforeRestart && resumeSeekingError == MpvError.Success && resumeSeeking);
        var advanced = await WaitAsync(() => Volatile.Read(ref restartCount) > 0 && !Flag(client, "seeking")
            && ReadDouble(client, "time-pos") > target + 0.1 && double.IsFinite(ReadDouble(client, "audio-pts")), 30_000);
        var absoluteSeeks = Volatile.Read(ref seekCount) - beforeAbsoluteSeek;
        report.Check("native intent: unfinished absolute seek resumes with exactly one SEEK", advanced && absoluteSeeks == 1);
        report.Check("native intent: both intent fixtures remain muted", Flag(client, "mute") && client.GetPropertyString("current-ao") == "wasapi");
        await File.WriteAllTextAsync(Path.Combine(output, "native-resume-intent.json"), JsonSerializer.Serialize(new
        {
            RebuildSeekingError = rebuildSeekingError.ToString(), RebuildSeeking = rebuildSeeking, RebuildSeekEvents = rebuildSeeks,
            ResumeSeekingError = resumeSeekingError.ToString(), ResumeSeeking = resumeSeeking, ResumeBeforeRestart = resumeBeforeRestart,
            Target = target, TargetOutsideVideoCache = outsideCache, AbsoluteSeekEvents = absoluteSeeks, PlaybackAdvanced = advanced,
            AudioBodyStalls = delayed.StalledBodies, Muted = Flag(client, "mute"),
        }, JsonOptions));
    }
}
