// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using Richasy.MpvKernel.Player.Models;

namespace Richasy.MpvKernel.Player;

public sealed partial class MpvPlayer
{
    /// <summary>
    /// Presents a requested audio recovery as playing and buffering while its source is renewed.
    /// This changes UI intent only; the client keeps the native player paused.
    /// </summary>
    public void PresentAudioOutputRecoveryResume()
        => PostCounted(_ =>
        {
            _isAudioRecoveryResuming = true;
            PlaybackState = Core.Enums.MpvPlayerState.Buffering;
            IsPlaying = true;
            IsBuffering = true;
        });

    private void CheckStateProperties()
    {
        PostCounted(_ =>
        {
            IsPlaying = PlaybackState == Core.Enums.MpvPlayerState.Playing || _isAudioRecoveryResuming;
            IsBuffering = PlaybackState is Core.Enums.MpvPlayerState.Buffering or Core.Enums.MpvPlayerState.Seeking;
            IsStopped = PlaybackState is Core.Enums.MpvPlayerState.Idle or Core.Enums.MpvPlayerState.End;
        });
    }

    private void TryShowSubtitle()
        => RunPlayerCallback(() => _subtitleResolver?.ShowSubtitle(Convert.ToInt32(Position)), "subtitle");

    private async Task InitializeTracksAsync()
    {
        if (!TryStartPlayerOperation(Client.GetTracksAsync, out var pending)) return;
        var tracks = await pending.ConfigureAwait(false);
        if (_isDisposed) return;
        if (tracks.IsSuccess)
        {
            PostCounted(_ =>
            {
                var args = new MpvTrackUpdatedEventArgs(tracks.Value);
                TrackUpdated?.Invoke(this, args);
            });
        }
    }
}
