// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;

namespace BiliFluent.Mpv.RelayCheck;

internal static partial class SeamlessSmoke
{
    private readonly record struct Sample(double Milliseconds, double Position, double AudioPts, bool CoreIdle, bool PausedForCache);

    private sealed record Metrics(double CoreGapMs, double AudioGapMs, double PositionJumpMs, bool Buffered, bool CoreIdle, double LargestSampleGapMs, int Samples)
    {
        public bool IsSmooth => Samples > 20 && CoreGapMs <= 100 && AudioGapMs <= 100 && PositionJumpMs <= 1000d / 30 && !Buffered && !CoreIdle;

        public override string ToString()
            => string.Create(CultureInfo.InvariantCulture, $"core-gap={CoreGapMs:0.0}ms audio-gap={AudioGapMs:0.0}ms jump={PositionJumpMs:0.0}ms sample-gap={LargestSampleGapMs:0.0}ms cache={Buffered} idle={CoreIdle}");
    }

    private sealed class Sampler : IDisposable
    {
        private static readonly string[] CsvHeader = ["wall_ms,time_pos,audio_pts,core_idle,paused_for_cache"];
        private readonly ConcurrentQueue<Sample> _samples = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Thread _thread;
        private bool _stop;

        public Sampler(MpvClient client)
        {
            _thread = new(() =>
            {
                while (!Volatile.Read(ref _stop))
                {
                    var started = Now;
                    var position = Position(client);
                    var now = Now;
                    var audio = client.GetProperty("audio-pts", out double pts) == MpvError.Success ? pts : double.NaN;
                    _samples.Enqueue(new(now, position, audio, Flag(client, "core-idle"), Flag(client, "paused-for-cache")));
                    // Diagnostic-only 4 ms cadence, rather than Sleep(4) plus synchronous native
                    // query time. Record actual sampling intervals; do not assume they are 4 ms.
                    while (!Volatile.Read(ref _stop) && Now - started < 4) Thread.SpinWait(64);
                }
            }) { IsBackground = true, Name = "seamless-check-sampler" };
            _thread.Start();
        }

        public double Now => _clock.Elapsed.TotalMilliseconds;

        public Metrics Measure(double from, double to, double speed)
        {
            var selected = _samples.Where(sample => sample.Milliseconds >= from && sample.Milliseconds <= to).ToArray();
            if (selected.Length < 2) return new(0, 0, 0, false, false, 0, selected.Length);
            var coreChange = selected[0].Milliseconds;
            var audioChange = coreChange;
            double coreGap = 0, audioGap = 0, jump = 0, sampleGap = 0;
            var previous = selected[0];
            var changedPosition = previous.Position;
            var changedPositionAt = previous.Milliseconds;
            foreach (var sample in selected.Skip(1))
            {
                var elapsed = sample.Milliseconds - previous.Milliseconds;
                sampleGap = Math.Max(sampleGap, elapsed);
                if (double.IsFinite(sample.Position) && double.IsFinite(previous.Position))
                {
                    coreGap = Math.Max(coreGap, sample.Milliseconds - coreChange);
                    if (Math.Abs(sample.Position - previous.Position) > 0.00001)
                    {
                        coreChange = sample.Milliseconds;
                        // Compare timestamp advances to the wall time between *changes*, not
                        // adjacent samples: repeated frame PTS values are cadence, not a seek.
                        jump = Math.Max(jump, Math.Abs((sample.Position - changedPosition) * 1000 - ((sample.Milliseconds - changedPositionAt) * speed)));
                        changedPosition = sample.Position;
                        changedPositionAt = sample.Milliseconds;
                    }
                }
                else
                {
                    coreGap = Math.Max(coreGap, sample.Milliseconds - coreChange);
                }

                audioGap = Math.Max(audioGap, sample.Milliseconds - audioChange);
                if (double.IsFinite(sample.AudioPts) && double.IsFinite(previous.AudioPts) && Math.Abs(sample.AudioPts - previous.AudioPts) > 0.00001)
                    audioChange = sample.Milliseconds;
                previous = sample;
            }

            return new(coreGap, audioGap, jump, selected.Any(sample => sample.PausedForCache), selected.Any(sample => sample.CoreIdle), sampleGap, selected.Length);
        }

        public Task WriteAsync(string path)
            => File.WriteAllLinesAsync(path, CsvHeader
                .Concat(_samples.Select(sample => string.Create(CultureInfo.InvariantCulture,
                    $"{sample.Milliseconds:0.000},{sample.Position:0.000000},{sample.AudioPts:0.000000},{sample.CoreIdle},{sample.PausedForCache}"))));

        public void Dispose()
        {
            Volatile.Write(ref _stop, true);
            _thread.Join();
        }
    }
}
