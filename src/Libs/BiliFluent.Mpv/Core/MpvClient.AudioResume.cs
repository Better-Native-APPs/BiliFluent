// Copyright (c) Bili Copilot. All rights reserved.

namespace Richasy.MpvKernel.Core;

public sealed partial class MpvClient
{
    private readonly MpvAudioResumeRecovery _audioResumeRecovery = new();

    /// <summary>
    /// Enables synchronized recovery of seekable on-demand media after paused output reconstruction.
    /// Live playback leaves this disabled and resolves a new live edge instead.
    /// </summary>
    public bool EnablePausedAudioOutputRecovery
    {
        get => _audioResumeRecovery.Enabled;
        set => _audioResumeRecovery.Enabled = value;
    }

    /// <summary>
    /// Whether the paused output has discarded samples and needs a synchronized restart.
    /// </summary>
    public bool NeedsPausedAudioOutputRecovery => _audioResumeRecovery.NeedsRecovery;

    /// <summary>
    /// A user has requested playback, but source renewal or synchronized startup is still pending.
    /// </summary>
    public bool IsPausedAudioOutputRecoveryResuming => _audioResumeRecovery.IsResumeBuffering;

    internal long AudioResumePresentationVersion => _audioResumeRecovery.PresentationVersion;

    /// <summary>
    /// Raised on the native event thread when the output is rebuilt while paused.
    /// The application owns URL freshness and any optional paused preparation policy.
    /// </summary>
    public event EventHandler? PausedAudioOutputReconfigured;

    /// <summary>
    /// Presents a requested recovery as buffering while the application renews its source URLs.
    /// The native player remains paused until ResumeAsync starts a synchronized seek.
    /// </summary>
    public void PreparePausedAudioOutputRecoveryResume()
    {
        _audioResumeRecovery.RequestResume(waitingForSource: true);
        NotifyPlayerState();
    }

    /// <summary>
    /// Cancels an outstanding resume intent without undoing a newer user pause or media change.
    /// </summary>
    public void CancelPendingAudioOutputRecovery()
    {
        _audioResumeRecovery.Cancel();
        NotifyPlayerState();
    }

    private void HandlePausedAudioOutputReconfiguration()
    {
        if (EnablePausedAudioOutputRecovery && _handle.GetProperty(MpvClientProperties.Pause, out bool paused) == MpvError.Success
            && _audioResumeRecovery.OutputReconfigured(paused))
            PausedAudioOutputReconfigured?.Invoke(this, EventArgs.Empty);
    }

    private MpvPlayerState PresentAudioRecoveryState(MpvPlayerState state)
        => _audioResumeRecovery.IsResumeBuffering ? MpvPlayerState.Buffering : state;

    private async Task<MpvResult> ResumeWithAudioRecoveryAsync()
    {
        var intent = _audioResumeRecovery.RequestResume();
        _ = NotifyPlayerStateAsync();
        var paused = false;
        var missingAudioPts = false;
        if (EnablePausedAudioOutputRecovery)
        {
            var error = await Task.Run(() =>
            {
                if (IsDisposed) return MpvError.Uninitialized;
                var pauseError = _handle.GetProperty(MpvClientProperties.Pause, out paused);
                if (paused && _handle.GetProperty("aid", out long aid) == MpvError.Success && aid > 0)
                    missingAudioPts = _handle.GetProperty("audio-pts", out double pts) != MpvError.Success || !double.IsFinite(pts);
                return pauseError;
            }).ConfigureAwait(false);
            if (error != MpvError.Success) return WrapAsResult(error, "Mpv | get pause failed");
            if (!paused && NeedsPausedAudioOutputRecovery)
            {
                error = await Task.Run(() => _audioResumeRecovery.ExecuteSeek(intent,
                    () => IsDisposed ? MpvError.Uninitialized : _handle.SetProperty(MpvClientProperties.Pause, true))).ConfigureAwait(false);
                if (error != MpvError.Success) return WrapAsResult(error, "Mpv | hold audio resume failed");
                paused = true;
            }
        }

        while (true)
        {
            var recovery = _audioResumeRecovery.PrepareResume(intent, paused, missingAudioPts);
            missingAudioPts = false;
            if (recovery is not null)
            {
                _ = NotifyPlayerStateAsync();
                // ao-reload only resets audio, discarding its output queue. A real seek resets
                // video as well and mpv's restart gate waits for both chains to become ready.
                // Its command response only means queued: keep pause until MPV_EVENT_SEEK.
                var error = await Task.Run(() => _audioResumeRecovery.ExecuteSeek(intent,
                    () => IsDisposed ? MpvError.Uninitialized : _handle.Command(["seek", "0", "relative+exact"]))).ConfigureAwait(false);
                if (error != MpvError.Success) return WrapAsResult(error, "Mpv | audio resume synchronization failed");
#pragma warning disable VSTHRD003 // This operation owns the native seek-event completion, which never requires the UI thread.
                if (!await recovery.Started.Task.ConfigureAwait(false)) return MpvResult.Ok();
#pragma warning restore VSTHRD003
            }

            var result = await Task.Run(() => _audioResumeRecovery.CommitResume(intent,
                () => IsDisposed ? MpvError.Uninitialized : _handle.SetProperty(MpvClientProperties.Pause, false))).ConfigureAwait(false);
            // A device can rebuild again after this seek started but before unpause. That
            // invalidates the preparation; repeat synchronization rather than releasing video.
            if (result.Retry) continue;
            _ = NotifyPlayerStateAsync();
            return WrapAsResult(result.Error, "Mpv | set pause failed");
        }
    }
}
