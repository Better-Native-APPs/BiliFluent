// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;
using System.Globalization;

namespace BiliCopilot.UI.Diagnostics;

internal enum ProcessCpuGroup
{
    Ui,
    MpvCore,
    MpvDemux,
    MpvDecoder,
    MpvAudioDecoder,
    MpvVideoOutput,
    MpvAudioOutput,
    MpvOther,
    Diagnostics,
    ThreadPool,
    Other,
}

internal readonly record struct ProcessCpuTopThread(uint Id, string? Name, long CpuTicks);

internal readonly record struct ProcessCpuInterval(double WindowMilliseconds, long TotalTicks, long UiTicks,
    long CoreTicks, long DemuxTicks, long DecoderTicks, long AudioDecoderTicks, long VideoOutputTicks, long AudioOutputTicks,
    long MpvOtherTicks, long DiagnosticTicks, long PoolTicks, long OtherTicks, ProcessCpuTopThread First, ProcessCpuTopThread Second,
    ProcessCpuTopThread Third, int Gen0, int Gen1, int Gen2, long GcPauseTicks, long HeapBytes, long WorkingSetBytes)
{
    internal bool ShouldLog => TotalTicks >= ProcessCpuDiagnostics.MinimumCpuTicks;
}

internal sealed class ProcessCpuDiagnostics
{
    internal const long MinimumCpuTicks = 50 * TimeSpan.TicksPerMillisecond;
    private Dictionary<uint, ProcessCpuThread> _previousThreads = new(64);
    private Dictionary<uint, ProcessCpuThread> _currentThreads = new(64);
    private ProcessCpuCounters? _previousCounters;

    internal ProcessCpuInterval Advance(ReadOnlySpan<ProcessCpuThread> threads, ProcessCpuCounters counters, uint uiThreadId)
    {
        _currentThreads.Clear();
        foreach (var thread in threads) _currentThreads[thread.Id] = thread;
        var result = default(ProcessCpuInterval);
        if (_previousCounters is { } previous)
        {
            Span<long> groups = stackalloc long[11];
            groups.Clear();
            var measuredTicks = 0L;
            var first = default(ProcessCpuTopThread);
            var second = default(ProcessCpuTopThread);
            var third = default(ProcessCpuTopThread);
            foreach (var thread in threads)
            {
                long ticks;
                if (_previousThreads.TryGetValue(thread.Id, out var old) && old.CreatedFileTime == thread.CreatedFileTime)
                    ticks = Math.Max(0, thread.CpuTicks - old.CpuTicks);
                else
                    // Only a thread born within this window can contribute its full lifetime CPU time.
                    ticks = thread.CreatedFileTime >= previous.CapturedFileTime ? thread.CpuTicks : 0;
                if (ticks <= 0) continue;
                var group = Classify(thread.Id, thread.Name, uiThreadId);
                groups[(int)group] += ticks;
                measuredTicks += ticks;
                if (group is ProcessCpuGroup.Diagnostics or ProcessCpuGroup.ThreadPool or ProcessCpuGroup.Other)
                {
                    var candidate = new ProcessCpuTopThread(thread.Id, thread.Name, ticks);
                    if (ComesBefore(candidate, first)) { third = second; second = first; first = candidate; }
                    else if (ComesBefore(candidate, second)) { third = second; second = candidate; }
                    else if (ComesBefore(candidate, third)) third = candidate;
                }
            }

            var total = Math.Max(0, counters.CpuTicks - previous.CpuTicks);
            // Threads born and gone between observations, or unreadable handles, remain visible in other.
            groups[(int)ProcessCpuGroup.Other] += Math.Max(0, total - measuredTicks);
            result = new(Stopwatch.GetElapsedTime(previous.Timestamp, counters.Timestamp).TotalMilliseconds,
                total, groups[0], groups[1], groups[2], groups[3], groups[4], groups[5], groups[6], groups[7], groups[8], groups[9], groups[10],
                first, second, third, Math.Max(0, counters.Gen0 - previous.Gen0), Math.Max(0, counters.Gen1 - previous.Gen1),
                Math.Max(0, counters.Gen2 - previous.Gen2), Math.Max(0, counters.GcPauseTicks - previous.GcPauseTicks),
                counters.HeapBytes, counters.WorkingSetBytes);
        }

        (_previousThreads, _currentThreads) = (_currentThreads, _previousThreads);
        _previousCounters = counters;
        return result;
    }

    internal static ProcessCpuGroup Classify(uint threadId, string name, uint uiThreadId)
    {
        if (threadId == uiThreadId) return ProcessCpuGroup.Ui;
        if (name is "PlaybackFrameProbe" or "PlaybackStallWatch") return ProcessCpuGroup.Diagnostics;
        var label = name.AsSpan();
        if (label.StartsWith("mpv/", StringComparison.OrdinalIgnoreCase)) label = label[4..];
        if (label.Equals("core", StringComparison.OrdinalIgnoreCase)) return ProcessCpuGroup.MpvCore;
        if (label.Equals("demux", StringComparison.OrdinalIgnoreCase) || label.StartsWith("demux/", StringComparison.OrdinalIgnoreCase))
            return ProcessCpuGroup.MpvDemux;
        if (label.StartsWith("av:", StringComparison.OrdinalIgnoreCase))
        {
            // FFmpeg names its decoder workers av:<codec>:dfN (frames) or av:<codec>:dsN (slices).
            var codec = label[3..];
            var separator = codec.IndexOf(':');
            if (separator >= 0) codec = codec[..separator];
            return IsAudioCodec(codec) ? ProcessCpuGroup.MpvAudioDecoder : ProcessCpuGroup.MpvDecoder;
        }
        if (label.Equals("ad", StringComparison.OrdinalIgnoreCase) || label.StartsWith("ad-", StringComparison.OrdinalIgnoreCase))
            return ProcessCpuGroup.MpvAudioDecoder;
        if (label.Equals("vd", StringComparison.OrdinalIgnoreCase) || label.StartsWith("vd-", StringComparison.OrdinalIgnoreCase)
            || label.StartsWith("avcodec", StringComparison.OrdinalIgnoreCase) || label.StartsWith("ffmpeg", StringComparison.OrdinalIgnoreCase))
            return ProcessCpuGroup.MpvDecoder;
        if (label.Equals("vo", StringComparison.OrdinalIgnoreCase) || label.StartsWith("vo/", StringComparison.OrdinalIgnoreCase))
            return ProcessCpuGroup.MpvVideoOutput;
        if (label.Equals("ao", StringComparison.OrdinalIgnoreCase) || label.StartsWith("ao/", StringComparison.OrdinalIgnoreCase))
            return ProcessCpuGroup.MpvAudioOutput;
        if (name.StartsWith("mpv/", StringComparison.OrdinalIgnoreCase)
            || label.StartsWith("lua/", StringComparison.OrdinalIgnoreCase)
            || label.Equals("curl", StringComparison.OrdinalIgnoreCase) || label.Equals("worker", StringComparison.OrdinalIgnoreCase)
            || label.Equals("dnd", StringComparison.OrdinalIgnoreCase) || label.Equals("opener", StringComparison.OrdinalIgnoreCase)
            || label.Equals("smtc/mpv", StringComparison.OrdinalIgnoreCase))
            return ProcessCpuGroup.MpvOther;
        if (name.StartsWith(".NET TP ", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".NET ThreadPool", StringComparison.OrdinalIgnoreCase))
            return ProcessCpuGroup.ThreadPool;
        return ProcessCpuGroup.Other;
    }

    internal static string Format(ProcessCpuInterval sample, int processors, bool playing, string pageTypeName,
        double samplingMilliseconds, bool stutterDiagnosticsEnabled = false)
        => string.Create(CultureInfo.InvariantCulture,
            $"Perf | cpu window-ms={sample.WindowMilliseconds:0} cpus={processors} total-ms={Milliseconds(sample.TotalTicks):0.0} ui-ms={Milliseconds(sample.UiTicks):0.0} mpv-core-ms={Milliseconds(sample.CoreTicks):0.0} mpv-demux-ms={Milliseconds(sample.DemuxTicks):0.0} mpv-dec-ms={Milliseconds(sample.DecoderTicks):0.0} mpv-adec-ms={Milliseconds(sample.AudioDecoderTicks):0.0} mpv-vo-ms={Milliseconds(sample.VideoOutputTicks):0.0} mpv-ao-ms={Milliseconds(sample.AudioOutputTicks):0.0} mpv-other-ms={Milliseconds(sample.MpvOtherTicks):0.0} diag-ms={Milliseconds(sample.DiagnosticTicks):0.0} pool-ms={Milliseconds(sample.PoolTicks):0.0} other-ms={Milliseconds(sample.OtherTicks):0.0} top=\"{FormatTop(sample)}\" gc0={sample.Gen0} gc1={sample.Gen1} gc2={sample.Gen2} gc-pause-ms={Milliseconds(sample.GcPauseTicks):0.0} heap-mb={sample.HeapBytes / 1048576d:0.0} ws-mb={sample.WorkingSetBytes / 1048576d:0.0} playing={(playing ? "true" : "false")} page={pageTypeName} stutter-diag={(stutterDiagnosticsEnabled ? "on" : "off")} sample-ms={samplingMilliseconds:0.00}");

    internal static string FormatTop(ProcessCpuInterval sample)
    {
        var builder = new StringBuilder(128);
        AppendTop(builder, sample.First);
        AppendTop(builder, sample.Second);
        AppendTop(builder, sample.Third);
        return builder.ToString();
    }

    private static void AppendTop(StringBuilder builder, ProcessCpuTopThread thread)
    {
        if (thread.CpuTicks <= 0) return;
        if (builder.Length > 0) builder.Append(',');
        if (string.IsNullOrEmpty(thread.Name)) builder.Append(thread.Id.ToString(CultureInfo.InvariantCulture));
        else
        {
            var length = Math.Min(32, thread.Name.Length);
            for (var i = 0; i < length; i++)
            {
                var value = thread.Name[i];
                builder.Append(value is < ' ' or > '~' ? '?' : value is '"' or ',' or ':' or ' ' ? '_' : value);
            }
        }
        builder.Append(':');
        builder.Append(Milliseconds(thread.CpuTicks).ToString("0.0", CultureInfo.InvariantCulture));
    }

    private static double Milliseconds(long ticks) => ticks / (double)TimeSpan.TicksPerMillisecond;

    private static bool IsAudioCodec(ReadOnlySpan<char> codec)
        => codec.Equals("aac", StringComparison.OrdinalIgnoreCase) || codec.Equals("aac_fixed", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("ac3", StringComparison.OrdinalIgnoreCase) || codec.Equals("ac3_fixed", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("eac3", StringComparison.OrdinalIgnoreCase) || codec.Equals("dca", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("truehd", StringComparison.OrdinalIgnoreCase) || codec.Equals("mlp", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("mp3", StringComparison.OrdinalIgnoreCase) || codec.Equals("mp3float", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("mp2", StringComparison.OrdinalIgnoreCase) || codec.Equals("mp2float", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("flac", StringComparison.OrdinalIgnoreCase) || codec.Equals("alac", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("opus", StringComparison.OrdinalIgnoreCase) || codec.Equals("libopus", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("vorbis", StringComparison.OrdinalIgnoreCase) || codec.Equals("libvorbis", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("wavpack", StringComparison.OrdinalIgnoreCase) || codec.Equals("ape", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("wmapro", StringComparison.OrdinalIgnoreCase) || codec.Equals("wmav1", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("wmav2", StringComparison.OrdinalIgnoreCase) || codec.Equals("wmalossless", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("tta", StringComparison.OrdinalIgnoreCase) || codec.Equals("tak", StringComparison.OrdinalIgnoreCase)
            || codec.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("adpcm_", StringComparison.OrdinalIgnoreCase);

    private static bool ComesBefore(ProcessCpuTopThread candidate, ProcessCpuTopThread existing)
        => candidate.CpuTicks > existing.CpuTicks || candidate.CpuTicks == existing.CpuTicks && candidate.Id < existing.Id;
}
