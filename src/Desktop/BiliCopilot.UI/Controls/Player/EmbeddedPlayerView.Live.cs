// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Hosting;
using BiliCopilot.UI.Live;
using BiliCopilot.UI.Live.Controls;
using BiliCopilot.UI.Pages.Overlay;
using BiliCopilot.UI.ViewModels.Core;
using Richasy.BiliKernel.Models.User;

namespace BiliCopilot.UI.Controls.Player;

/// <summary>
/// The live room parts of the page: the room header in the title row and the chat panel as the side column.
/// This view only places them and keeps the chat switch; the room, its messages and playback belong to the player.
/// The page tells a room from other media the same way its template selectors do, by the kind of the connector.
/// </summary>
public sealed partial class EmbeddedPlayerView
{
    // The chat panel is as wide as the page allows (LiveChatPanel: 340 / 284 / 260) and is not resized by hand.
    private const double RoomWideBreakpoint = 960d;
    private const double RoomNarrowBreakpoint = 640d;

    // The picture keeps at least this width next to an open chat panel.
    private const double RoomVideoMinWidth = 300d;

    private readonly HashSet<LiveRoomHeader> _roomHeaders = [];
    private bool _isRoomSidePanel;
    private double _roomSidePanelWidth;

    // What the user asked for. On a narrow page the panel is closed until it is opened by hand, and that choice
    // ends when the page is wide again.
    private bool _isChatRequested = true;
    private bool _isChatOpenedWhenNarrow;

    private static bool HasRoomSidePanel(object? connector) => connector is LiveConnectorViewModel;

    private static LivePlaybackState GetRoomPlaybackState(PlayerViewModel? owner)
        => owner is null ? LivePlaybackState.Idle
            : owner.IsPlaybackClosing ? LivePlaybackState.Closing
            : owner.LiveLoadState switch
            {
                LiveMediaLoadState.Resolving or LiveMediaLoadState.CommandSubmitted or LiveMediaLoadState.FileLoaded => LivePlaybackState.Opening,
                LiveMediaLoadState.Ready => LivePlaybackState.Playing,
                LiveMediaLoadState.Paused => LivePlaybackState.Paused,
                LiveMediaLoadState.Ended => LivePlaybackState.Ended,
                LiveMediaLoadState.Failed => LivePlaybackState.Failed,
                LiveMediaLoadState.Closed => LivePlaybackState.Closed,
                _ => LivePlaybackState.Idle,
            };

#pragma warning disable CA1822 // Instance members: the bindings in the page markup call them on the page.
    private int GetHeaderColumnSpan(object? connector) => HasRoomSidePanel(connector) ? 2 : 1;

    private Visibility GetSideSplitVisibility(object? connector)
        => HasRoomSidePanel(connector) ? Visibility.Collapsed : Visibility.Visible;

    private int GetSideBodyRow(object? connector) => HasRoomSidePanel(connector) ? 0 : 2;

    private int GetSideBodyRowSpan(object? connector) => HasRoomSidePanel(connector) ? 3 : 1;
#pragma warning restore CA1822

    private double GetRoomSidePanelWidth()
        => ActualWidth >= RoomWideBreakpoint ? LiveChatPanel.RecommendedWidth
            : ActualWidth >= RoomNarrowBreakpoint ? LiveChatPanel.CompactWidth
            : LiveChatPanel.MinimumWidth;

    // Whether the page has room for the chat panel at all. The switch is offered exactly then.
    private bool CanShowRoomSidePanel()
        => _active && _loaded && !_suspended && ViewModel is not null && _mode == PlayerPresentationMode.Normal
            && ActualWidth - GetRoomSidePanelWidth() >= RoomVideoMinWidth;

    private void UpdateRoomSidePanel()
    {
        var narrow = ActualWidth < RoomNarrowBreakpoint;
        if (!narrow) _isChatOpenedWhenNarrow = false;
        var width = GetRoomSidePanelWidth();
        var show = CanShowRoomSidePanel() && _isChatRequested && (!narrow || _isChatOpenedWhenNarrow);
        if (_isSidePanelShown != show || (show && _roomSidePanelWidth != width))
        {
            _isSidePanelShown = show;
            _roomSidePanelWidth = width;

            // The splitter stays in its hidden form: the panel width follows the page, not a drag.
            IsNavColumnEffectiveHide = true;
            SidePanelColumn.Width = new GridLength(show ? width : 0);
            IsSidePanelActive = show;
        }

        UpdateRoomHeaders();
    }

    private void OnRoomOwnerChanged(PlayerViewModel owner, string? propertyName)
    {
        // Entering or leaving a room swaps the side column rules; inside a room only the header follows playback.
        if (propertyName is null or "" or nameof(PlayerViewModel.Connector))
        {
            if (HasRoomSidePanel(owner.Connector) || _isRoomSidePanel) UpdateSidePanel();
        }
        else if (propertyName is nameof(PlayerViewModel.LiveLoadState) or nameof(PlayerViewModel.IsPlaybackClosing))
        {
            UpdateRoomHeaders();
        }
    }

    private void OnRoomHeaderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not LiveRoomHeader header) return;
        _roomHeaders.Add(header);
        UpdateRoomHeader(header);
    }

    private void OnRoomHeaderUnloaded(object sender, RoutedEventArgs e)
    {
        // A late Unloaded of a re-parented, already reloaded header must not drop it.
        if (sender is LiveRoomHeader { IsLoaded: false } header) _roomHeaders.Remove(header);
    }

    private void UpdateRoomHeaders()
    {
        foreach (var header in _roomHeaders) UpdateRoomHeader(header);
    }

    private void UpdateRoomHeader(LiveRoomHeader header)
    {
        header.PlaybackState = GetRoomPlaybackState(ViewModel);
        header.IsChatOpen = _isRoomSidePanel && _isSidePanelShown == true;
        header.IsChatToggleVisible = CanShowRoomSidePanel();
    }

    private void OnRoomChatToggleRequested(object? sender, EventArgs e)
    {
        if (!_isRoomSidePanel || !CanShowRoomSidePanel()) return;
        var open = _isSidePanelShown != true;
        if (ActualWidth < RoomNarrowBreakpoint)
        {
            _isChatOpenedWhenNarrow = open;
            if (open) _isChatRequested = true;
        }
        else
        {
            _isChatRequested = open;
        }

        UpdateSidePanel();
    }

    // The button of the failed state: ask for the picture of the room once more. The player clears the error
    // when it starts, and shows it again if the picture still cannot be loaded.
    private async void OnRoomRetryClick(object sender, RoutedEventArgs e)
    {
        var owner = ViewModel;
        if (!IsCurrentAttachment(owner, _attachmentEpoch) || owner is not { IsPlaybackClosing: false, IsLiveReleaseBlocked: false }) return;
        try
        {
            await owner.RefreshLiveAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Live retry failed: {ex.Message}");
        }
    }

    // The same way to the user space as the uploader of a video.
    private void OnRoomAnchorInvoked(object? sender, UserProfile anchor)
    {
        if (!_active || _suspended || anchor is null) return;
        this.Get<NavigationViewModel>().NavigateToOver(typeof(UserSpacePage), anchor);
    }
}
