// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Controls.Player.StoreIsland;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Automation;
using System.ComponentModel;
using System.Diagnostics;
using Windows.UI.Text;

namespace BiliCopilot.UI.Controls.Core;

/// <summary>
/// The chapter entry of a transport bar: the title of the chapter that plays, and behind it the chapter list.
/// Both transport bars host it with their own button style. A media without chapters collapses the control, so the
/// bar lays out as if it were not there. Hosts hide it through <see cref="IsEntryVisible"/>, not through Visibility.
/// </summary>
public sealed partial class ChapterButton : PlayerControlBase
{
    /// <summary><see cref="IsActive"/> dependency property.</summary>
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(ChapterButton), new PropertyMetadata(false, OnActiveChanged));

    /// <summary><see cref="IsTitleVisible"/> dependency property.</summary>
    public static readonly DependencyProperty IsTitleVisibleProperty =
        DependencyProperty.Register(nameof(IsTitleVisible), typeof(bool), typeof(ChapterButton), new PropertyMetadata(true, OnLayoutChanged));

    /// <summary><see cref="IsEntryVisible"/> dependency property.</summary>
    public static readonly DependencyProperty IsEntryVisibleProperty =
        DependencyProperty.Register(nameof(IsEntryVisible), typeof(bool), typeof(ChapterButton), new PropertyMetadata(true, OnLayoutChanged));

    /// <summary><see cref="TitleMaxWidth"/> dependency property.</summary>
    public static readonly DependencyProperty TitleMaxWidthProperty =
        DependencyProperty.Register(nameof(TitleMaxWidth), typeof(double), typeof(ChapterButton), new PropertyMetadata(120d, OnLayoutChanged));

    /// <summary><see cref="EntryStyle"/> dependency property.</summary>
    public static readonly DependencyProperty EntryStyleProperty =
        DependencyProperty.Register(nameof(EntryStyle), typeof(Style), typeof(ChapterButton), new PropertyMetadata(default(Style), OnLayoutChanged));

    private readonly IslandPopupClaims _popups = new();
    private PlayerViewModel? _subscribedViewModel;
    private bool _loaded;
    private bool _openedByKeyboard;
    private long _epoch;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChapterButton"/> class.
    /// </summary>
    public ChapterButton()
    {
        InitializeComponent();

        // The host style may fix a square size for icon buttons; this one grows with its title.
        EntryButton.Width = double.NaN;

        // Nothing shows, and no margin is taken, until a media with chapters is known.
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

    /// <summary>Whether the chapter title shows next to the icon. A narrow bar turns it off first.</summary>
    public bool IsTitleVisible
    {
        get => (bool)GetValue(IsTitleVisibleProperty);
        set => SetValue(IsTitleVisibleProperty, value);
    }

    /// <summary>Whether the host has room for the entry at all.</summary>
    public bool IsEntryVisible
    {
        get => (bool)GetValue(IsEntryVisibleProperty);
        set => SetValue(IsEntryVisibleProperty, value);
    }

    /// <summary>The width at which the chapter title is trimmed.</summary>
    public double TitleMaxWidth
    {
        get => (double)GetValue(TitleMaxWidthProperty);
        set => SetValue(TitleMaxWidthProperty, value);
    }

    /// <summary>The button style of the host bar.</summary>
    public Style? EntryStyle
    {
        get => (Style?)GetValue(EntryStyleProperty);
        set => SetValue(EntryStyleProperty, value);
    }

    private bool IsAvailable => _loaded && ViewModel is { HasChapters: true, IsChapterVisible: true };

    private bool CanInteract => IsAvailable && IsActive && ViewModel is { IsWindowDeactivated: false,
        IsPlaybackClosing: false, IsConnecting: false } && ViewModel.Player?.IsPlaybackInitialized == true;

    /// <summary>Closes the chapter list. The host calls it when it cancels its own pending input.</summary>
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
        var control = (ChapterButton)sender;
        if (!(bool)args.NewValue) control.CancelPendingInput();
        control.Synchronize();
    }

    private static void OnLayoutChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((ChapterButton)sender).Synchronize();

    private static string GetIndexText(int index)
        => (index + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);

    private static string GetTimeText(double seconds)
        => ChapterPreviewTip.FormatTime(seconds);

    private static FontWeight GetTitleWeight(bool isPlaying)
        => isPlaying ? FontWeights.SemiBold : FontWeights.Normal;

    private static Uri? GetImage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;
        else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[7..];
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    private static Visibility GetImageVisibility(string? url)
        => GetImage(url) is null ? Visibility.Collapsed : Visibility.Visible;

    private static string GetItemName(ChapterItemViewModel chapter)
        => string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            ResourceToolkit.GetLocalizedString(chapter.IsPlaying ? StringNames.ChapterItemPlayingTemplate : StringNames.ChapterItemTemplate),
            chapter.Index + 1,
            chapter.Title,
            GetTimeText(chapter.Start));

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
        if (_subscribedViewModel is not null) _subscribedViewModel.PropertyChanged -= OnOwnerChanged;
        _subscribedViewModel = viewModel;
        if (viewModel is not null) viewModel.PropertyChanged += OnOwnerChanged;
    }

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _subscribedViewModel)) return;
        if (args.PropertyName is not (null or "" or nameof(PlayerViewModel.HasChapters) or nameof(PlayerViewModel.IsChapterVisible)
            or nameof(PlayerViewModel.CurrentChapter) or nameof(PlayerViewModel.MediaGeneration)
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
        if (EntryButton is null || ChapterList is null) return;
        var available = IsAvailable;
        var viewModel = available ? ViewModel : null;

        // Collapsing the control itself leaves no margin behind in the host bar.
        Visibility = available && IsEntryVisible ? Visibility.Visible : Visibility.Collapsed;
        EntryButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        IsEnabled = CanInteract;
        if (!ReferenceEquals(EntryButton.Style, EntryStyle) && EntryStyle is not null) EntryButton.Style = EntryStyle;
        if (TitleBlock.MaxWidth != TitleMaxWidth) TitleBlock.MaxWidth = TitleMaxWidth;
        TitleBlock.Visibility = IsTitleVisible ? Visibility.Visible : Visibility.Collapsed;

        // Between two chapters, or before the first one, the entry is named after the list.
        var label = ResourceToolkit.GetLocalizedString(StringNames.Chapters);
        var title = viewModel?.CurrentChapter?.Title;
        var hasTitle = !string.IsNullOrWhiteSpace(title);
        var text = hasTitle ? title! : label;
        if (TitleBlock.Text != text) TitleBlock.Text = text;
        var name = hasTitle
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, ResourceToolkit.GetLocalizedString(StringNames.ChapterCurrentTemplate), title)
            : label;
        if (AutomationProperties.GetName(EntryButton) != name)
        {
            AutomationProperties.SetName(EntryButton, name);
            ToolTipService.SetToolTip(EntryButton, name);
        }

        var source = viewModel?.Chapters;
        if (!ReferenceEquals(ChapterList.ItemsSource, source)) ChapterList.ItemsSource = source;
        if (ChapterFlyout.IsOpen) RefreshItemNames();
    }

    private void RefreshItemNames()
    {
        if (ViewModel is not { } viewModel) return;
        foreach (var chapter in viewModel.Chapters)
        {
            if (ChapterList.ContainerFromItem(chapter) is { } container) AutomationProperties.SetName(container, GetItemName(chapter));
        }
    }

    private void OnChapterContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // The row is read as one item: number, title, start time, and whether it is the one that plays.
        if (args.InRecycleQueue || args.Item is not ChapterItemViewModel chapter) return;
        AutomationProperties.SetName(args.ItemContainer, GetItemName(chapter));
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

        // Bring the chapter that plays into view and start the keyboard there.
        var current = viewModel.CurrentChapter ?? viewModel.Chapters.FirstOrDefault();
        if (current is null || !viewModel.Chapters.Contains(current)) return;
        ChapterList.ScrollIntoView(current);
        ChapterList.UpdateLayout();
        (ChapterList.ContainerFromItem(current) as Control)?.Focus(_openedByKeyboard ? FocusState.Keyboard : FocusState.Programmatic);
    }

    private async void OnChapterItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ChapterItemViewModel chapter || !CanInteract || ViewModel is not { } owner
            || !_popups.IsCurrent(_popups.Get(ChapterFlyout), owner, _epoch))
        {
            return;
        }

        // The list has done its job: close it and let the video show.
        ChapterFlyout.Hide();
        try
        {
            await owner.JumpToChapterCommand.ExecuteAsync(chapter);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Chapter jump failed: {ex.Message}");
        }
    }
}
