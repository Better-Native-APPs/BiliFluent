// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Resolvers;
using Richasy.MpvKernel.Core.Enums;
using Richasy.MpvKernel.Player;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private long _audioResumeUserIntent;
    private Task? _audioResumeSourceWork;
    private bool _audioResumeSourceResumeWanted;
    private bool? _requestedUserPause;
    private long _pauseRequestVersion;
    private bool _pauseRequestCompleted;
    private Task? _audioResumeBackgroundSourceWork;
    private long _audioResumeBackgroundSourceVersion;

    private bool IsAudioResumeSourceWaiting => _audioResumeSourceWork is { IsCompleted: false };
    private bool IsAudioResumeRequested => Client?.IsPausedAudioOutputRecoveryResuming == true
        || IsAudioResumeSourceWaiting && _audioResumeSourceResumeWanted;

    internal static bool IsAudioResumeInputCancellationBlocking(bool cancelling, bool sourceWaiting, bool pauseOrSeek)
        => cancelling && (!sourceWaiting || !pauseOrSeek);

    private void CancelAudioResumeUserIntent()
    {
        Interlocked.Increment(ref _audioResumeUserIntent);
        _audioResumeSourceResumeWanted = false;
        if (Client?.EnablePausedAudioOutputRecovery == true) Client.CancelPendingAudioOutputRecovery();
        _requestedUserPause = null;
        _pauseRequestVersion++;
    }

    private Task ToggleOnDemandPauseAsync()
        => SetUserPausedAsync(_requestedUserPause is { } paused ? !paused
            : Player?.IsPlaying == true || IsAudioResumeRequested);

    private long NoteUserPauseRequest(bool paused)
    {
        _requestedUserPause = paused;
        _pauseRequestCompleted = false;
        return ++_pauseRequestVersion;
    }

    private void ForgetUnsubmittedPauseRequest(long version)
    {
        if (version != _pauseRequestVersion) return;
        _requestedUserPause = null;
        _pauseRequestCompleted = false;
    }

    private async Task CompleteUserPauseRequestAsync(Task work, long version)
    {
        try
        {
#pragma warning disable VSTHRD003 // This request joins its already-owned native/source work; the version rejects an older completion.
            await work;
#pragma warning restore VSTHRD003
            if (version != _pauseRequestVersion) return;
            _pauseRequestCompleted = true;
            ObserveUserPauseAcknowledgement();
        }
        catch
        {
            if (version == _pauseRequestVersion) _requestedUserPause = null;
            throw;
        }
    }

    private void ObserveUserPauseAcknowledgement()
    {
        if (!_pauseRequestCompleted || _requestedUserPause is not { } paused || Player is not { } player) return;
        if (paused ? player.PlaybackState == MpvPlayerState.Paused : player.IsPlaying)
            _requestedUserPause = null;
    }

    private async Task<bool> PreflightExpiredAudioResumeAsync(long version, long intent)
    {
        if (Client is not { EnablePausedAudioOutputRecovery: true } client || !CanUseOnDemandNetwork
            || !_hasLoadedFile || IsPlaybackClosing || IsConnecting
            || OnDemandResolver?.CurrentStreams is not { } current)
            return true;

        client.AllowPausedAudioOutputRecovery = HasFreshAudioResumeSource(current);
        if (client.AllowPausedAudioOutputRecovery) return true;

        // The library both preflights and resumes with one native requirement. An ordinary
        // cached resume must keep its existing background refresh and refresh throttling.
        var requirement = await client.CheckPausedAudioOutputRecoveryAsync();
        if (intent != Interlocked.Read(ref _audioResumeUserIntent) || !IsCurrentPlayback(version)
            || !ReferenceEquals(client, Client)) return false;
        if (requirement.IsSuccess) return true;
        client.PreparePausedAudioOutputRecoveryResume();
        return false;
    }

    private static bool HasFreshAudioResumeSource(PlaybackStreamSelection selection)
        => selection.Deadline is not { } deadline || deadline - DateTimeOffset.UtcNow >= PlaybackNetworkPolicy.MinimumUrlLifetime;

    private Task StartAudioResumeBackgroundSourceRefresh(MpvPlayer player, MediaSourceResolverBase resolver,
        PlaybackStreamSelection from, long version, CancellationToken token)
    {
        _audioResumeBackgroundSourceVersion = version;
        _audioResumeBackgroundSourceWork = RefreshAudioResumeBackgroundSourceAsync(player, resolver, from, version, token);
        return _audioResumeBackgroundSourceWork;
    }

    private async Task RefreshAudioResumeBackgroundSourceAsync(MpvPlayer player, MediaSourceResolverBase resolver,
        PlaybackStreamSelection from, long version, CancellationToken token)
    {
        await RefreshRelayAsync(player, resolver, from, version, token, "resume-expired", reloadOnFailure: true);
        if (IsCurrentPlayback(version) && ReferenceEquals(player, Player) && ReferenceEquals(resolver, OnDemandResolver)
            && resolver.CurrentStreams is { } current && Client is { } client)
            client.AllowPausedAudioOutputRecovery = HasFreshAudioResumeSource(current);
    }

    private async Task ResumeDeferredForAudioSourceAsync(long version, long intent)
    {
        if (intent != Interlocked.Read(ref _audioResumeUserIntent) || !IsCurrentPlayback(version)
            || Client?.IsPausedAudioOutputRecoveryDeferredForSource != true) return;
        if (TryReloadExpiredSourceOnResume(out var reload))
        {
            await reload;
            return;
        }
        // A background table refresh may have completed while the native resume was queued.
        if (OnDemandResolver?.CurrentStreams is { } current && HasFreshAudioResumeSource(current))
            await SetUserPausedAsync(false);
    }

    private Task StartAudioResumeSourceRefresh(MpvPlayer player, MediaSourceResolverBase resolver,
        PlaybackStreamSelection from, long version, CancellationToken token)
    {
        _audioResumeSourceResumeWanted = true;
        _audioResumeSourceWork = RefreshAudioResumeSourceAsync(player, resolver, from, version,
            Interlocked.Read(ref _audioResumeUserIntent), token);
        return _audioResumeSourceWork;
    }

    private void ObserveAudioResumeSourcePresentation(string? propertyName)
    {
        if (propertyName is nameof(Player.PlaybackState) or nameof(Player.IsPlaying) or nameof(Player.IsBuffering))
            ObserveUserPauseAcknowledgement();
        if (!IsAudioResumeSourceWaiting || !_audioResumeSourceResumeWanted || Player is not { } player) return;
        // The native pause belongs to source renewal, not to the user. Keep the same
        // buffering presentation even if an expired source requires a whole-file reload.
        if (propertyName is nameof(Player.PlaybackState) or nameof(Player.IsPlaying) or nameof(Player.IsBuffering)
            && (player.PlaybackState != MpvPlayerState.Buffering || !player.IsPlaying || !player.IsBuffering))
            player.PresentAudioOutputRecoveryResume();
    }

    private async Task ResumeAfterAudioSourceWorkAsync(Task sourceWork, long version, long intent)
    {
#pragma warning disable VSTHRD003 // The source renewal is this view model's already-owned work; awaiting it holds no media/native gate.
        await sourceWork;
#pragma warning restore VSTHRD003
        if (intent == Interlocked.Read(ref _audioResumeUserIntent) && IsCurrentPlayback(version))
            await SetUserPausedAsync(false);
    }

    private async Task SeekAfterAudioSourceWorkAsync(Task sourceWork, double position, long version, long intent, bool resume)
    {
#pragma warning disable VSTHRD003 // This seek joins the source work it cancelled before entering the normal native lane.
        await sourceWork;
#pragma warning restore VSTHRD003
        if (intent != Interlocked.Read(ref _audioResumeUserIntent) || !IsCurrentPlayback(version)) return;
        var seek = SeekUserAsync(position);
        var submittedIntent = Interlocked.Read(ref _audioResumeUserIntent);
        await seek;
        if (resume && submittedIntent == Interlocked.Read(ref _audioResumeUserIntent) && IsCurrentPlayback(version))
            await SetUserPausedAsync(false);
    }

    private async Task RefreshAudioResumeSourceAsync(MpvPlayer player, MediaSourceResolverBase resolver,
        PlaybackStreamSelection from, long version, long intent, CancellationToken token)
    {
        try
        {
            // A recovery seek rereads both streams. Unlike an ordinary cached resume, it must
            // not race an asynchronous address-table refresh with expired upstream URLs.
            if (IsRelayingEvery(player, from))
            {
                if (_audioResumeBackgroundSourceVersion == version && _audioResumeBackgroundSourceWork is { } background)
                {
                    _audioResumeBackgroundSourceWork = null;
#pragma warning disable VSTHRD003 // Recovery joins this media's already-owned background refresh, never starts a second table request.
                    await background;
#pragma warning restore VSTHRD003
                }
                else
                    await RefreshRelayAsync(player, resolver, from, version, token, "audio-resume-expired", reloadOnFailure: true);
            }
            else
                await ReloadExpiredSourceAsync(player, resolver, from, version, player.Position,
                    player.PlaybackRate > 0 ? player.PlaybackRate : null, refreshed: false, preservePause: true);

            if (intent != Interlocked.Read(ref _audioResumeUserIntent) || !IsCurrentPlayback(version)
                || !ReferenceEquals(player, Player) || !ReferenceEquals(resolver, OnDemandResolver)) return;
            if (resolver.CurrentStreams is not { } current
                || current.Deadline is { } deadline && deadline - DateTimeOffset.UtcNow < PlaybackNetworkPolicy.MinimumUrlLifetime)
            {
                Client?.PreparePausedAudioOutputRecoveryResume();
                return;
            }

            // Do not make the normal pause path join this source task from inside itself.
            // A whole-file reload has already synchronized both chains; an in-place refresh
            // still needs the native seek guarantee before it can unpause.
            _audioResumeSourceWork = null;
            Client!.AllowPausedAudioOutputRecovery = true;
            await SetUserPausedAsync(false);
        }
        catch
        {
            if (intent == Interlocked.Read(ref _audioResumeUserIntent) && IsCurrentPlayback(version))
                Client?.PreparePausedAudioOutputRecoveryResume();
            throw;
        }
    }
}
