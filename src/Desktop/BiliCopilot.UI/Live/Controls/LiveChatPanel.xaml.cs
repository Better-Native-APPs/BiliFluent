#nullable enable

using System.Collections.Specialized;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace BiliCopilot.UI.Live.Controls;

/// <summary>
/// Live chat side panel: connection state, the message list, its empty state and the "back to latest" button.
/// Display only — the host supplies the bounded message collection, the connection state and the room binding.
/// The control never copies or trims messages, never sends, and knows nothing about the picture or the player.
/// </summary>
public sealed partial class LiveChatPanel : UserControl
{
    /// <summary>Suggested panel width in DIPs when the host is wide (960 and up).</summary>
    public const double RecommendedWidth = 340;

    /// <summary>Suggested panel width in DIPs for a medium host (640 to 960).</summary>
    public const double CompactWidth = 284;

    /// <summary>Smallest panel width in DIPs that still reads well; below it the host should hide the panel.</summary>
    public const double MinimumWidth = 260;

    /// <summary>Identifies the <see cref="Messages"/> property.</summary>
    public static readonly DependencyProperty MessagesProperty =
        DependencyProperty.Register(
            nameof(Messages),
            typeof(object),
            typeof(LiveChatPanel),
            new PropertyMetadata(null, (d, _) => ((LiveChatPanel)d).OnMessagesPropertyChanged()));

    /// <summary>Identifies the <see cref="ChatState"/> property.</summary>
    public static readonly DependencyProperty ChatStateProperty =
        DependencyProperty.Register(
            nameof(ChatState),
            typeof(LiveChatState),
            typeof(LiveChatPanel),
            new PropertyMetadata(LiveChatState.Idle, (d, _) => ((LiveChatPanel)d).UpdateChatState()));

    // Distance from the end, in DIPs, within which the list still counts as following the latest message.
    private const double FollowThreshold = 48;
    private const int MaxUnseen = 1000;

    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    private IReadOnlyList<LiveChatMessage>? _boundMessages;
    private INotifyCollectionChanged? _messageSource;
    private ScrollViewer? _scroller;
    private bool _active = true;
    private bool _followLatest = true;
    private int _unseen;
    private LiveBinding? _roomBinding;

    /// <summary>Initializes a new instance of the <see cref="LiveChatPanel"/> class.</summary>
    public LiveChatPanel()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue;

        ChatHeaderText.Text = LiveText.ChatHeader;
        AutomationProperties.SetName(MessageList, LiveText.ChatHeader);

        MessageList.ContainerContentChanging += OnMessageContainerChanging;
        MessageList.Loaded += OnMessageListLoaded;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        UpdateChatState();
        UpdateJumpButton();
    }

    /// <summary>
    /// The user pressed "back to latest" and the list has returned to the newest message. A host with a composer
    /// can move focus there; without a handler the keyboard focus stays in the list.
    /// </summary>
    public event EventHandler? JumpedToLatest;

    /// <summary>
    /// The room's message collection, shown as is. Pass a collection that raises
    /// <see cref="INotifyCollectionChanged"/> (the session's bounded <c>Messages</c>) so rows follow it live.
    /// </summary>
    public IReadOnlyList<LiveChatMessage>? Messages
    {
        get => GetValue(MessagesProperty) as IReadOnlyList<LiveChatMessage>;
        set => SetValue(MessagesProperty, value);
    }

    /// <summary>Chat connection state for the status dot, its caption and the empty-state wording.</summary>
    public LiveChatState ChatState
    {
        get => (LiveChatState)GetValue(ChatStateProperty);
        set => SetValue(ChatStateProperty, value);
    }

    /// <summary>
    /// The room/account binding the list belongs to. A change (another room, another account, a refresh) returns
    /// the list to following the latest message and clears the unread count. Assigning an equal value does nothing,
    /// so a host may push it on every refresh. Plain property (not a dependency property): set it from code or x:Bind.
    /// </summary>
    public LiveBinding? RoomBinding
    {
        get => _roomBinding;
        set
        {
            if (_roomBinding == value) return; // Compared by value: reading back must survive repeated pushes.
            _roomBinding = value;
            _unseen = 0;
            SetFollow(true);
            UpdateJumpButton();
        }
    }

    /// <summary>
    /// Whether the panel draws the line under its header. A host whose own header divider already runs across the
    /// panel turns it off; the row keeps its height, so the list starts where it always does. Plain property.
    /// </summary>
    public bool IsHeaderDividerVisible
    {
        get => HeaderDivider.Opacity > 0;
        set => HeaderDivider.Opacity = value ? 1 : 0;
    }

    // ---------------------------------------------------------------- lifecycle

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _active = true;
        BindMessages();
        HookScroller();
        UpdateChatState(); // The visual state needs the content root; re-apply once it is certainly there.
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) return; // Re-parenting can raise Loaded before the old Unloaded.
        _active = false;
        if (_messageSource is not null) _messageSource.CollectionChanged -= OnMessagesChanged;
        _messageSource = null;
        _boundMessages = null;
        MessageList.ItemsSource = null;
        if (_scroller is not null) _scroller.ViewChanged -= OnScrollViewChanged;
        _scroller = null;
    }

    private void OnMessagesPropertyChanged()
    {
        if (_active) BindMessages(); // While unloaded the collection is picked up again on Loaded.
    }

    private void BindMessages()
    {
        var messages = Messages;
        if (ReferenceEquals(_boundMessages, messages)) return;
        if (_messageSource is not null) _messageSource.CollectionChanged -= OnMessagesChanged;
        _boundMessages = messages;
        _messageSource = messages as INotifyCollectionChanged;
        if (_messageSource is not null) _messageSource.CollectionChanged += OnMessagesChanged;
        MessageList.ItemsSource = messages; // The host's bounded collection; never copied.
        _unseen = 0;
        SetFollow(true);
        UpdateEmptyText();
        UpdateJumpButton();
    }

    private int MessageCount => _boundMessages?.Count ?? 0;

    // ---------------------------------------------------------------- connection state

    private void UpdateChatState()
    {
        var state = ChatState;
        ChatStatusText.Text = LiveText.Chat(state);
        var visualState = state switch
        {
            LiveChatState.Connected => nameof(ChatLiveState),
            LiveChatState.Connecting or LiveChatState.Authenticating or LiveChatState.Retrying => nameof(ChatWarnState),
            LiveChatState.Failed or LiveChatState.Unavailable => nameof(ChatErrorState),
            _ => nameof(ChatIdleState),
        };
        VisualStateManager.GoToState(this, visualState, false);
        UpdateEmptyText();
    }

    private void UpdateEmptyText()
    {
        var empty = MessageCount == 0;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            var state = ChatState;
            EmptyText.Text = state == LiveChatState.Connected ? LiveText.EmptyConnected : LiveText.Chat(state);
        }
    }

    // ---------------------------------------------------------------- follow latest / read back

    private void OnMessageListLoaded(object sender, RoutedEventArgs e) => HookScroller();

    private void HookScroller()
    {
        if (_scroller is not null || !_active) return;
        _scroller = FindDescendant<ScrollViewer>(MessageList);
        if (_scroller is null) return;
        _scroller.ViewChanged += OnScrollViewChanged;
        ApplyScrollMode();
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_scroller is null || e.IsIntermediate) return;
        var atEnd = _scroller.ScrollableHeight - _scroller.VerticalOffset <= FollowThreshold;
        if (atEnd) _unseen = 0;
        SetFollow(atEnd);
        UpdateJumpButton();
    }

    private void SetFollow(bool follow)
    {
        if (_followLatest == follow) return;
        _followLatest = follow;
        ApplyScrollMode();
    }

    private void ApplyScrollMode()
    {
        // At the end: built-in bounded follow. Scrolled back: keep the rows the user is reading in place,
        // including when the host trims the oldest rows at its cap.
        if (MessageList.ItemsPanelRoot is ItemsStackPanel panel)
        {
            panel.ItemsUpdatingScrollMode = _followLatest
                ? ItemsUpdatingScrollMode.KeepLastItemInView
                : ItemsUpdatingScrollMode.KeepItemsInView;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_active || !_dispatcher.HasThreadAccess) return;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when !_followLatest:
                _unseen = Math.Min(_unseen + (e.NewItems?.Count ?? 1), MaxUnseen);
                break;
            case NotifyCollectionChangedAction.Reset:
                _unseen = 0;
                SetFollow(true);
                break;
        }

        UpdateEmptyText();
        UpdateJumpButton();
    }

    private void OnJumpClick(object sender, RoutedEventArgs e)
    {
        _unseen = 0;
        SetFollow(true);
        if (_boundMessages is { Count: > 0 } messages)
            MessageList.ScrollIntoView(messages[messages.Count - 1], ScrollIntoViewAlignment.Leading);
        UpdateJumpButton();

        // The button has just collapsed under the focus: hand it to the host, or keep it in the list.
        if (JumpedToLatest is { } handler) handler(this, EventArgs.Empty);
        else MessageList.Focus(FocusState.Programmatic);
    }

    private void UpdateJumpButton()
    {
        var show = !_followLatest && MessageCount > 0;
        JumpButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        var text = LiveText.NewMessages(_unseen);
        JumpText.Text = text;
        AutomationProperties.SetName(JumpButton, text);
    }

    // ---------------------------------------------------------------- rows

    private void OnMessageContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // A panel that was collapsed when it loaded gets its scroller only once rows are realized.
        HookScroller();

        // Records' ToString() includes binding/account data; give assistive tech only the visible text.
        if (args.InRecycleQueue || args.Item is not LiveChatMessage message) return;
        AutomationProperties.SetName(args.ItemContainer, $"{DisplayName(message)}：{message.Text}");
    }

    private static string DisplayName(LiveChatMessage message)
        => string.IsNullOrWhiteSpace(message.UserName) ? LiveText.AnonymousUser : message.UserName;

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindDescendant<T>(child);
            if (nested is not null) return nested;
        }

        return null;
    }
}
