// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Xaml.Input;
using System.ComponentModel;

namespace BiliCopilot.UI.Controls.Player.StoreIsland;

/// <summary>
/// The audio track button on the right of the island transport bar. It follows <see cref="IslandFormatButton"/>:
/// the first list shows <see cref="PlayerViewModel.AudioLanguages"/>, the second <see cref="PlayerViewModel.AudioQualities"/>,
/// and one click switches to the chosen entry.
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
    private object? _activating;
    private object? _tapped;
    private long _tappedAt;

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private bool CanInteract => _loaded && IsActive && ViewModel is { IsWindowDeactivated: false,
        IsPlaybackClosing: false, IsConnecting: false, HasMultipleAudioLanguages: true }
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
        if (args.PropertyName == nameof(PlayerViewModel.MediaGeneration) || !CanInteract) CancelPendingInput();
        Synchronize();
    }

    private void Synchronize()
    {
        if (AudioView is null) return;
        _synchronizing = true;
        try
        {
            IsEnabled = CanInteract;
            var vm = _loaded ? ViewModel : null;
            AudioView.ItemsSource = vm?.AudioLanguages;
            QualityView.ItemsSource = vm?.AudioQualities;
            Select(AudioView, vm is null ? -1 : vm.AudioLanguages.IndexOf(vm.SelectedAudioLanguage!));
            Select(QualityView, vm is null ? -1 : vm.AudioQualities.IndexOf(vm.SelectedAudioQuality!));
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
        _tapped = item;
        _tappedAt = Environment.TickCount64;
        Activate(item);
    }

    private async void Activate(object? item)
    {
        if (_synchronizing || !CanInteract || !_popups.IsCurrent(_popups.Get(AudioFlyout), ViewModel, _epoch)
            || ReferenceEquals(item, _activating)) return;

        // An entry is activated only while its own list still shows it and it is not the one that plays.
        CommunityToolkit.Mvvm.Input.IAsyncRelayCommand command;
        if (item is AudioLanguageItemViewModel { IsEnabled: true } language
            && ReferenceEquals(AudioView.ItemsSource, ViewModel.AudioLanguages)
            && ViewModel.AudioLanguages.Any(entry => ReferenceEquals(entry, language))
            && !ReferenceEquals(language, ViewModel.SelectedAudioLanguage))
            command = language.ActiveCommand;
        else if (item is AudioQualityItemViewModel { IsEnabled: true } quality
            && ReferenceEquals(QualityView.ItemsSource, ViewModel.AudioQualities)
            && ViewModel.AudioQualities.Any(entry => ReferenceEquals(entry, quality))
            && !ReferenceEquals(quality, ViewModel.SelectedAudioQuality))
            command = quality.ActiveCommand;
        else return;

        _activating = item;
        try
        {
            await command.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Island audio selection failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_activating, item)) _activating = null;
        }
    }
}
