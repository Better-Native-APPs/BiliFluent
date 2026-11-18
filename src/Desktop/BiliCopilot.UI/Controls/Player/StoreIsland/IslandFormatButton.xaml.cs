// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Xaml.Input;
using System.ComponentModel;

namespace BiliCopilot.UI.Controls.Player.StoreIsland;

/// <summary>
/// 岛式传输条右侧的画质（清晰度）按钮。由 Store 源 <c>VideoPlayerFormatButton</c> 迁移而来，
/// 数据源映射到当前 <see cref="PlayerViewModel.Sources"/> / <see cref="PlayerViewModel.SelectedSource"/>；
/// 媒体带杜比或 Hi-Res 音频时，下方另列 <see cref="PlayerViewModel.AudioQualities"/>。
/// A live room is served by the same button: the list then holds the tiers of the room
/// (<see cref="PlayerViewModel.QualityMenuSources"/> / <see cref="PlayerViewModel.QualityMenuSelection"/>,
/// which are the two members above for any other media).
/// </summary>
public sealed partial class IslandFormatButton : PlayerControlBase
{
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(IslandFormatButton),
            new PropertyMetadata(false, OnActiveChanged));

    private readonly IslandPopupClaims _popups = new();
    private PlayerViewModel? _subscribedViewModel;
    private bool _loaded;
    private bool _synchronizing;
    private long _epoch;
    private object? _activating;
    private object? _tapped;
    private long _tappedAt;

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private bool CanInteract => _loaded && IsActive && ViewModel is { IsWindowDeactivated: false,
        IsPlaybackClosing: false, IsConnecting: false, HasQualityMenu: true }
        && ViewModel.Player?.IsPlaybackInitialized == true;

    /// <summary>
    /// Initializes a new instance of the <see cref="IslandFormatButton"/> class.
    /// </summary>
    public IslandFormatButton()
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
        var control = (IslandFormatButton)sender;
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
        // The list of an open menu belongs to one media; the same list reported again changes nothing.
        if (args.PropertyName is nameof(PlayerViewModel.MediaGeneration) or nameof(PlayerViewModel.Sources)
            || (args.PropertyName is nameof(PlayerViewModel.QualityMenuSources)
                && !ReferenceEquals(FormatView?.ItemsSource, _subscribedViewModel?.QualityMenuSources))
            || !CanInteract) CancelPendingInput();
        Synchronize();
    }

    private void Synchronize()
    {
        if (FormatView is null) return;
        _synchronizing = true;
        try
        {
            IsEnabled = CanInteract;
            var vm = _loaded ? ViewModel : null;
            FormatView.ItemsSource = vm?.QualityMenuSources;
            AudioQualityView.ItemsSource = vm?.AudioQualities;
            Select(FormatView, vm is null ? -1 : vm.QualityMenuSources.IndexOf(vm.QualityMenuSelection!));
            Select(AudioQualityView, vm is null ? -1 : vm.AudioQualities.IndexOf(vm.SelectedAudioQuality!));
        }
        finally { _synchronizing = false; }
    }

    private static void Select(ItemsView view, int index)
    {
        if (index >= 0) view.Select(index);
        else view.DeselectAll();
    }

    private void OnFlyoutOpened(object sender, object e)
    {
        if (!CanInteract) { IslandPopupClaims.Hide(sender); return; }
        _popups.Open(sender, ViewModel, _epoch);
        Synchronize();
    }

    private void OnFormatItemInvoked(ItemsView sender, ItemsViewItemInvokedEventArgs args)
    {
        // ItemInvoked is user activation. Select/Deselect/ItemsSource never dispatch commands.
        // The second click of a double click arrives here after the first one was already
        // handled as a tap; it is the same activation, not another one.
        if (IslandPopupClaims.IsDoubleTapEcho(args.InvokedItem, _tapped, _tappedAt)) return;
        Activate(args.InvokedItem);
    }

    private void OnFormatItemTapped(object sender, TappedRoutedEventArgs args)
    {
        // While the list tracks a selection, ItemsView raises ItemInvoked only for a double tap
        // or Enter. A single click or tap on an item activates it here.
        var item = (sender as FrameworkElement)?.Tag;
        _tapped = item;
        _tappedAt = Environment.TickCount64;
        Activate(item);
    }

    private async void Activate(object? item)
    {
        if (_synchronizing || !CanInteract || !_popups.IsCurrent(_popups.Get(FormatFlyout), ViewModel, _epoch)
            || ReferenceEquals(item, _activating)) return;

        // An entry is activated only while its own list still shows it and it is not the one in effect.
        // While the quality is automatic, the audio quality that plays can be chosen too: that keeps it.
        CommunityToolkit.Mvvm.Input.IAsyncRelayCommand command;
        if (item is SourceItemViewModel { IsEnabled: true } format
            && ReferenceEquals(FormatView.ItemsSource, ViewModel.QualityMenuSources)
            && ViewModel.QualityMenuSources.Any(entry => ReferenceEquals(entry, format))
            && !ReferenceEquals(format, ViewModel.QualityMenuSelection))
            command = format.ActiveCommand;
        else if (item is AudioQualityItemViewModel { IsEnabled: true } quality
            && ReferenceEquals(AudioQualityView.ItemsSource, ViewModel.AudioQualities)
            && ViewModel.AudioQualities.Any(entry => ReferenceEquals(entry, quality))
            && (!ReferenceEquals(quality, ViewModel.SelectedAudioQuality) || ViewModel.IsQualityAuto))
            command = quality.ActiveCommand;
        else return;

        _activating = item;
        try
        {
            await command.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Island format selection failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_activating, item)) _activating = null;

            // A pick that changed nothing leaves the list showing what plays, not what was clicked.
            Synchronize();
        }
    }
}
