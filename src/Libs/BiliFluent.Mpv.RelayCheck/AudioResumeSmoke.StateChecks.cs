// Copyright (c) Bili Copilot. All rights reserved.

using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;

namespace BiliFluent.Mpv.RelayCheck;

internal static partial class AudioResumeSmoke
{
    private static void RunRecoveryStateChecks(Report report)
    {
        var state = NewStartedState();
        report.Check("audio recovery: playing output changes do not arm recovery", !state.OutputReconfigured(false) && !state.NeedsRecovery);
        state.OutputReconfigured(true);
        var intent = state.RequestResume();
        var operation = state.PrepareResume(intent, true, true)!;
        report.Check("audio recovery: resume intent presents buffering, not user pause", state.IsResumeBuffering && !operation.Started.Task.IsCompleted);
        state.SeekStarted();
        report.Check("audio recovery: only a real seek event releases the wait", operation.Started.Task.IsCompletedSuccessfully && operation.Started.Task.Result);
        var unpauses = 0;
        state.CommitResume(intent, () => { unpauses++; return MpvError.Success; });
        report.Check("audio recovery: real seek allows one unpause and retains buffering", unpauses == 1 && state.IsResumeBuffering);
        state.PlaybackRestarted();
        report.Check("audio recovery: native synchronized restart completes buffering", !state.IsResumeBuffering && !state.NeedsRecovery);

        foreach (var action in new[] { "pause", "seek", "replace-media", "close" })
        {
            state = NewStartedState();
            state.OutputReconfigured(true);
            intent = state.RequestResume();
            operation = state.PrepareResume(intent, true, false)!;
            if (action is "replace-media" or "close") state.ResetMedia(); else state.Cancel();
            unpauses = 0;
            state.CommitResume(intent, () => { unpauses++; return MpvError.Success; });
            report.Check("audio recovery: " + action + " cancels wait and stale unpause",
                operation.Started.Task.IsCompletedSuccessfully && !operation.Started.Task.Result && unpauses == 0 && !state.IsResumeBuffering);
        }

        state = NewStartedState();
        state.OutputReconfigured(true);
        intent = state.RequestResume();
        state.Cancel();
        report.Check("audio recovery: pause during a native property read invalidates preparation", state.PrepareResume(intent, true, true) is null);
        state = NewStartedState();
        intent = state.RequestResume();
        report.Check("audio recovery: missing native PTS handles a delayed output event", state.PrepareResume(intent, true, true) is not null);
        state = NewStartedState();
        state.Enabled = false;
        report.Check("audio recovery: live opt-out does not seek", !state.OutputReconfigured(true) && state.PrepareResume(state.RequestResume(), true, true) is null);
        state = NewStartedState();
        state.OutputReconfigured(true);
        intent = state.RequestResume();
        operation = state.PrepareResume(intent, true, false)!;
        state.SeekStarted();
        state.PlaybackRestarted();
        state.CommitResume(intent, () => MpvError.Success);
        report.Check("audio recovery: paused native restart before unpause also completes", !state.IsResumeBuffering);
        state = NewStartedState();
        state.OutputReconfigured(true);
        state.RequestResume();
        report.Check("audio recovery: expired-source renewal keeps internal pause presented as buffering", state.IsResumeBuffering && state.NeedsRecovery);
        state.Cancel();
        report.Check("audio recovery: source wait can be cancelled without losing dirty output", !state.IsResumeBuffering && state.NeedsRecovery);

        state = NewStartedState();
        state.OutputReconfigured(true);
        intent = state.RequestResume();
        state.PrepareResume(intent, true, false);
        state.SeekStarted();
        state.OutputReconfigured(true);
        unpauses = 0;
        var retry = state.CommitResume(intent, () => { unpauses++; return MpvError.Success; });
        operation = state.PrepareResume(intent, true, false)!;
        report.Check("audio recovery: another paused rebuild invalidates the prepared seek", retry.Retry && unpauses == 0 && !operation.Started.Task.IsCompleted);
        state.SeekStarted();
        state.CommitResume(intent, () => { unpauses++; return MpvError.Success; });
        report.Check("audio recovery: the replacement seek restores the unpause guarantee", unpauses == 1);

        state = NewStartedState();
        state.OutputReconfigured(true);
        intent = state.RequestResume();
        state.ResetMedia();
        var seeks = 0;
        state.ExecuteSeek(intent, () => { seeks++; return MpvError.Success; });
        report.Check("audio recovery: cancelled pool work cannot seek the next media", seeks == 0);

        state = NewStartedState();
        var presentation = state.PresentationVersion;
        foreach (var action in new[] { "pause", "seek", "repeated pause" })
        {
            state.Cancel();
            report.Check("audio recovery: ordinary " + action + " retains pending state notifications", state.PresentationVersion == presentation);
        }
        intent = state.RequestResume();
        report.Check("audio recovery: ordinary resume does not invalidate presentation", state.PresentationVersion == presentation);
        report.Check("audio recovery: ordinary paused audio does not require source renewal", !state.CheckResumeRequirement(intent, true, false));
        report.Check("audio recovery: native missing PTS arms the shared preflight requirement", state.CheckResumeRequirement(intent, true, true));
        report.Check("audio recovery: resume uses the preflight requirement", state.PrepareResume(intent, true, false) is not null);
        presentation = state.PresentationVersion;
        state.Cancel();
        report.Check("audio recovery: cancelling an active wait invalidates old presentation", state.PresentationVersion > presentation);

        state = NewStartedState();
        intent = state.Intent;
        state.ResetMedia();
        state.CheckResumeRequirement(intent, true, true);
        report.Check("audio recovery: stale preflight cannot dirty a replacement media", !state.NeedsRecovery);
    }

    private static MpvAudioResumeRecovery NewStartedState()
    {
        var state = new MpvAudioResumeRecovery { Enabled = true };
        state.PlaybackRestarted();
        return state;
    }
}
