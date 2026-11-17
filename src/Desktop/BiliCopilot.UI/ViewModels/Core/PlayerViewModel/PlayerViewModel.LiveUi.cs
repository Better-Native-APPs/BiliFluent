// Copyright (c) BiliFluent. All rights reserved.

using System.ComponentModel;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// What the shared player views need to know about a live room, derived from the live state of the player.
/// Read-only: nothing here starts, stops or changes playback. Each property is raised again right after the
/// state it is derived from, so a view binds to it instead of asking what kind of media plays.
/// </summary>
public sealed partial class PlayerViewModel
{
    /// <summary>The media that plays comes with a room: its chat, its danmaku and the box to send one.</summary>
    public bool HasRoomChat => Connector is LiveConnectorViewModel;

    /// <summary>The room names its quality tiers, even when the one that plays is the only one.</summary>
    public bool HasLiveQualities => IsLiveMedia && LiveSources.Count > 0;

    /// <summary>The stream ended or dropped after it had played, and the player is not asking for it again.</summary>
    public bool IsLiveEnded => IsLiveMedia && LiveLoadState == LiveMediaLoadState.Ended && !IsConnecting;

    /// <summary>The failure on screen belongs to a live room whose picture could not be shown.</summary>
    public bool IsLiveFailure => IsLiveMedia && !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>Asking for the newest picture of the room is allowed right now.</summary>
    public bool CanRefreshLive => IsLiveMedia && !IsPlaybackClosing && !IsLiveReleaseBlocked;

    /// <inheritdoc/>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(Connector):
                // The live connector is the same object seen as a room; it changes with the connector.
                OnPropertyChanged(nameof(HasRoomChat));
                OnPropertyChanged(nameof(LiveConnector));
                break;

            // The last name of the live notification and of the capability notification: by then every
            // live member and the admitted media are up to date.
            case nameof(CanSelectLiveQuality):
            case nameof(CanSendDanmaku):
                OnPropertyChanged(nameof(HasLiveQualities));
                OnPropertyChanged(nameof(IsLiveEnded));
                OnPropertyChanged(nameof(IsLiveFailure));
                OnPropertyChanged(nameof(CanRefreshLive));
                break;
            case nameof(IsConnecting):
                OnPropertyChanged(nameof(IsLiveEnded));
                break;
            case nameof(ErrorMessage):
                OnPropertyChanged(nameof(IsLiveFailure));
                break;
            case nameof(IsPlaybackClosing):
                OnPropertyChanged(nameof(CanRefreshLive));
                break;
        }
    }
}
