// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Controls.Danmaku;
using BiliCopilot.UI.Hosting;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace BiliCopilot.UI.Controls.Core;

/// <summary>
/// Hosts the interactive (command) danmaku cards: one card per active item, placed by
/// <see cref="CommandDanmakuPanel"/> inside the picture and clear of the header and the control bar.
/// </summary>
public sealed partial class PlayerOverlay
{
    /// <summary>Height of the header row at the top of the overlay.</summary>
    private const double CommandDanmakuHeaderHeight = 60;

    private ObservableCollection<CommandDanmakuItemViewModel>? _commandDanmakuSource;
    private InteractiveDanmakuCard? _commandDanmakuHovered;
    private long _commandDanmakuNoticeVisibilityToken;
    private long _commandDanmakuNoticeMarginToken;
    private bool _commandDanmakuChromeHooked;

    /// <summary>
    /// Gets a value indicating whether the pointer rests on a card. The controls then stay as they are:
    /// hiding them would move the card away from under the pointer.
    /// </summary>
    private bool IsCommandDanmakuPointerOver
        => _commandDanmakuHovered is { Visibility: Visibility.Visible } && CommandDanmakuLayer.Visibility == Visibility.Visible;

    /// <summary>Follows the active items of <paramref name="viewModel"/>; null stops following and removes every card.</summary>
    private void AttachCommandDanmaku(PlayerViewModel? viewModel)
    {
        if (_commandDanmakuSource is not null) _commandDanmakuSource.CollectionChanged -= OnCommandDanmakuCollectionChanged;
        _commandDanmakuSource = viewModel?.ActiveCommandDanmakus;
        if (_commandDanmakuSource is not null) _commandDanmakuSource.CollectionChanged += OnCommandDanmakuCollectionChanged;
        SynchronizeCommandDanmakuCards();
        UpdateCommandDanmakuEnvironment();
    }

    /// <summary>Watches everything that moves the header, the control bar or the SponsorBlock notice.</summary>
    private void HookCommandDanmakuChrome(bool hook)
    {
        if (_commandDanmakuChromeHooked == hook) return;
        _commandDanmakuChromeHooked = hook;
        if (hook)
        {
            SponsorNoticeHost.SizeChanged += OnCommandDanmakuChromeSizeChanged;
            _commandDanmakuNoticeVisibilityToken = SponsorNoticeHost.RegisterPropertyChangedCallback(VisibilityProperty, OnCommandDanmakuNoticeChanged);
            _commandDanmakuNoticeMarginToken = SponsorNoticeHost.RegisterPropertyChangedCallback(MarginProperty, OnCommandDanmakuNoticeChanged);
            if (IslandTransport is not null) IslandTransport.SizeChanged += OnCommandDanmakuChromeSizeChanged;
            if (TransportControls is not null) TransportControls.SizeChanged += OnCommandDanmakuChromeSizeChanged;
        }
        else
        {
            SponsorNoticeHost.SizeChanged -= OnCommandDanmakuChromeSizeChanged;
            SponsorNoticeHost.UnregisterPropertyChangedCallback(VisibilityProperty, _commandDanmakuNoticeVisibilityToken);
            SponsorNoticeHost.UnregisterPropertyChangedCallback(MarginProperty, _commandDanmakuNoticeMarginToken);
            if (IslandTransport is not null) IslandTransport.SizeChanged -= OnCommandDanmakuChromeSizeChanged;
            if (TransportControls is not null) TransportControls.SizeChanged -= OnCommandDanmakuChromeSizeChanged;
        }
    }

    /// <summary>A control bar was created or removed by a presentation change (the sender; x:Load may have cleared the field).</summary>
    private void HookCommandDanmakuTransport(FrameworkElement bar, bool hook)
    {
        bar.SizeChanged -= OnCommandDanmakuChromeSizeChanged;
        if (hook && _commandDanmakuChromeHooked) bar.SizeChanged += OnCommandDanmakuChromeSizeChanged;
        UpdateCommandDanmakuEnvironment();
    }

    private void OnCommandDanmakuChromeSizeChanged(object sender, SizeChangedEventArgs e) => UpdateCommandDanmakuEnvironment();

    private void OnCommandDanmakuNoticeChanged(DependencyObject sender, DependencyProperty property) => UpdateCommandDanmakuEnvironment();

    private void OnCommandDanmakuHostPropertyChanged(string? propertyName)
    {
        if (propertyName is null or "" or nameof(PlayerViewModel.IsControlsVisible) or nameof(PlayerViewModel.Presentation)
            or nameof(PlayerViewModel.IsEmbedded) or nameof(PlayerViewModel.UsingPanscan) or nameof(PlayerViewModel.Connector)
            or nameof(PlayerViewModel.MediaGeneration) or nameof(PlayerViewModel.IsConnecting)
            or nameof(PlayerViewModel.IsPlaybackClosing) or nameof(PlayerViewModel.ErrorMessage)
            or nameof(PlayerViewModel.CanUseInteractiveDanmaku))
        {
            UpdateCommandDanmakuEnvironment();
        }
    }

    /// <summary>
    /// Passes the current surroundings to the layer. Pure view state: no player input and no view-model call.
    /// The small window shows no cards at all: its control bar leaves no room, and a card that hid whenever
    /// the pointer approached could never be used.
    /// </summary>
    private void UpdateCommandDanmakuEnvironment()
    {
        if (CommandDanmakuLayer is null) return;
        var vm = ViewModel;
        var usable = _loaded && vm is not null && !vm.IsConnecting && !vm.IsPlaybackClosing
            && string.IsNullOrEmpty(vm.ErrorMessage) && vm.Presentation != PlayerPresentationMode.CompactOverlay
            && IsCapabilityShown(vm.CanUseInteractiveDanmaku, vm.HasAdmittedMedia);
        var visibility = usable ? Visibility.Visible : Visibility.Collapsed;
        if (CommandDanmakuLayer.Visibility != visibility) CommandDanmakuLayer.Visibility = visibility;
        if (!usable || vm is null) return;

        var top = 0d;
        var bottom = 0d;
        if (vm.IsControlsVisible)
        {
            FrameworkElement? bar = UsesOverlayTransport(vm.Presentation) ? TransportControls : IslandTransport;
            bottom = bar?.ActualHeight ?? 0;
            if (IsOverlayHeaderVisible(true, vm.IsEmbedded, vm.Presentation) == Visibility.Visible) top = CommandDanmakuHeaderHeight;
        }

        var notice = SponsorNoticeHost.Visibility == Visibility.Visible
            ? new Size(SponsorNoticeHost.ActualWidth, SponsorNoticeHost.ActualHeight) : default;
        CommandDanmakuLayer.SetEnvironment(vm.CommandDanmakuVideoAspectRatio, vm.UsingPanscan, top, bottom, notice, SponsorNoticeHost.Margin);
    }

    private void OnCommandDanmakuCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _commandDanmakuSource)) SynchronizeCommandDanmakuCards();
    }

    /// <summary>
    /// Makes the layer's cards match the active items. A card is created once and never recycled or
    /// reordered in the tree, so appearing and disappearing each animate exactly once.
    /// </summary>
    private void SynchronizeCommandDanmakuCards()
    {
        if (CommandDanmakuLayer is null) return;
        var children = CommandDanmakuLayer.Children;
        var source = _commandDanmakuSource;
        for (var index = children.Count - 1; index >= 0; index--)
        {
            if (children[index] is InteractiveDanmakuCard card && (source is null || !source.Contains(card.ViewModel!)))
                RemoveCommandDanmakuCard(card, index);
        }

        if (source is null) return;
        var animate = AreAnimationsEnabled();
        CommandDanmakuLayer.AnimationsEnabled = animate;
        for (var order = 0; order < source.Count; order++)
        {
            var item = source[order];
            InteractiveDanmakuCard? card = null;
            foreach (var child in children)
            {
                if (child is InteractiveDanmakuCard existing && ReferenceEquals(existing.ViewModel, item))
                {
                    card = existing;
                    break;
                }
            }

            if (card is null)
            {
                card = new InteractiveDanmakuCard { ViewModel = item };
                card.SetTransitions(animate);
                card.PointerEntered += OnCommandDanmakuCardPointerEntered;
                card.PointerExited += OnCommandDanmakuCardPointerExited;
                children.Add(card);
            }

            card.Order = order;
        }

        CommandDanmakuLayer.InvalidateMeasure();
    }

    private void RemoveCommandDanmakuCard(InteractiveDanmakuCard card, int index)
    {
        card.PointerEntered -= OnCommandDanmakuCardPointerEntered;
        card.PointerExited -= OnCommandDanmakuCardPointerExited;
        if (ReferenceEquals(_commandDanmakuHovered, card)) _commandDanmakuHovered = null;

        // Keyboard focus inside a card that goes away returns to the player, so its shortcuts keep working.
        var hadFocus = false;
        if (_loaded && XamlRoot is { } root)
        {
            for (var node = FocusManager.GetFocusedElement(root) as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (!ReferenceEquals(node, card)) continue;
                hadFocus = true;
                break;
            }
        }

        CommandDanmakuLayer.Children.RemoveAt(index);
        if (hadFocus) Focus(FocusState.Programmatic);
    }

    private void OnCommandDanmakuCardPointerEntered(object sender, PointerRoutedEventArgs e)
        => _commandDanmakuHovered = sender as InteractiveDanmakuCard;

    private void OnCommandDanmakuCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(_commandDanmakuHovered, sender)) _commandDanmakuHovered = null;
    }
}
