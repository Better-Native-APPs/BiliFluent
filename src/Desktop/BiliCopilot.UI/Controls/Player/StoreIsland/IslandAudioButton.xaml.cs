// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Xaml.Input;
using System.ComponentModel;

namespace BiliCopilot.UI.Controls.Player.StoreIsland;

/// <summary>
/// The audio track button on the right of the island transport bar. It follows <see cref="IslandFormatButton"/>:
/// the list shows <see cref="PlayerViewModel.AudioTracks"/> and one click switches to the chosen track.
/// </summary>
public sealed partial class IslandAudioButton : PlayerControlBase
{
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(IslandAudioButton),
            new PropertyMetadata(false, OnActiveChanged));

    private readonly IslandPopupClaims _popups = new();
    private PlayerViewModel? _subscribedViewModel;
    private bool _loaded;
    private bool _synchronizing;
    private long _epoch;
    private AudioTrackItemViewModel? _activating;
    private AudioTrackItemViewModel? _tapped;
    private long _tappedAt;

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private bool CanInteract => _loaded && IsActive && ViewModel is { IsWindowDeactivated: false,
        IsPlaybackClosing: false, IsConnecting: false, IsAudioTrackSelectable: true }
        && ViewModel.Player?.IsPlaybackInitialized == true;

    /// <summary>
    /// Initializes a new instance of the <see cref="IslandAudioButton"/> class.
    /// </summary>
    public IslandAudioButton()
    {
        InitializeComponent();
        Loaded += OnLocalLoaded;
        Unloaded += OnLocalUnloaded;
    }

    /// <inheritdoc/>
    private void OnLocalLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        Subscribe(ViewModel);
        Synchronize();
    }

    private void OnLocalUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _loaded = false;
        CancelPendingInput();
        Subscribe(null);
        Synchronize();
    }

    /// <inheritdoc/>
    protected override void OnViewModelChanged(PlayerViewModel? oldValue, PlayerViewModel? newValue)
    {
        CancelPendingInput();
        Subscribe(_loaded ? newValue : null);
        Synchronize();
    }

    public void CancelPendingInput()
    {
        ++_epoch;
        _popups.Cancel();
    }

    private static void OnActiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (IslandAudioButton)sender;
        if (!(bool)args.NewValue) control.CancelPendingInput();
        control.Synchronize();
    }

    private void Subscribe(PlayerViewModel? vm)
    {
        if (_subscribedViewModel is not null) _subscribedViewModel.PropertyChanged -= OnOwnerChanged;
        _subscribedViewModel = vm;
        if (vm is not null) vm.PropertyChanged += OnOwnerChanged;
    }

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _subscribedViewModel)) return;
        if (args.PropertyName is nameof(PlayerViewModel.MediaGeneration) or nameof(PlayerViewModel.AudioTracks)
            || !CanInteract) CancelPendingInput();
        Synchronize();
    }

    private void Synchronize()
    {
        if (AudioView is null) return;
        _synchronizing = true;
        try
        {
            IsEnabled = CanInteract;
            AudioView.ItemsSource = _loaded ? ViewModel?.AudioTracks : null;
            var index = _loaded && ViewModel is { } vm
                ? vm.AudioTracks.ToList().FindIndex(item => ReferenceEquals(item, vm.SelectedAudioTrack)) : -1;
            if (index >= 0) AudioView.Select(index);
            else AudioView.DeselectAll();
        }
        finally { _synchronizing = false; }
    }

    private void OnFlyoutOpened(object sender, object e)
    {
        if (!CanInteract) { IslandPopupClaims.Hide(sender); return; }
        _popups.Open(sender, ViewModel, _epoch);
        Synchronize();
    }

    private void OnAudioItemInvoked(ItemsView sender, ItemsViewItemInvokedEventArgs args)
    {
        // ItemInvoked is user activation. Select/Deselect/ItemsSource never dispatch commands.
        // The second click of a double click arrives here after the first one was already
        // handled as a tap; it is the same activation, not another one.
        if (IslandPopupClaims.IsDoubleTapEcho(args.InvokedItem, _tapped, _tappedAt)) return;
        Activate(args.InvokedItem);
    }

    private void OnAudioItemTapped(object sender, TappedRoutedEventArgs args)
    {
        // While the list tracks a selection, ItemsView raises ItemInvoked only for a double tap
        // or Enter. A single click or tap on an item activates it here.
        var item = (sender as FrameworkElement)?.Tag;
        _tapped = item as AudioTrackItemViewModel;
        _tappedAt = Environment.TickCount64;
        Activate(item);
    }

    private async void Activate(object? item)
    {
        if (_synchronizing || !CanInteract || !_popups.IsCurrent(_popups.Get(AudioFlyout), ViewModel, _epoch)
            || !ReferenceEquals(AudioView.ItemsSource, ViewModel.AudioTracks)
            || item is not AudioTrackItemViewModel { IsEnabled: true } track
            || !ViewModel.AudioTracks.Any(item => ReferenceEquals(item, track))
            || ReferenceEquals(track, ViewModel.SelectedAudioTrack)
            || ReferenceEquals(track, _activating)) return;
        _activating = track;
        try
        {
            await track.ActiveCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Island audio track selection failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_activating, track)) _activating = null;
        }
    }
}
