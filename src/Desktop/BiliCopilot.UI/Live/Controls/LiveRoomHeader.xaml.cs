#nullable enable

using System.Globalization;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media.Imaging;
using Richasy.BiliKernel.Models.User;

namespace BiliCopilot.UI.Live.Controls;

/// <summary>
/// Live room header: anchor avatar and name, room title, follow tag, audience count, playback state and the
/// chat panel toggle. Display only — the host supplies every value through a property and handles every action
/// through an event. The control knows nothing about the picture, the player or the page that hosts it.
/// </summary>
public sealed partial class LiveRoomHeader : UserControl
{
    private const string FollowingCopy = "已关注";
    private const string NotFollowingCopy = "未关注";

    /// <summary>Identifies the <see cref="Title"/> property.</summary>
    public static readonly DependencyProperty TitleProperty =
        Register(nameof(Title), typeof(string), string.Empty, header => header.UpdateTitle());

    /// <summary>Identifies the <see cref="AnchorName"/> property.</summary>
    public static readonly DependencyProperty AnchorNameProperty =
        Register(nameof(AnchorName), typeof(string), string.Empty, header => header.UpdateAnchor());

    /// <summary>Identifies the <see cref="AnchorAvatar"/> property.</summary>
    public static readonly DependencyProperty AnchorAvatarProperty =
        Register(nameof(AnchorAvatar), typeof(Uri), null, header => header.UpdateAvatar());

    /// <summary>Identifies the <see cref="Anchor"/> property.</summary>
    public static readonly DependencyProperty AnchorProperty =
        Register(nameof(Anchor), typeof(UserProfile), null, header => header.UpdateAnchor());

    /// <summary>Identifies the <see cref="IsFollowing"/> property.</summary>
    public static readonly DependencyProperty IsFollowingProperty =
        Register(nameof(IsFollowing), typeof(object), null, header => header.UpdateFollow());

    /// <summary>Identifies the <see cref="AudienceCount"/> property.</summary>
    public static readonly DependencyProperty AudienceCountProperty =
        Register(nameof(AudienceCount), typeof(object), null, header => header.UpdateAudience());

    /// <summary>Identifies the <see cref="AudienceLabel"/> property.</summary>
    public static readonly DependencyProperty AudienceLabelProperty =
        Register(nameof(AudienceLabel), typeof(string), string.Empty, header => header.UpdateAudience());

    /// <summary>Identifies the <see cref="PlaybackState"/> property.</summary>
    public static readonly DependencyProperty PlaybackStateProperty =
        Register(nameof(PlaybackState), typeof(LivePlaybackState), LivePlaybackState.Idle, header => header.UpdatePlayback());

    /// <summary>Identifies the <see cref="IsChatOpen"/> property.</summary>
    public static readonly DependencyProperty IsChatOpenProperty =
        Register(nameof(IsChatOpen), typeof(bool), true, header => header.UpdateChatToggle());

    /// <summary>Identifies the <see cref="IsChatToggleVisible"/> property.</summary>
    public static readonly DependencyProperty IsChatToggleVisibleProperty =
        Register(nameof(IsChatToggleVisible), typeof(bool), true, header => header.UpdateChatToggle());

    private Uri? _shownAvatar;
    private bool _isBackdropVisible = true;

    /// <summary>Initializes a new instance of the <see cref="LiveRoomHeader"/> class.</summary>
    public LiveRoomHeader()
    {
        InitializeComponent();
        AutomationProperties.SetName(ChatToggle, LiveText.ChatToggleName);
        ToolTipService.SetToolTip(ChatToggle, LiveText.ChatToggleName); // Icon-only command: the name is the tooltip.
        UpdateTitle();
        UpdateAnchor();
        UpdateFollow();
        UpdateAudience();
        UpdatePlayback();
        UpdateChatToggle();
        Loaded += OnLoaded;
    }

    /// <summary>The anchor avatar was invoked. The argument is the current <see cref="Anchor"/>; the host navigates.</summary>
    public event EventHandler<UserProfile>? AnchorInvoked;

    /// <summary>The chat toggle was invoked. The host decides whether the panel opens and reports it via <see cref="IsChatOpen"/>.</summary>
    public event EventHandler? ChatToggleRequested;

    /// <summary>Room title. Blank shows the generic room name.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value ?? string.Empty);
    }

    /// <summary>Anchor display name. Blank hides the name.</summary>
    public string AnchorName
    {
        get => (string)GetValue(AnchorNameProperty);
        set => SetValue(AnchorNameProperty, value ?? string.Empty);
    }

    /// <summary>Anchor avatar. Null or a relative URI falls back to the initials of <see cref="AnchorName"/>.</summary>
    public Uri? AnchorAvatar
    {
        get => (Uri?)GetValue(AnchorAvatarProperty);
        set => SetValue(AnchorAvatarProperty, value is { IsAbsoluteUri: true } ? value : null);
    }

    /// <summary>The room owner's profile. The avatar button is enabled once it carries an id.</summary>
    public UserProfile? Anchor
    {
        get => (UserProfile?)GetValue(AnchorProperty);
        set => SetValue(AnchorProperty, value);
    }

    /// <summary>Follow state. Null (unknown) hides the tag.</summary>
    public bool? IsFollowing
    {
        get => GetValue(IsFollowingProperty) as bool?;
        set => SetValue(IsFollowingProperty, value);
    }

    /// <summary>Audience count. Null (not received yet) hides the metric.</summary>
    public long? AudienceCount
    {
        get => GetValue(AudienceCountProperty) switch
        {
            long count => count,
            int count => count,
            _ => null,
        };
        set => SetValue(AudienceCountProperty, value);
    }

    /// <summary>Caption in front of <see cref="AudienceCount"/>, chosen by the room.</summary>
    public string AudienceLabel
    {
        get => (string)GetValue(AudienceLabelProperty);
        set => SetValue(AudienceLabelProperty, value ?? string.Empty);
    }

    /// <summary>Playback state for the status dot and caption. It comes from the player side, not from the room.</summary>
    public LivePlaybackState PlaybackState
    {
        get => (LivePlaybackState)GetValue(PlaybackStateProperty);
        set => SetValue(PlaybackStateProperty, value);
    }

    /// <summary>Whether the chat panel is currently shown. Only selects the toggle glyph.</summary>
    public bool IsChatOpen
    {
        get => (bool)GetValue(IsChatOpenProperty);
        set => SetValue(IsChatOpenProperty, value);
    }

    /// <summary>Whether the header shows its own chat toggle. A host with another panel switch can turn it off.</summary>
    public bool IsChatToggleVisible
    {
        get => (bool)GetValue(IsChatToggleVisibleProperty);
        set => SetValue(IsChatToggleVisibleProperty, value);
    }

    /// <summary>
    /// Whether the header paints its own card fill. A host that already paints the header row turns it off, so
    /// the fill is not laid twice. Plain property (not a dependency property): set it from code or markup.
    /// </summary>
    public bool IsBackdropVisible
    {
        get => _isBackdropVisible;
        set
        {
            if (_isBackdropVisible == value) return;
            _isBackdropVisible = value;
            Backdrop.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static DependencyProperty Register(string name, Type type, object? defaultValue, Action<LiveRoomHeader> changed)
        => DependencyProperty.Register(
            name,
            type,
            typeof(LiveRoomHeader),
            new PropertyMetadata(defaultValue, (d, _) => changed((LiveRoomHeader)d)));

    // The visual state needs the content root; re-apply once it is certainly there.
    private void OnLoaded(object sender, RoutedEventArgs e) => UpdatePlayback();

    private void UpdateTitle()
        => TitleText.Text = string.IsNullOrWhiteSpace(Title) ? LiveText.UntitledRoom : Title;

    /// <summary>Anchor name, initials fallback, avatar button availability and its spoken name.</summary>
    private void UpdateAnchor()
    {
        var anchor = AnchorName;
        var hasAnchor = !string.IsNullOrWhiteSpace(anchor);
        AnchorNameText.Text = hasAnchor ? anchor : string.Empty;
        AnchorNameText.Visibility = hasAnchor ? Visibility.Visible : Visibility.Collapsed;
        AnchorPicture.DisplayName = hasAnchor ? anchor : string.Empty; // Initials fallback while/if the image is unavailable.
        AnchorButton.IsEnabled = !string.IsNullOrEmpty(Anchor?.Id);
        var spaceName = ResourceToolkit.GetLocalizedString(StringNames.UserSpace);
        var anchorAction = hasAnchor ? $"{anchor}：{spaceName}" : spaceName;
        AutomationProperties.SetName(AnchorButton, anchorAction);
        ToolTipService.SetToolTip(AnchorButton, anchorAction);
        UpdateMetaRow();
    }

    private void UpdateAvatar()
    {
        var avatar = AnchorAvatar;
        if (Equals(avatar, _shownAvatar)) return; // Only rebuild the image when the URI really changes.
        _shownAvatar = avatar;
        AnchorPicture.ProfilePicture = avatar is { IsAbsoluteUri: true }
            ? new BitmapImage(avatar) { DecodePixelWidth = 72, DecodePixelType = DecodePixelType.Logical }
            : null;
    }

    /// <summary>Follow tag: hidden while the state is unknown.</summary>
    private void UpdateFollow()
    {
        var following = IsFollowing;
        FollowTag.Visibility = following is null ? Visibility.Collapsed : Visibility.Visible;
        if (following is { } isFollowing) FollowText.Text = isFollowing ? FollowingCopy : NotFollowingCopy;
        UpdateMetaRow();
    }

    /// <summary>Audience metric: hidden until the room has reported a count.</summary>
    private void UpdateAudience()
    {
        var count = AudienceCount;
        AudiencePanel.Visibility = count is null ? Visibility.Collapsed : Visibility.Visible;
        if (count is { } audience)
        {
            AudienceText.Text = string.Format(CultureInfo.CurrentCulture, "{0} {1:N0}", AudienceLabel, audience);
            AutomationProperties.SetName(AudiencePanel, AudienceText.Text);
        }

        UpdateMetaRow();
    }

    private void UpdateMetaRow()
        => AnchorMetaRow.Visibility = !string.IsNullOrWhiteSpace(AnchorName) || IsFollowing is not null || AudienceCount is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void UpdatePlayback()
    {
        var state = PlaybackState;
        PlaybackStatusText.Text = LiveText.Playback(state);
        var visualState = state switch
        {
            LivePlaybackState.Playing => nameof(PlaybackLiveState),
            LivePlaybackState.Opening or LivePlaybackState.Paused => nameof(PlaybackWarnState),
            LivePlaybackState.Failed or LivePlaybackState.Ended => nameof(PlaybackErrorState),
            _ => nameof(PlaybackIdleState),
        };
        VisualStateManager.GoToState(this, visualState, false);
    }

    private void UpdateChatToggle()
    {
        ChatToggle.Visibility = IsChatToggleVisible ? Visibility.Visible : Visibility.Collapsed;
        ChatToggleIcon.Symbol = IsChatOpen
            ? FluentIcons.Common.Symbol.PanelRightContract
            : FluentIcons.Common.Symbol.PanelRightExpand;
    }

    private void OnAnchorClick(object sender, RoutedEventArgs e)
    {
        if (Anchor is { } anchor) AnchorInvoked?.Invoke(this, anchor);
    }

    private void OnChatToggleClick(object sender, RoutedEventArgs e)
        => ChatToggleRequested?.Invoke(this, EventArgs.Empty);
}
