// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// What the shared player views need to know about a live room, derived from the live state of the player.
/// Read-only: nothing here starts, stops or changes playback. Each property is raised again right after the
/// state it is derived from, so a view binds to it instead of asking what kind of media plays. A live room plays
/// in the same transport bars as any other media: the members below let one quality menu and one danmaku box
/// serve both, and equal the on-demand members they stand for whenever the media is not a room.
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

    /// <summary>
    /// The quality menu has something to show: a choice for an on-demand media, the tiers of a room.
    /// For an on-demand media this is <see cref="IsQualityMenuAvailable"/>.
    /// </summary>
    public bool HasQualityMenu => IsQualityMenuAvailable || HasLiveQualities;

    /// <summary>The entries of the quality menu. For an on-demand media this is <see cref="Sources"/>.</summary>
    public ObservableCollection<SourceItemViewModel> QualityMenuSources => IsLiveMedia ? LiveSources : Sources;

    /// <summary>
    /// The entry of the quality menu that plays. For an on-demand media this is <see cref="SelectedSource"/>.
    /// </summary>
    public SourceItemViewModel? QualityMenuSelection => IsLiveMedia ? SelectedLiveSource : SelectedSource;

    /// <summary>
    /// The danmaku switch has a layer to show or hide: the danmaku of an on-demand media, or those of a room.
    /// For an on-demand media this is <see cref="IsDanmakuControlVisible"/>.
    /// </summary>
    public bool IsDanmakuSwitchAvailable => IsDanmakuControlVisible || HasRoomChat;

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
                OnPropertyChanged(nameof(IsDanmakuSwitchAvailable));
                break;
            case nameof(IsDanmakuControlVisible):
                OnPropertyChanged(nameof(IsDanmakuSwitchAvailable));
                break;
            case nameof(IsQualityMenuAvailable):
                OnPropertyChanged(nameof(HasQualityMenu));
                break;
            case nameof(SelectedSource):
            case nameof(SelectedLiveSource):
                OnPropertyChanged(nameof(QualityMenuSelection));
                break;

            // The last name of the live notification and of the capability notification: by then every
            // live member and the admitted media are up to date.
            case nameof(CanSelectLiveQuality):
            case nameof(CanSendDanmaku):
                OnPropertyChanged(nameof(HasLiveQualities));
                OnPropertyChanged(nameof(HasQualityMenu));
                OnPropertyChanged(nameof(QualityMenuSources));
                OnPropertyChanged(nameof(QualityMenuSelection));
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
