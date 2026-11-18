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

    private bool IsAudioResumeSourceWaiting => _audioResumeSourceWork is { IsCompleted: false };
    private bool IsAudioResumeRequested => Client?.IsPausedAudioOutputRecoveryResuming == true
        || IsAudioResumeSourceWaiting && _audioResumeSourceResumeWanted;

    private void CancelAudioResumeUserIntent()
    {
        Interlocked.Increment(ref _audioResumeUserIntent);
        _audioResumeSourceResumeWanted = false;
        Client?.CancelPendingAudioOutputRecovery();
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
                await RefreshRelayAsync(player, resolver, from, version, token, "audio-resume-expired", reloadOnFailure: true);
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
