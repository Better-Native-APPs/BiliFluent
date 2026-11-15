// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Xaml.Input;
using System.ComponentModel;

namespace BiliCopilot.UI.Controls.Player.StoreIsland;

/// <summary>
/// 岛式传输条右侧的字幕按钮。由 Store 源 <c>SubtitleButton</c> 迁移而来，
/// 数据源映射到当前 <see cref="PlayerViewModel.Subtitles"/> 与 <see cref="PlayerViewModel.IsSubtitleEnabled"/>。
/// 媒体带配音语言时，字幕上方另列 <see cref="PlayerViewModel.AudioLanguages"/>，点一下即切换声轨。
/// </summary>
public sealed partial class IslandSubtitleButton : PlayerControlBase
{
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(IslandSubtitleButton),
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
        IsPlaybackClosing: false, IsConnecting: false }
        && (!ViewModel.IsSubtitleEmpty || ViewModel.HasMultipleAudioLanguages)
        && ViewModel.Player?.IsPlaybackInitialized == true;

    /// <summary>
    /// Initializes a new instance of the <see cref="IslandSubtitleButton"/> class.
    /// </summary>
    public IslandSubtitleButton()
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
        var control = (IslandSubtitleButton)sender;
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
        if (args.PropertyName is nameof(PlayerViewModel.MediaGeneration) or nameof(PlayerViewModel.Subtitles)
            || !CanInteract) CancelPendingInput();
        Synchronize();
    }

    private void Synchronize()
    {
        if (SubtitleView is null) return;
        _synchronizing = true;
        try
        {
            IsEnabled = CanInteract;
            SubtitleView.ItemsSource = _loaded ? ViewModel?.Subtitles : null;
            var index = _loaded && ViewModel is { } vm ? vm.Subtitles.ToList().FindIndex(item => item.IsSelected) : -1;
            if (index >= 0) SubtitleView.Select(index);
            else SubtitleView.DeselectAll();

            AudioView.ItemsSource = _loaded ? ViewModel?.AudioLanguages : null;
            index = _loaded && ViewModel is { } owner ? owner.AudioLanguages.ToList().FindIndex(item => item.IsSelected) : -1;
            if (index >= 0) AudioView.Select(index);
            else AudioView.DeselectAll();
        }
        finally { _synchronizing = false; }
    }

    // The button shows for subtitles, and for a media that has dubbing languages even without subtitles.
    private Visibility GetMenuVisibility(bool isSubtitleEmpty, bool hasMultipleAudioLanguages)
        => !isSubtitleEmpty || hasMultipleAudioLanguages ? Visibility.Visible : Visibility.Collapsed;

    // Subtitles only: "Subtitle". Languages only: "Audio track", with the audio icon. Both: "Audio track and subtitles".
    private string GetMenuName(bool isSubtitleEmpty, bool hasMultipleAudioLanguages)
        => ResourceToolkit.GetLocalizedString(!hasMultipleAudioLanguages
            ? StringNames.Subtitle
            : isSubtitleEmpty ? StringNames.AudioTrack : StringNames.AudioTrackAndSubtitle);

    private FluentIcons.Common.Symbol GetMenuSymbol(bool isSubtitleEmpty, bool hasMultipleAudioLanguages)
        => hasMultipleAudioLanguages && isSubtitleEmpty
            ? FluentIcons.Common.Symbol.HeadphonesSoundWave : FluentIcons.Common.Symbol.ClosedCaption;

    private void OnFlyoutOpened(object sender, object e)
    {
        if (!CanInteract) { IslandPopupClaims.Hide(sender); return; }
        _popups.Open(sender, ViewModel, _epoch);
        Synchronize();
    }

    private void OnSubtitleItemInvoked(ItemsView sender, ItemsViewItemInvokedEventArgs args)
    {
        // The second click of a double click arrives here after the first one was already
        // handled as a tap; activating again would switch the chosen track off.
        if (IslandPopupClaims.IsDoubleTapEcho(args.InvokedItem, _tapped, _tappedAt)) return;
        Activate(args.InvokedItem);
    }

    private void OnSubtitleItemTapped(object sender, TappedRoutedEventArgs args)
    {
        // While the list tracks a selection, ItemsView raises ItemInvoked only for a double tap
        // or Enter. A single click or tap on an item activates it here.
        var item = (sender as FrameworkElement)?.Tag;
        _tapped = item;
        _tappedAt = Environment.TickCount64;
        Activate(item);
    }

    private void OnAudioItemInvoked(ItemsView sender, ItemsViewItemInvokedEventArgs args)
    {
        // Same as a subtitle: the second click of a double click is the activation the tap already handled.
        if (IslandPopupClaims.IsDoubleTapEcho(args.InvokedItem, _tapped, _tappedAt)) return;
        ActivateLanguage(args.InvokedItem);
    }

    private void OnAudioItemTapped(object sender, TappedRoutedEventArgs args)
    {
        var item = (sender as FrameworkElement)?.Tag;
        _tapped = item;
        _tappedAt = Environment.TickCount64;
        ActivateLanguage(item);
    }

    private async void ActivateLanguage(object? item)
    {
        var claim = _popups.Get(SubtitleFlyout);
        if (_synchronizing || !CanInteract || !_popups.IsCurrent(claim, ViewModel, _epoch)
            || !ReferenceEquals(AudioView.ItemsSource, ViewModel.AudioLanguages)
            || item is not AudioLanguageItemViewModel { IsEnabled: true } language
            || !ViewModel.AudioLanguages.Any(entry => ReferenceEquals(entry, language))
            || ReferenceEquals(language, ViewModel.SelectedAudioLanguage)
            || ReferenceEquals(language, _activating)) return;
        _activating = language;
        try
        {
            // Choosing the language that already plays does nothing; a language is never switched off.
            await language.ActiveCommand.ExecuteAsync(null);
            if (CanInteract && _popups.IsCurrent(claim, ViewModel, _epoch)) Synchronize();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Island audio language selection failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_activating, language)) _activating = null;
        }
    }

    private async void Activate(object? item)
    {
        var claim = _popups.Get(SubtitleFlyout);
        if (_synchronizing || !CanInteract || !_popups.IsCurrent(claim, ViewModel, _epoch)
            || !ReferenceEquals(SubtitleView.ItemsSource, ViewModel.Subtitles)
            || item is not SubtitleItemViewModel meta
            || !ViewModel.Subtitles.Any(subtitle => ReferenceEquals(subtitle, meta))
            || ReferenceEquals(meta, _activating)) return;
        _activating = meta;
        try
        {
            // Only explicit activation toggles an already selected track off.
            await meta.ActiveCommand.ExecuteAsync(null);
            if (CanInteract && _popups.IsCurrent(claim, ViewModel, _epoch)) Synchronize();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Island subtitle selection failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_activating, meta)) _activating = null;
        }
    }
}
