// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Forms;
using BiliCopilot.UI.Models;
using System.ComponentModel;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class AppViewModel
{
    private NavigationViewModel? _mainNavigation;
    private PlayerViewModel? _navigationPlaybackOwner;
    private MediaSnapshot? _navigationPlaybackSnapshot;
    private event Action? MainPlaybackNavigationChanged;

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
        MainPlaybackNavigationChanged?.Invoke();
    }

    partial void OnMainPlayerHostAttached(PlayerViewModel player)
        => Windows.OfType<MainWindow>().FirstOrDefault()?.AttachEmbeddedNavigationInput(player);

    partial void OnMainPlayerClosing(PlayerViewModel? player)
    {
        if (player is not null)
        {
            Windows.OfType<MainWindow>().FirstOrDefault()?.DetachEmbeddedNavigationInput(player);
            if (ReferenceEquals(player, _navigationPlaybackOwner) && _navigationPlaybackSnapshot is { } snapshot
                && !string.IsNullOrEmpty(player.ErrorMessage)
                && (IsSharedLivePlayer(player) ? !player.HasLiveFrame : player.Player?.IsPlaybackInitialized != true))
                _mainNavigation?.NoteUnavailablePlayback(snapshot);
        }
        MainPlaybackNavigationChanged?.Invoke();
    }

    partial void OnMainPlayerReleased(PlayerViewModel player)
    {
        Windows.OfType<MainWindow>().FirstOrDefault()?.DetachEmbeddedNavigationInput(player);
        if (!ReferenceEquals(player, _navigationPlaybackOwner)) return;
        _mainNavigation?.DiscardReleasedUnavailablePlayback();
        _navigationPlaybackOwner = null;
        _navigationPlaybackSnapshot = null;
        MainPlaybackNavigationChanged?.Invoke();
    }

    partial void OnMainPlaybackNavigationChanged()
    {
        MainPlaybackNavigationChanged?.Invoke();
        _mainNavigation?.NotifyHistoryAvailability();
    }

    internal bool TryDismissMainNavigationTransient()
        => Windows.OfType<MainWindow>().FirstOrDefault()?.TryDismissNavigationTransient() == true;

    internal async Task<PlaybackNavigationRestoreResult> RestoreMainPlayerAsync(MediaSnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Opening submits playback. PlaybackRestart/first live frame can arrive after this task returns.
            // The application owns the actual open; a cancelled observation never bypasses its close barrier.
            await OpenMainPlayerAsync(snapshot).WaitAsync(cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested || IsPlaybackStopping)
        {
            return PlaybackNavigationRestoreResult.Superseded;
        }
        catch (Exception)
        {
            return PlaybackNavigationRestoreResult.Failed;
        }

        if (cancellationToken.IsCancellationRequested || IsPlaybackStopping)
            return PlaybackNavigationRestoreResult.Superseded;
        if (_navigationPlaybackOwner is not { } player || !ReferenceEquals(_navigationPlaybackSnapshot, snapshot))
            return PlaybackNavigationRestoreResult.Failed;

        var done = new TaskCompletionSource<PlaybackNavigationRestoreResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        INotifyPropertyChanged? native = null;
        void CheckResult()
        {
            if (cancellationToken.IsCancellationRequested || IsPlaybackStopping || player.IsPlaybackClosing
                || !ReferenceEquals(EmbeddedPlayer, player) || !ReferenceEquals(_navigationPlaybackOwner, player)
                || !ReferenceEquals(_navigationPlaybackSnapshot, snapshot))
                done.TrySetResult(PlaybackNavigationRestoreResult.Superseded);
            else if (!string.IsNullOrEmpty(player.ErrorMessage))
                done.TrySetResult(PlaybackNavigationRestoreResult.Failed);
            else if (IsSharedLivePlayer(player) ? player.HasLiveFrame : player.Player?.IsPlaybackInitialized == true)
                done.TrySetResult(PlaybackNavigationRestoreResult.Restored);
        }
        void AttachNative()
        {
            if (ReferenceEquals(native, player.Player)) return;
            if (native is not null) native.PropertyChanged -= OnNativeChanged;
            native = player.Player;
            if (native is not null) native.PropertyChanged += OnNativeChanged;
        }
        void OnNativeChanged(object? sender, PropertyChangedEventArgs args) => CheckResult();
        void OnPlayerChanged(object? sender, PropertyChangedEventArgs args)
        {
            AttachNative();
            CheckResult();
        }
        void OnAppChanged(object? sender, PropertyChangedEventArgs args) => CheckResult();

        player.PropertyChanged += OnPlayerChanged;
        PropertyChanged += OnAppChanged;
        MainPlaybackNavigationChanged += CheckResult;
        using var cancellation = cancellationToken.Register(() => done.TrySetResult(PlaybackNavigationRestoreResult.Superseded));
        try
        {
            // Subscribe before inspecting to cover events both before and after open completion.
            AttachNative();
            CheckResult();
            return await done.Task;
        }
        finally
        {
            if (native is not null) native.PropertyChanged -= OnNativeChanged;
            player.PropertyChanged -= OnPlayerChanged;
            PropertyChanged -= OnAppChanged;
            MainPlaybackNavigationChanged -= CheckResult;
        }
    }

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
        if (player.Player?.IsPlaybackInitialized == true)
            MediaNavigationSnapshot.SetPosition(snapshot, player.Player.Position);
        return snapshot;
    }
}
