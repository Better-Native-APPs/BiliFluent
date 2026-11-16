// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;
using Richasy.MpvKernel.Dash;
using Richasy.MpvKernel.Player.Models;

namespace Richasy.MpvKernel.Player;

public sealed partial class MpvPlayer
{
    private static double SeamlessVideoLead(double speed) => speed <= 1 ? speed : 1 + (1.5 * (speed - 1));

    private static int FindSeamlessVideoFragment(DashIndex index, double position, double speed)
    {
        var minimum = position + SeamlessVideoLead(speed);
        for (var i = index.FragmentAt(minimum); i < index.Fragments.Count; i++)
        {
            if (index.Fragments[i].Time >= minimum && index.Fragments[i].StartsWithKeyFrame) return i;
        }

        return -1;
    }

    private bool ReadSeamlessTimeline(out double position, out double speed)
    {
        position = 0;
        speed = 0;
        return !Client.IsDisposed && Client.GetProperty("time-pos", out position) == MpvError.Success && double.IsFinite(position) && position >= 0
            && Client.GetProperty("speed", out speed) == MpvError.Success && double.IsFinite(speed) && speed > 0
            && Client.GetProperty("duration", out double duration) == MpvError.Success && double.IsFinite(duration) && duration > position
            && Client.GetProperty("core-idle", out bool idle) == MpvError.Success && !idle;
    }

    private void CheckSeamlessContinuity(SeamlessOperation operation, out double position)
    {
        operation.Token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(operation.MainSource, _relayCurrent)) operation.Cancel(MpvSeamlessSwitchReason.PlaybackChanged);
        if (Client.GetProperty("pause", out bool paused) == MpvError.Success && paused) operation.Cancel(MpvSeamlessSwitchReason.Paused);
        if (Client.GetProperty("seeking", out bool seeking) == MpvError.Success && seeking) operation.Cancel(MpvSeamlessSwitchReason.Seeked);
        if (!ReadSeamlessTimeline(out position, out var speed)) operation.Cancel(MpvSeamlessSwitchReason.PlaybackChanged);
        if (Math.Abs(speed - operation.Speed) > 0.001) operation.Cancel(MpvSeamlessSwitchReason.SpeedChanged);
        operation.Token.ThrowIfCancellationRequested();
    }

    private Task<(MpvSeamlessSwitchReason Reason, double Position)> WaitSeamlessBoundaryAsync(SeamlessOperation operation)
        => Task.Run(() => WaitSeamlessBoundary(operation), operation.Token);

    private (MpvSeamlessSwitchReason Reason, double Position) WaitSeamlessBoundary(SeamlessOperation operation)
    {
        var clock = Stopwatch.StartNew();
        CheckSeamlessContinuity(operation, out var previous);
        var previousAt = clock.Elapsed.TotalSeconds;
        var changedAt = previousAt;
        var checkedAt = previousAt;
        try
        {
            while (clock.Elapsed.TotalSeconds < 15)
            {
                operation.Token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(operation.MainSource, _relayCurrent)) operation.Cancel(MpvSeamlessSwitchReason.PlaybackChanged);
                if (Client.GetProperty("time-pos", out double position) != MpvError.Success)
                    operation.Cancel(MpvSeamlessSwitchReason.PlaybackChanged);
                var now = clock.Elapsed.TotalSeconds;
                // Events invalidate the operation immediately. Do not serialize six synchronous
                // native property reads per tick: that can skip a safe window at high speed.
                if (now - checkedAt >= 0.1)
                {
                    CheckSeamlessContinuity(operation, out position);
                    now = clock.Elapsed.TotalSeconds;
                    checkedAt = now;
                }

                operation.Token.ThrowIfCancellationRequested();
                if (position < previous - 0.01 || Math.Abs(position - previous - ((now - previousAt) * operation.Speed)) > 0.2)
                {
                    operation.Cancel(MpvSeamlessSwitchReason.Seeked);
                    operation.Token.ThrowIfCancellationRequested();
                }

                var delta = position - operation.Boundary!.Value;
                if (Math.Abs(position - previous) > 0.00001) changedAt = now;
                // time-pos is frame-quantized. Interpolate by at most one 30 fps frame, then
                // still require the *native* position to be inside the accepted safe window.
                var estimatedDelta = delta + Math.Min((now - changedAt) * operation.Speed, 1d / 30);
                if (delta > 0.03) return (MpvSeamlessSwitchReason.MissedBoundary, position);
                if (delta >= -0.06 && estimatedDelta >= -0.02)
                {
                    if (Client.GetProperty("pause", out bool paused) == MpvError.Success && paused) operation.Cancel(MpvSeamlessSwitchReason.Paused);
                    if (Client.GetProperty("speed", out double speed) == MpvError.Success && Math.Abs(speed - operation.Speed) > 0.001) operation.Cancel(MpvSeamlessSwitchReason.SpeedChanged);
                    operation.Token.ThrowIfCancellationRequested();
                    return (MpvSeamlessSwitchReason.None, position);
                }

                previous = position;
                previousAt = now;
                // Stopwatch-driven final scheduling avoids timer-resolution/occlusion rounding.
                // Busy waits are limited to the final window and run only on this worker.
                if ((-estimatedDelta / operation.Speed) > 0.06) Thread.Sleep(5);
                else Thread.SpinWait(128);
            }

            return (MpvSeamlessSwitchReason.WaitTimeout, previous);
        }
        finally
        {
            operation.WaitElapsed = clock.Elapsed;
        }
    }
}
