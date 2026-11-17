// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Controls.Player.StoreIsland;
using BiliCopilot.UI.Hosting;
using BiliCopilot.UI.Live.Controls;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Richasy.MpvKernel.Core.Enums;
using Richasy.MpvKernel.Player;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace BiliCopilot.UI.Controls.Core;

/// <summary>Which transport bar hosts a <see cref="LiveTransportRow"/>.</summary>
public enum LiveTransportLayout
{
    /// <summary>The card of the normal window: commands left, the danmaku box in the middle.</summary>
    Island,

    /// <summary>The bar of full screen and the compact window: the danmaku box left, play centred.</summary>
    Overlay,
}

/// <summary>
/// The commands of a media without a timeline, in one row: play or pause, refresh, the danmaku switch and box of
/// a room, quality, volume, compact window and full screen. Both transport bars host it in place of their on-demand
/// rows; the host says which arrangement to use and whether it takes input. The row owns no playback: every command
/// goes to the player, and what it shows follows the player.
/// </summary>
public sealed partial class LiveTransportRow : PlayerControlBase
{
    /// <summary><see cref="IsActive"/> dependency property.</summary>
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(LiveTransportRow), new PropertyMetadata(false, OnActiveChanged));

    /// <summary><see cref="Layout"/> dependency property.</summary>
    public static readonly DependencyProperty LayoutProperty =
        DependencyProperty.Register(nameof(Layout), typeof(LiveTransportLayout), typeof(LiveTransportRow), new PropertyMetadata(LiveTransportLayout.Island, OnLayoutChanged));

    // Below these row widths the box, then the commands that have another way, give their place to the rest.
    // Play, the danmaku switch, volume and the window commands stay at any width.
    private const double IslandBoxMinWidth = 472;
    private const double IslandCommandsMinWidth = 300;
    private const double OverlayBoxMinWidth = 576;
    private const double OverlayCommandsMinWidth = 400;

    // The box of the full screen bar keeps to the left and does not grow past this width: with the switch
    // beside it the group is 400 wide, the width a widget has at the left of that bar.
    private const double OverlayBoxMaxWidth = 352;

    private readonly IslandPopupClaims _popups = new();
    private PlayerViewModel? _subscribedViewModel;
    private MpvPlayer? _subscribedPlayer;
    private bool _loaded;
    private bool _synchronizingVolume;
    private int _volumeWrites;
    private long _epoch;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTransportRow"/> class.
    /// </summary>
    public LiveTransportRow()
    {
        InitializeComponent();
        SetName(RefreshButton, ResourceToolkit.GetLocalizedString(StringNames.LiveRefresh));
        SetName(DanmakuToggle, ResourceToolkit.GetLocalizedString(StringNames.LiveDanmakuSwitch));
        var volume = ResourceToolkit.GetLocalizedString(StringNames.Volume);
        SetName(VolumeButton, volume);
        AutomationProperties.SetName(VolumeSlider, volume);
        VolumeCaption.Text = volume;
        ApplyLayout();
        Loaded += OnLocalLoaded;
        Unloaded += OnLocalUnloaded;
        Synchronize();
    }

    /// <summary>Whether the host bar takes input.</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>The arrangement of the host bar.</summary>
    public LiveTransportLayout Layout
    {
        get => (LiveTransportLayout)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    /// <summary>The play button, for the host that animates it between its bars. Visual only.</summary>
    internal FrameworkElement PlayPauseTarget => PlayPauseButton;

    private bool CanInteract => _loaded && IsActive && ViewModel is { IsWindowDeactivated: false,
        IsPlaybackClosing: false, IsConnecting: false } && ViewModel.Player?.IsPlaybackInitialized == true;

    /// <summary>Closes what the row has open. The host calls it when it cancels its own pending input.</summary>
    public void CancelPendingInput()
    {
        ++_epoch;
        _popups.Cancel();
        QualityEntry?.CancelPendingInput();
    }

    /// <inheritdoc/>
    protected override void OnViewModelChanged(PlayerViewModel? oldValue, PlayerViewModel? newValue)
    {
        CancelPendingInput();
        Subscribe(_loaded ? newValue : null);
        Synchronize();
    }

    private static void OnActiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var row = (LiveTransportRow)sender;
        if (!(bool)args.NewValue) row.CancelPendingInput();
        row.Synchronize();
    }

    private static void OnLayoutChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var row = (LiveTransportRow)sender;

        // An open list or slider is anchored to a button that is about to move.
        row.CancelPendingInput();
        row.ApplyLayout();
        row.Synchronize();
    }

    private static void SetName(FrameworkElement element, string name)
    {
        if (AutomationProperties.GetName(element) == name) return;
        AutomationProperties.SetName(element, name);
        ToolTipService.SetToolTip(element, name); // Icon-only command: the name is the tooltip.
    }

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

    private void Subscribe(PlayerViewModel? viewModel)
    {
        if (!ReferenceEquals(_subscribedViewModel, viewModel))
        {
            if (_subscribedViewModel is not null) _subscribedViewModel.PropertyChanged -= OnOwnerChanged;
            _subscribedViewModel = viewModel;
            if (viewModel is not null) viewModel.PropertyChanged += OnOwnerChanged;
        }

        SubscribePlayer(viewModel?.Player);
    }

    private void SubscribePlayer(MpvPlayer? player)
    {
        if (ReferenceEquals(_subscribedPlayer, player)) return;
        if (_subscribedPlayer is not null) _subscribedPlayer.PropertyChanged -= OnPlayerChanged;
        _subscribedPlayer = player;
        if (player is not null) player.PropertyChanged += OnPlayerChanged;
    }

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _subscribedViewModel)) return;
        if (args.PropertyName is not (null or "" or nameof(PlayerViewModel.Player) or nameof(PlayerViewModel.MediaGeneration)
            or nameof(PlayerViewModel.HasRoomChat) or nameof(PlayerViewModel.CanRefreshLive) or nameof(PlayerViewModel.IsLiveReleaseBlocked)
            or nameof(PlayerViewModel.Presentation) or nameof(PlayerViewModel.IsMute) or nameof(PlayerViewModel.MaxVolume)
            or nameof(PlayerViewModel.IsWindowDeactivated) or nameof(PlayerViewModel.IsConnecting) or nameof(PlayerViewModel.IsPlaybackClosing)))
        {
            return;
        }

        if (args.PropertyName is null or "" or nameof(PlayerViewModel.Player)) SubscribePlayer(_subscribedViewModel?.Player);

        // What is open belongs to one media and to a bar that takes input.
        if (args.PropertyName == nameof(PlayerViewModel.MediaGeneration) || !CanInteract) CancelPendingInput();
        Synchronize();
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (null or "" or nameof(MpvPlayer.PlaybackState) or nameof(MpvPlayer.IsLoading)
            or nameof(MpvPlayer.Volume) or nameof(MpvPlayer.IsPlaybackInitialized)))
        {
            return;
        }

        // The player can report from another thread.
        if (DispatcherQueue.HasThreadAccess) Apply();
        else DispatcherQueue.TryEnqueue(Apply);

        void Apply()
        {
            if (!ReferenceEquals(sender, _subscribedPlayer)) return;
            if (!CanInteract) CancelPendingInput();
            Synchronize();
        }
    }

    /// <summary>Styles and places of the two arrangements. The order inside each group never changes.</summary>
    private void ApplyLayout()
    {
        var overlay = Layout == LiveTransportLayout.Overlay;

        // The bar keeps play in the centre between two equal sides; the card gives the middle to the box.
        LeftColumn.Width = overlay ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        MiddleColumn.Width = overlay ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        RightColumn.Width = overlay ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        CommandRow.ColumnSpacing = overlay ? 0 : 8;
        Grid.SetColumn(ComposerGroup, overlay ? 0 : 1);
        Grid.SetColumn(PlaybackGroup, overlay ? 1 : 0);
        ComposerGroup.Margin = overlay ? new Thickness(0, 0, 24, 0) : default;

        // The group fills its side; capping the column of the box keeps the box at the left with a steady width.
        ComposerColumn.MaxWidth = overlay ? OverlayBoxMaxWidth : double.PositiveInfinity;
        WindowGroup.HorizontalAlignment = overlay ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        WindowGroup.Margin = overlay ? new Thickness(0, 0, 8, 0) : default;

        // The bar is a black gradient in every app theme; the box on it stays legible.
        ComposerGroup.RequestedTheme = overlay ? ElementTheme.Dark : ElementTheme.Default;

        PlayPauseButton.Style = LocalStyle(overlay ? "OverlayPlayButtonStyle" : "IslandPlayButtonStyle");
        PlayPauseIcon.FontSize = overlay ? 22 : 14;
        BufferingRing.Width = BufferingRing.Height = overlay ? 52 : 38;
        RefreshIcon.FontSize = overlay ? 16 : 14;
        DanmakuToggle.Style = LocalStyle(overlay ? "OverlayDanmakuToggleStyle" : "IslandDanmakuToggleStyle");
        var iconStyle = LocalStyle(overlay ? "OverlayIconButtonStyle" : "IslandIconButtonStyle");
        RefreshButton.Style = iconStyle;
        Composer.SendButtonStyle = iconStyle;
        QualityEntry.EntryStyle = iconStyle;
        VolumeButton.Style = iconStyle;
        CompactOverlayButton.Style = iconStyle;
        FullScreenButton.Style = iconStyle;
    }

    private Style LocalStyle(string key) => (Style)Resources[key];

    private void Synchronize()
    {
        if (PlayPauseButton is null || VolumeSlider is null) return;
        var viewModel = _loaded ? ViewModel : null;
        var active = CanInteract;
        IsEnabled = active;

        var player = viewModel?.Player;
        var state = player?.PlaybackState ?? MpvPlayerState.Idle;
        var playing = state == MpvPlayerState.Playing;
        PlayPauseIcon.Symbol = playing ? FluentIcons.Common.Symbol.Pause : FluentIcons.Common.Symbol.Play;
        SetName(PlayPauseButton, ResourceToolkit.GetLocalizedString(playing ? StringNames.Pause : StringNames.Play));

        // Play starts or resumes the picture, so it waits for a live release that is still unconfirmed.
        PlayPauseButton.IsEnabled = viewModel?.IsLiveReleaseBlocked != true;
        var buffering = state is MpvPlayerState.Seeking or MpvPlayerState.Buffering || player?.IsLoading == true;
        BufferingRing.IsActive = buffering;
        BufferingRing.Visibility = buffering ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = viewModel?.CanRefreshLive == true;

        Composer.IsActive = active;
        QualityEntry.IsActive = active;

        var presentation = viewModel?.Presentation ?? PlayerPresentationMode.Normal;
        var fullScreen = presentation == PlayerPresentationMode.FullScreen;
        var compact = presentation == PlayerPresentationMode.CompactOverlay;
        FullScreenIcon.Symbol = fullScreen ? FluentIcons.Common.Symbol.FullScreenMinimize : FluentIcons.Common.Symbol.FullScreenMaximize;
        CompactOverlayIcon.Symbol = compact ? FluentIcons.Common.Symbol.ContractDownLeft : FluentIcons.Common.Symbol.ContractUpRight;
        SetName(FullScreenButton, ResourceToolkit.GetLocalizedString(fullScreen ? StringNames.ExitFullScreen : StringNames.EnterFullScreen));
        SetName(CompactOverlayButton, ResourceToolkit.GetLocalizedString(compact ? StringNames.ExitCompactOverlay : StringNames.EnterCompactOverlay));

        SynchronizeVolume();
        UpdateArrangement();
    }

    /// <summary>
    /// What a narrow row or the compact window leaves out. The compact window keeps play and volume; its own
    /// title bar leaves it and enters full screen.
    /// </summary>
    private void UpdateArrangement()
    {
        var viewModel = _loaded ? ViewModel : null;
        var overlay = Layout == LiveTransportLayout.Overlay;
        var compact = viewModel?.Presentation == PlayerPresentationMode.CompactOverlay;
        var room = viewModel?.HasRoomChat == true;

        // Before the first layout the width is unknown; nothing is left out on a guess.
        var width = CommandRow.ActualWidth;
        var boxFits = width <= 0 || width >= (overlay ? OverlayBoxMinWidth : IslandBoxMinWidth);
        var commandsFit = width <= 0 || width >= (overlay ? OverlayCommandsMinWidth : IslandCommandsMinWidth);

        SetVisible(RefreshButton, !compact && commandsFit);
        SetVisible(ComposerGroup, room && !compact);
        SetVisible(Composer, boxFits);
        QualityEntry.IsEntryVisible = !compact && commandsFit;
        SetVisible(CompactOverlayButton, !compact);
        SetVisible(FullScreenButton, !compact);
    }

    private static void SetVisible(UIElement element, bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (element.Visibility != visibility) element.Visibility = visibility;
    }

    // The row follows its host, never its own content, so this cannot feed back.
    private void OnRowSizeChanged(object sender, SizeChangedEventArgs e) => UpdateArrangement();

    private async void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (!CanInteract || ViewModel is not { IsLiveReleaseBlocked: false } owner) return;
        try
        {
            await owner.PlayPauseCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            // The player shows what went wrong with the picture; nothing is retried here.
            Debug.WriteLine($"Live play or pause failed: {ex.Message}");
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (!CanInteract || ViewModel is not { CanRefreshLive: true } owner) return;
        try
        {
            await owner.RefreshLiveAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Live refresh failed: {ex.Message}");
        }
    }

    // A send of the room that did not go through: the draft stays in the box and the player tells why.
    private void OnComposerNoticed(object? sender, LiveComposerNotice notice)
        => ViewModel?.Window?.ShowTip(notice.Message, notice.Type);

    private void OnVolumeFlyoutOpened(object sender, object e)
    {
        if (!CanInteract || ViewModel is not { } owner)
        {
            IslandPopupClaims.Hide(sender);
            return;
        }

        _popups.Open(sender, owner, _epoch);
        SynchronizeVolume();
    }

    /// <summary>The slider and the icon follow the player, except while a change of the user is on its way.</summary>
    private void SynchronizeVolume()
    {
        var viewModel = _loaded ? ViewModel : null;
        var ceiling = PlayerOverlay.GetVolumeCeiling(viewModel?.MaxVolume ?? 0);
        var volume = Math.Clamp(viewModel?.Player?.Volume ?? VolumeSlider.Value, 0, ceiling);
        if (_volumeWrites == 0 && (VolumeSlider.Maximum != ceiling || Math.Abs(VolumeSlider.Value - volume) >= 0.5))
        {
            _synchronizingVolume = true;
            try
            {
                VolumeSlider.Maximum = ceiling;
                VolumeSlider.Value = volume;
            }
            finally { _synchronizingVolume = false; }
        }

        UpdateVolumeVisual(VolumeSlider.Value);
    }

    private void UpdateVolumeVisual(double volume)
    {
        var text = volume.ToString("0", CultureInfo.CurrentCulture);
        if (VolumeText.Text != text) VolumeText.Text = text;
        var symbol = volume < 0.5 || (_loaded && ViewModel?.IsMute == true) ? FluentIcons.Common.Symbol.SpeakerMute : FluentIcons.Common.Symbol.Speaker2;
        if (VolumeIcon.Symbol != symbol) VolumeIcon.Symbol = symbol;
    }

    private async void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (VolumeText is null) return; // Raised while the markup is still loading.
        UpdateVolumeVisual(e.NewValue);

        // Only a change made in the open flyout goes to the player: by pointer, by keyboard or by a screen reader.
        // What this row writes to the slider itself is marked and never comes back as a change.
        var value = e.NewValue;
        if (_synchronizingVolume || !double.IsFinite(value) || !CanInteract || ViewModel is not { } owner
            || !_popups.IsCurrent(_popups.Get(VolumeFlyout), owner, _epoch)
            || (!owner.IsMute && Math.Abs(value - owner.Player.Volume) < 1))
        {
            return;
        }

        ++_volumeWrites;
        try
        {
            // Moving the slider is asking for sound: a muted player is unmuted first.
            if (owner.IsMute) await owner.ToggleMuteStateCommand.ExecuteAsync(null);
            await owner.ChangeVolumeAsync(value);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Live volume change failed: {ex.Message}");
        }
        finally
        {
            --_volumeWrites;
        }
    }
}
