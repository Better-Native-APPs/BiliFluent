// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Forms;
using BiliCopilot.UI.Models;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class AppViewModel
{
    private NavigationViewModel? _mainNavigation;
    private PlayerViewModel? _navigationPlaybackOwner;
    private MediaSnapshot? _navigationPlaybackSnapshot;

    internal void BindMainNavigation(NavigationViewModel navigation)
        => _mainNavigation = navigation;

    partial void QueueMainPlayerNavigation(MediaSnapshot snapshot, Func<Task> open, ref Task? navigation)
    {
        if (_mainNavigation is { } owner) navigation = owner.OpenPlayerAsync(snapshot, open);
    }

    partial void OnMainPlayerCreated(PlayerViewModel player, MediaSnapshot snapshot)
    {
        _navigationPlaybackOwner = player;
        _navigationPlaybackSnapshot = snapshot;
        _mainNavigation?.RecordOpenedPlayer(snapshot);
    }

    partial void OnMainPlayerHostAttached(PlayerViewModel player)
        => Windows.OfType<MainWindow>().FirstOrDefault()?.AttachEmbeddedNavigationInput(player);

    partial void OnMainPlayerClosing(PlayerViewModel? player)
    {
        if (player is not null)
            Windows.OfType<MainWindow>().FirstOrDefault()?.DetachEmbeddedNavigationInput(player);
    }

    partial void OnMainPlayerReleased(PlayerViewModel player)
    {
        OnMainPlayerClosing(player);
        if (!ReferenceEquals(player, _navigationPlaybackOwner)) return;
        _navigationPlaybackOwner = null;
        _navigationPlaybackSnapshot = null;
    }

    internal Task RestoreMainPlayerAsync(MediaSnapshot snapshot) => OpenMainPlayerAsync(snapshot);

    internal MediaSnapshot? CaptureMainPlayerNavigation()
    {
        if (EmbeddedPlayer is not { IsPlaybackClosing: false } player
            || !ReferenceEquals(player, _navigationPlaybackOwner) || _navigationPlaybackSnapshot is not { } original)
            return null;

        // Playlist/part/episode changes stay inside PlayerViewModel. Read the current connector's
        // already-resolved media instead of returning to the original launch item.
        var snapshot = player.Connector switch
        {
            VideoConnectorViewModel { _view.Information: { } video } connector => new MediaSnapshot(video, connector.IsPrivatePlay),
            PgcConnectorViewModel { _view.Information: { } season } connector => new MediaSnapshot(season, connector._episode),
            _ => MediaNavigationSnapshot.Copy(original),
        };
        MediaNavigationSnapshot.CopyPreferences(original, snapshot);
        if (player.Connector is VideoConnectorViewModel currentVideo) snapshot.PreferPart = currentVideo._part;
        MediaNavigationSnapshot.SetPosition(snapshot, player.Player?.Position);
        return snapshot;
    }

    internal bool HasRestoredMainPlayer
        => EmbeddedPlayer is { IsPlaybackClosing: false } player && string.IsNullOrEmpty(player.ErrorMessage)
            && (IsSharedLivePlayer(player) ? player.HasLiveFrame : player.Player?.IsPlaybackInitialized == true);
}
