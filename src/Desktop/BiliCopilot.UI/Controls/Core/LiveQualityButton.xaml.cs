// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Controls.Player.StoreIsland;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Automation;
using Richasy.BiliKernel.Models.Media;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Windows.UI.Text;

namespace BiliCopilot.UI.Controls.Core;

/// <summary>
/// The quality entry of a live room on a transport bar: the tiers the room offers, with the one that plays marked.
/// Both transport bars host it with their own button style. A media without live tiers collapses the control, so
/// the bar lays out as if it were not there. Hosts hide it through <see cref="IsEntryVisible"/>, not through
/// Visibility. Picking a tier asks the player once; the mark moves when the player reports what plays.
/// </summary>
public sealed partial class LiveQualityButton : PlayerControlBase
{
    /// <summary><see cref="IsActive"/> dependency property.</summary>
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(LiveQualityButton), new PropertyMetadata(false, OnActiveChanged));

    /// <summary><see cref="IsEntryVisible"/> dependency property.</summary>
    public static readonly DependencyProperty IsEntryVisibleProperty =
        DependencyProperty.Register(nameof(IsEntryVisible), typeof(bool), typeof(LiveQualityButton), new PropertyMetadata(true, OnLayoutChanged));

    /// <summary><see cref="EntryStyle"/> dependency property.</summary>
    public static readonly DependencyProperty EntryStyleProperty =
        DependencyProperty.Register(nameof(EntryStyle), typeof(Style), typeof(LiveQualityButton), new PropertyMetadata(default(Style), OnLayoutChanged));

    private readonly IslandPopupClaims _popups = new();
    private PlayerViewModel? _subscribedViewModel;
    private bool _loaded;
    private bool _openedByKeyboard;
    private long _epoch;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveQualityButton"/> class.
    /// </summary>
    public LiveQualityButton()
    {
        InitializeComponent();

        // Nothing shows, and no margin is taken, until a media with live tiers is known.
        Visibility = Visibility.Collapsed;
        Loaded += OnLocalLoaded;
        Unloaded += OnLocalUnloaded;
    }

    /// <summary>Whether the host bar takes input.</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Whether the host has room for the entry at all.</summary>
    public bool IsEntryVisible
    {
        get => (bool)GetValue(IsEntryVisibleProperty);
        set => SetValue(IsEntryVisibleProperty, value);
    }

    /// <summary>The button style of the host bar.</summary>
    public Style? EntryStyle
    {
        get => (Style?)GetValue(EntryStyleProperty);
        set => SetValue(EntryStyleProperty, value);
    }

    // Only a live room publishes live tiers, and the tiers of a room that was left are not offered to what follows.
    private bool IsAvailable => _loaded && ViewModel is { IsLiveMedia: true, LiveSources.Count: > 0 };

    private bool CanInteract => IsAvailable && IsActive && ViewModel is { IsWindowDeactivated: false,
        IsPlaybackClosing: false, IsConnecting: false, CanSelectLiveQuality: true }
        && ViewModel.Player?.IsPlaybackInitialized == true;

    /// <summary>Closes the tier list. The host calls it when it cancels its own pending input.</summary>
    public void CancelPendingInput()
    {
        ++_epoch;
        _popups.Cancel();
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
        var control = (LiveQualityButton)sender;
        if (!(bool)args.NewValue) control.CancelPendingInput();
        control.Synchronize();
    }

    private static void OnLayoutChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((LiveQualityButton)sender).Synchronize();

    private static FontWeight GetNameWeight(bool isSelected)
        => isSelected ? FontWeights.SemiBold : FontWeights.Normal;

    // A tier whose name already says HDR needs no second mark.
    private static Visibility GetHdrTagVisibility(PlayerFormatInformation? format)
        => format is { IsHdr: true } && format.Description?.Contains("HDR", StringComparison.OrdinalIgnoreCase) != true
            ? Visibility.Visible
            : Visibility.Collapsed;

    // The name as it is read out: the eye gets the HDR mark from the tag, the ear from the name.
    private static string GetDisplayName(SourceItemViewModel source)
        => GetHdrTagVisibility(source.Data) == Visibility.Visible ? $"{source.Name} HDR" : source.Name;

    // The row is read as one item: the tier and whether it plays or is being switched to.
    private static string GetItemName(SourceItemViewModel source, bool isSwitching)
    {
        var name = GetDisplayName(source);
        return source.IsSelected
            ? string.Format(CultureInfo.CurrentCulture, ResourceToolkit.GetLocalizedString(StringNames.LiveQualityItemCurrentTemplate), name)
            : isSwitching
                ? string.Format(CultureInfo.CurrentCulture, ResourceToolkit.GetLocalizedString(StringNames.LiveQualityItemSwitchingTemplate), name)
                : name;
    }

    // A tier was asked for and the player has not reported it playing yet.
    private static bool IsSwitching(PlayerViewModel viewModel)
        => viewModel.RequestedLiveSource is { } requested && !Equals(requested, viewModel.SelectedLiveSource)
            && (viewModel.IsConnecting || viewModel.IsLiveStarting);

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
        if (ReferenceEquals(_subscribedViewModel, viewModel)) return;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnOwnerChanged;
            _subscribedViewModel.LiveSources.CollectionChanged -= OnSourcesChanged;
        }

        _subscribedViewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnOwnerChanged;
            viewModel.LiveSources.CollectionChanged += OnSourcesChanged;
        }
    }

    private void OnSourcesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _subscribedViewModel?.LiveSources)) return;

        // An open list shows the tiers of one resolve; a new set closes it.
        CancelPendingInput();
        Synchronize();
    }

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _subscribedViewModel)) return;
        if (args.PropertyName is not (null or "" or nameof(PlayerViewModel.SelectedLiveSource)
            or nameof(PlayerViewModel.RequestedLiveSource) or nameof(PlayerViewModel.CanSelectLiveQuality)
            or nameof(PlayerViewModel.IsLiveStarting) or nameof(PlayerViewModel.IsLiveMedia) or nameof(PlayerViewModel.MediaGeneration)
            or nameof(PlayerViewModel.Player) or nameof(PlayerViewModel.IsWindowDeactivated)
            or nameof(PlayerViewModel.IsConnecting) or nameof(PlayerViewModel.IsPlaybackClosing)))
        {
            return;
        }

        // An open list belongs to one media and to a bar that takes input.
        if (args.PropertyName == nameof(PlayerViewModel.MediaGeneration) || !CanInteract) CancelPendingInput();
        Synchronize();
    }

    private void Synchronize()
    {
        if (EntryButton is null || QualityList is null) return;
        var available = IsAvailable;
        var viewModel = available ? ViewModel : null;

        // Collapsing the control itself leaves no margin behind in the host bar.
        Visibility = available && IsEntryVisible ? Visibility.Visible : Visibility.Collapsed;
        EntryButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        IsEnabled = CanInteract;
        if (!ReferenceEquals(EntryButton.Style, EntryStyle) && EntryStyle is not null) EntryButton.Style = EntryStyle;

        var switching = viewModel is not null && IsSwitching(viewModel);
        SwitchingRing.IsActive = switching;
        SwitchingRing.Visibility = switching ? Visibility.Visible : Visibility.Collapsed;
        EntryIcon.Visibility = switching ? Visibility.Collapsed : Visibility.Visible;

        // The entry is named after what plays, or after the switch that is on its way.
        var label = ResourceToolkit.GetLocalizedString(StringNames.Quality);
        var current = switching
            ? ResourceToolkit.GetLocalizedString(StringNames.LiveQualitySwitching)
            : viewModel?.SelectedLiveSource is { } selected ? GetDisplayName(selected) : null;
        var name = string.IsNullOrWhiteSpace(current)
            ? label
            : string.Format(CultureInfo.CurrentCulture, ResourceToolkit.GetLocalizedString(StringNames.LiveQualityTemplate), current);
        if (AutomationProperties.GetName(EntryButton) != name)
        {
            AutomationProperties.SetName(EntryButton, name);
            ToolTipService.SetToolTip(EntryButton, name);
        }

        var source = viewModel?.LiveSources;
        if (!ReferenceEquals(QualityList.ItemsSource, source)) QualityList.ItemsSource = source;
        if (QualityFlyout.IsOpen) RefreshItemNames();
    }

    private void RefreshItemNames()
    {
        if (ViewModel is not { } viewModel) return;
        var switching = IsSwitching(viewModel);
        foreach (var source in viewModel.LiveSources)
        {
            if (QualityList.ContainerFromItem(source) is { } container)
                AutomationProperties.SetName(container, GetItemName(source, switching && Equals(source, viewModel.RequestedLiveSource)));
        }
    }

    private void OnQualityContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not SourceItemViewModel source) return;
        var switching = ViewModel is { } viewModel && IsSwitching(viewModel) && Equals(source, viewModel.RequestedLiveSource);
        AutomationProperties.SetName(args.ItemContainer, GetItemName(source, switching));
    }

    private void OnFlyoutOpening(object sender, object e)
        => _openedByKeyboard = EntryButton.FocusState == FocusState.Keyboard;

    private void OnFlyoutOpened(object sender, object e)
    {
        if (!CanInteract || ViewModel is not { } viewModel)
        {
            IslandPopupClaims.Hide(sender);
            return;
        }

        _popups.Open(sender, viewModel, _epoch);
        Synchronize();

        // Bring the tier that plays into view and start the keyboard there.
        var current = viewModel.SelectedLiveSource ?? viewModel.LiveSources.FirstOrDefault();
        if (current is null || !viewModel.LiveSources.Contains(current)) return;
        QualityList.ScrollIntoView(current);
        QualityList.UpdateLayout();
        (QualityList.ContainerFromItem(current) as Control)?.Focus(_openedByKeyboard ? FocusState.Keyboard : FocusState.Programmatic);
    }

    private async void OnQualityItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SourceItemViewModel source || !CanInteract || ViewModel is not { } owner
            || !_popups.IsCurrent(_popups.Get(QualityFlyout), owner, _epoch))
        {
            return;
        }

        // The list has done its job: close it and let the picture show. The tier that plays needs no switch.
        QualityFlyout.Hide();
        if (source.IsSelected || !owner.SelectLiveSourceCommand.CanExecute(source)) return;
        try
        {
            await owner.SelectLiveSourceCommand.ExecuteAsync(source);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Live quality switch failed: {ex.Message}");
        }
    }
}
