// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Controls.Player.StoreIsland;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace BiliCopilot.UI.Controls.Core;

/// <summary>
/// The quality entry of a live room on a transport bar: the tiers the room names, with the one that plays selected.
/// The row of a media without a timeline hosts it with the button style of its bar. A media without live tiers
/// collapses the control, so the bar lays out as if it were not there. Hosts hide it through
/// <see cref="IsEntryVisible"/>, not through Visibility. The list always opens, so a room with a single tier still
/// shows which one plays. Picking another tier asks the player once; the selection moves when the player reports
/// what plays.
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
    private SourceItemViewModel? _activating;
    private bool _loaded;
    private bool _synchronizing;
    private long _epoch;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveQualityButton"/> class.
    /// </summary>
    public LiveQualityButton()
    {
        InitializeComponent();

        // Nothing shows, and no room is taken, until a media with live tiers is known.
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

    private bool IsAvailable => _loaded && ViewModel is { HasLiveQualities: true };

    // Opening the list needs a bar that takes input, not a tier to switch to.
    private bool CanInteract => IsAvailable && IsActive && ViewModel is { IsWindowDeactivated: false,
        IsPlaybackClosing: false, IsConnecting: false } && ViewModel.Player?.IsPlaybackInitialized == true;

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
            or nameof(PlayerViewModel.HasLiveQualities) or nameof(PlayerViewModel.MediaGeneration)
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
        if (EntryButton is null || QualityView is null) return;
        var available = IsAvailable;
        var viewModel = available ? ViewModel : null;

        // Collapsing the control itself leaves no gap behind in the host bar.
        Visibility = available && IsEntryVisible ? Visibility.Visible : Visibility.Collapsed;
        EntryButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        IsEnabled = CanInteract;
        if (!ReferenceEquals(EntryButton.Style, EntryStyle) && EntryStyle is not null) EntryButton.Style = EntryStyle;

        // The entry is named after what plays.
        var label = ResourceToolkit.GetLocalizedString(StringNames.Quality);
        var name = viewModel?.SelectedLiveSource is { Name: { Length: > 0 } current }
            ? string.Format(CultureInfo.CurrentCulture, ResourceToolkit.GetLocalizedString(StringNames.LiveQualityTemplate), current)
            : label;
        if (AutomationProperties.GetName(EntryButton) != name)
        {
            AutomationProperties.SetName(EntryButton, name);
            ToolTipService.SetToolTip(EntryButton, name);
        }

        // The selection is what the player reports, never what was just clicked.
        _synchronizing = true;
        try
        {
            var sources = viewModel?.LiveSources;
            if (!ReferenceEquals(QualityView.ItemsSource, sources)) QualityView.ItemsSource = sources;
            var index = viewModel?.SelectedLiveSource is { } selected ? viewModel.LiveSources.IndexOf(selected) : -1;
            if (index >= 0) QualityView.Select(index);
            else QualityView.DeselectAll();
        }
        finally { _synchronizing = false; }
    }

    private void OnFlyoutOpened(object sender, object e)
    {
        if (!CanInteract || ViewModel is not { } viewModel)
        {
            IslandPopupClaims.Hide(sender);
            return;
        }

        _popups.Open(sender, viewModel, _epoch);
        Synchronize();
    }

    // Enter, a double tap, and the invoke of a screen reader.
    private void OnQualityItemInvoked(ItemsView sender, ItemsViewItemInvokedEventArgs args)
        => Activate(args.InvokedItem, isExplicit: true);

    // While the list tracks a selection, ItemsView raises ItemInvoked only for a double tap or Enter.
    // A single click or tap on a row picks it here.
    private void OnQualityItemTapped(object sender, TappedRoutedEventArgs args)
        => Activate((sender as FrameworkElement)?.Tag, isExplicit: true);

    // Space, and the select of a screen reader, move the selection without invoking the row. That is a pick as
    // well. The selection this control sets itself is always the tier that plays, which is never a pick.
    private void OnQualitySelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (!_synchronizing) Activate(sender.SelectedItem, isExplicit: false);
    }

    private async void Activate(object? item, bool isExplicit)
    {
        if (_synchronizing || item is not SourceItemViewModel source || !CanInteract || ViewModel is not { } owner
            || !_popups.IsCurrent(_popups.Get(QualityFlyout), owner, _epoch) || !owner.LiveSources.Contains(source)
            || ReferenceEquals(source, _activating))
        {
            return;
        }

        // Picking the tier that plays changes nothing; the list has done its job.
        if (Equals(source, owner.SelectedLiveSource))
        {
            if (isExplicit) QualityFlyout.Hide();
            return;
        }

        // A room that cannot switch right now shows its tiers without taking a pick: the selection returns
        // to the tier that plays.
        if (!owner.SelectLiveSourceCommand.CanExecute(source))
        {
            Synchronize();
            return;
        }

        QualityFlyout.Hide();
        _activating = source;
        try
        {
            await owner.SelectLiveSourceCommand.ExecuteAsync(source);
        }
        catch (Exception ex)
        {
            // The player shows what went wrong with the picture; nothing is retried here.
            Debug.WriteLine($"Live quality switch failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_activating, source)) _activating = null;
        }
    }
}
