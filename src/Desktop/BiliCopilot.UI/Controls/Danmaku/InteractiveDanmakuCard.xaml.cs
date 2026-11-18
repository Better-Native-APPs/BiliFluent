// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Richasy.BiliKernel.Models.Danmaku;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;

namespace BiliCopilot.UI.Controls.Danmaku;

/// <summary>
/// One interactive danmaku card shown over the video: an uploader message, a related video,
/// a follow / like / coin prompt, a poll or a star rating, depending on the item's kind.
/// </summary>
public sealed partial class InteractiveDanmakuCard : UserControl
{
    private const double ShowMilliseconds = 250;
    private const double HideMilliseconds = 150;
    private const double MoveMilliseconds = 200;
    private const string NoticeActivity = "CommandDanmakuCard";

    private CommandDanmakuItemViewModel? _viewModel;
    private CommandDanmakuItemViewModel? _observed;
    private VideoConnectorViewModel? _observedConnector;
    private string _remainingTemplate = string.Empty;
    private int _shownSeconds = -1;
    private bool _isRatingSynchronizing;
    private bool _isLoaded;
    private CommandDanmakuVoteRow[]? _voteRows;
    private CommandDanmakuVoteRow? _pendingVoteRow;
    private Button? _pendingVoteButton;
    private FocusState _pendingVoteFocus;
    private bool _wasVoteSubmitting;
    private Rect? _arrangedSlot;
    private bool _glideRequested;
    private bool _glidedInArrange;
    private int _resizeVersion;

    /// <summary>Initializes a new instance of the <see cref="InteractiveDanmakuCard"/> class.</summary>
    public InteractiveDanmakuCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Gets or sets the item shown by this card. Set once, before the card enters the tree.</summary>
    public CommandDanmakuItemViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (ReferenceEquals(_viewModel, value)) return;
            Observe(null);
            _viewModel = value;
            Build();
            if (_isLoaded) Observe(value);
        }
    }

    /// <summary>Gets or sets the priority of the card; a lower value is placed first.</summary>
    internal int Order { get; set; }

    /// <summary>Gets the horizontal server position as 0..1, or null when the item has none.</summary>
    internal double? NormalizedX { get; private set; }

    /// <summary>Gets the vertical server position as 0..1, or null when the item has none.</summary>
    internal double? NormalizedY { get; private set; }

    /// <summary>Gets or sets the rectangle chosen by the last measure pass; null when there is no room.</summary>
    internal Rect? Slot { get; set; }

    /// <summary>Gets or sets the rectangle used by the last arrange pass.</summary>
    internal Rect? ArrangedSlot
    {
        get => _arrangedSlot;
        set
        {
            _arrangedSlot = value;

            // The panel asks for a glide before it arranges a card it may animate. No request in this
            // pass (first layout, window resize, reduced motion) means the card must not animate either.
            _glidedInArrange = _glideRequested;
            _glideRequested = false;
        }
    }

    /// <summary>Gets or sets the height last measured while the card was visible.</summary>
    internal double MeasuredHeight { get; set; }

    /// <summary>
    /// Turns the short fade and slide on appearing and disappearing on or off.
    /// Call before the card enters the tree; pass false when the system asks for reduced motion.
    /// </summary>
    internal void SetTransitions(bool enabled)
    {
        if (!enabled)
        {
            ElementCompositionPreview.SetImplicitShowAnimation(this, null);
            ElementCompositionPreview.SetImplicitHideAnimation(this, null);
            return;
        }

        ElementCompositionPreview.SetIsTranslationEnabled(this, true);
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

        var fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.Target = "Opacity";
        fadeIn.Duration = TimeSpan.FromMilliseconds(HideMilliseconds);
        fadeIn.InsertKeyFrame(0f, 0f);
        fadeIn.InsertKeyFrame(1f, 1f);
        var slideIn = compositor.CreateVector3KeyFrameAnimation();
        slideIn.Target = "Translation";
        slideIn.Duration = TimeSpan.FromMilliseconds(ShowMilliseconds);
        slideIn.InsertKeyFrame(0f, new Vector3(0f, 12f, 0f));
        slideIn.InsertKeyFrame(1f, Vector3.Zero, easing);
        var show = compositor.CreateAnimationGroup();
        show.Add(fadeIn);
        show.Add(slideIn);

        var fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.Target = "Opacity";
        fadeOut.Duration = TimeSpan.FromMilliseconds(HideMilliseconds);
        fadeOut.InsertKeyFrame(0f, 1f);
        fadeOut.InsertKeyFrame(1f, 0f);
        var slideOut = compositor.CreateVector3KeyFrameAnimation();
        slideOut.Target = "Translation";
        slideOut.Duration = TimeSpan.FromMilliseconds(HideMilliseconds);
        slideOut.InsertKeyFrame(0f, Vector3.Zero);
        slideOut.InsertKeyFrame(1f, new Vector3(0f, 8f, 0f));
        var hide = compositor.CreateAnimationGroup();
        hide.Add(fadeOut);
        hide.Add(slideOut);

        ElementCompositionPreview.SetImplicitShowAnimation(this, show);
        ElementCompositionPreview.SetImplicitHideAnimation(this, hide);
    }

    /// <summary>
    /// Lets the surface travel from its previous place to the one just arranged. The card itself is
    /// already laid out at the new place; the inner surface is moved so this never fights the
    /// appearing / disappearing animation of the card.
    /// </summary>
    internal void GlideFrom(double offsetX, double offsetY)
    {
        _glideRequested = true;
        if (Math.Abs(offsetX) < 1 && Math.Abs(offsetY) < 1) return;
        var compositor = ElementCompositionPreview.GetElementVisual(CardRoot).Compositor;
        var move = compositor.CreateVector3KeyFrameAnimation();
        move.Target = "Translation";
        move.Duration = TimeSpan.FromMilliseconds(MoveMilliseconds);
        move.InsertKeyFrame(0f, new Vector3((float)offsetX, (float)offsetY, 0f));
        move.InsertKeyFrame(1f, Vector3.Zero,
            compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));

        // The resting value is the laid-out place, whether the animation finishes or is replaced.
        CardRoot.Translation = Vector3.Zero;
        CardRoot.StartAnimation(move);
    }

    /// <inheritdoc/>
    protected override AutomationPeer OnCreateAutomationPeer() => new InteractiveDanmakuCardAutomationPeer(this);

    private static string Text(StringNames name) => ResourceToolkit.GetLocalizedString(name);

    private static string Format(StringNames template, params object[] values)
        => string.Format(CultureInfo.CurrentCulture, Text(template), values);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        if (_viewModel is not { IsActive: true } vm) return;
        Observe(vm);
        UpdateRemaining();
        UpdateAttention();
        UpdateGrade(announce: false);
        UpdateVote(announce: false);

        // Tell a screen reader that something appeared, without moving focus away from the player.
        if (FrameworkElementAutomationPeer.FromElement(this) is { } peer)
        {
            peer.RaiseNotificationEvent(AutomationNotificationKind.ItemAdded, AutomationNotificationProcessing.MostRecent,
                AutomationProperties.GetName(this), NoticeActivity);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        Observe(null);
    }

    private void Observe(CommandDanmakuItemViewModel? viewModel)
    {
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnViewModelPropertyChanged;
            foreach (var option in _observed.VoteOptions) option.PropertyChanged -= OnVoteOptionPropertyChanged;
        }

        if (_observedConnector is not null) _observedConnector.PropertyChanged -= OnConnectorPropertyChanged;
        _observed = viewModel;
        _observedConnector = viewModel?.AttentionConnector;
        if (_observed is not null)
        {
            _observed.PropertyChanged += OnViewModelPropertyChanged;
            foreach (var option in _observed.VoteOptions) option.PropertyChanged += OnVoteOptionPropertyChanged;
        }

        if (_observedConnector is not null) _observedConnector.PropertyChanged += OnConnectorPropertyChanged;
    }

    private void Build()
    {
        if (_viewModel is not { } vm) return;
        NormalizedX = CommandDanmakuPlacement.Normalize(vm.PosXPercent, vm.PosX, CommandDanmakuPlacement.ReferenceWidth);
        NormalizedY = CommandDanmakuPlacement.Normalize(vm.PosYPercent, vm.PosY, CommandDanmakuPlacement.ReferenceHeight);

        var label = Text(vm.Kind switch
        {
            CommandDanmakuKind.Link => StringNames.CommandDanmakuKindLink,
            CommandDanmakuKind.Attention => StringNames.CommandDanmakuKindAttention,
            CommandDanmakuKind.Vote => StringNames.CommandDanmakuKindVote,
            CommandDanmakuKind.Grade => StringNames.CommandDanmakuKindGrade,
            _ => StringNames.CommandDanmakuKindUp,
        });
        KindLabel.Text = label;
        KindIcon.Symbol = vm.Kind switch
        {
            CommandDanmakuKind.Link => FluentIcons.Common.Symbol.VideoClip,
            CommandDanmakuKind.Attention => FluentIcons.Common.Symbol.PersonHeart,
            CommandDanmakuKind.Vote => FluentIcons.Common.Symbol.Poll,
            CommandDanmakuKind.Grade => FluentIcons.Common.Symbol.Star,
            _ => FluentIcons.Common.Symbol.Megaphone,
        };

        var hasTitle = !string.IsNullOrWhiteSpace(vm.Title);
        TitleText.Text = vm.Title;
        TitleText.Visibility = hasTitle ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(this, hasTitle ? Format(StringNames.CommandDanmakuCardNameTemplate, label, vm.Title) : label);
        CloseButton.Command = vm.DismissCommand;
        _remainingTemplate = Text(StringNames.CommandDanmakuRemainingTemplate);

        switch (vm.Kind)
        {
            case CommandDanmakuKind.Up:
                ShowAvatar(vm.IconUrl);
                break;
            case CommandDanmakuKind.Link:
                _ = FindName(nameof(LinkSection));
                LinkSection.Command = vm.OpenLinkedVideoCommand;
                break;
            case CommandDanmakuKind.Attention:
                BuildAttention(vm);
                break;
            case CommandDanmakuKind.Vote:
                BuildVote(vm);
                break;
            case CommandDanmakuKind.Grade:
                BuildGrade(vm);
                break;
        }

        UpdateRemaining();
    }

    private void ShowAvatar(string? iconUrl)
    {
        // The uploader's picture replaces the kind badge; without one the generic person glyph is shown.
        KindBadge.Visibility = Visibility.Collapsed;
        Avatar.Visibility = Visibility.Visible;
        if (string.IsNullOrWhiteSpace(iconUrl)) return;
        var address = iconUrl.StartsWith("//", StringComparison.Ordinal) ? "https:" + iconUrl : iconUrl;
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            Avatar.ProfilePicture = new BitmapImage(uri) { DecodePixelWidth = 56, DecodePixelType = DecodePixelType.Logical };
        }
    }

    private void BuildAttention(CommandDanmakuItemViewModel vm)
    {
        var follow = vm.ShowsFollow;
        var support = vm.ShowsTripleActions;

        // Without a video connector (for example a series episode) none of the actions can run.
        if (vm.AttentionConnector is null || (!follow && !support)) return;
        _ = FindName(nameof(AttentionSection));
        if (follow)
        {
            FollowButton.Command = vm.FollowCommand;
        }
        else
        {
            FollowButton.Visibility = Visibility.Collapsed;
            Grid.SetColumn(TripleButton, 0);
            Grid.SetColumnSpan(TripleButton, 2);
        }

        if (support)
        {
            TripleButton.Command = vm.TripleCommand;
            LikeButton.Command = vm.LikeCommand;
            CoinOneButton.CommandParameter = 1;
            CoinOneButton.Command = vm.CoinCommand;
            CoinTwoButton.CommandParameter = 2;
            CoinTwoButton.Command = vm.CoinCommand;
        }
        else
        {
            TripleButton.Visibility = Visibility.Collapsed;
            SupportRow.Visibility = Visibility.Collapsed;
            Grid.SetColumnSpan(FollowButton, 2);
            Grid.SetColumnSpan(FollowedLabel, 2);
        }

        UpdateAttention();
    }

    private void UpdateAttention()
    {
        if (AttentionSection is null || _viewModel is not { AttentionConnector: { } connector } vm) return;
        if (vm.ShowsFollow)
        {
            // Already following: show the state instead of a button that would unfollow by accident.
            FollowButton.Visibility = connector.IsFollow ? Visibility.Collapsed : Visibility.Visible;
            FollowedLabel.Visibility = connector.IsFollow ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!vm.ShowsTripleActions) return;
        var likeText = Text(connector.IsLiked ? StringNames.CommandDanmakuLiked : StringNames.CommandDanmakuLike);
        LikeText.Text = likeText;
        LikeIcon.IconVariant = connector.IsLiked ? FluentIcons.Common.IconVariant.Filled : FluentIcons.Common.IconVariant.Regular;
        AutomationProperties.SetName(LikeButton, likeText);
        var coinButtons = connector.IsCoined ? Visibility.Collapsed : Visibility.Visible;
        CoinOneButton.Visibility = coinButtons;
        CoinTwoButton.Visibility = coinButtons;
        CoinedLabel.Visibility = connector.IsCoined ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildVote(CommandDanmakuItemViewModel vm)
    {
        var options = vm.VoteOptions;
        if (options.Count == 0) return;
        _ = FindName(nameof(VoteSection));
        var rows = new CommandDanmakuVoteRow[options.Count];
        for (var index = 0; index < rows.Length; index++) rows[index] = new CommandDanmakuVoteRow(options[index]);
        _voteRows = rows;
        VoteList.ItemsSource = rows;
        AutomationProperties.SetName(VoteScroller, KindLabel.Text);
        UpdateVote(announce: false);
    }

    private void UpdateVote(bool announce)
    {
        if (VoteSection is null || _voteRows is not { } rows || _viewModel is not { Kind: CommandDanmakuKind.Vote } vm) return;

        // Options are offered while a vote can still be cast. Once it is cast, or when this poll cannot
        // take a vote at all (no id, or the server did not say whether the user voted), results are shown.
        var choosing = !vm.IsSubmitted && !string.IsNullOrEmpty(vm.VoteId) && vm.MyVote == 0;
        var canChoose = choosing && vm.CanSubmit;
        long total = 0;
        foreach (var option in vm.VoteOptions) total += Math.Max(0, option.Count ?? 0);
        foreach (var row in rows)
        {
            var option = row.Option;
            var count = Math.Max(0, option.Count ?? 0);
            var percent = total > 0 ? Math.Round(count * 100d / total) : 0;
            var countText = count.ToString("N0", CultureInfo.CurrentCulture);
            row.Stats = option.Count.HasValue ? Format(StringNames.CommandDanmakuVoteStatsTemplate, countText, percent) : string.Empty;
            row.Percent = percent;
            row.IsSelected = option.IsSelected;
            row.AutomationName = Format(option.IsSelected ? StringNames.CommandDanmakuVoteOptionSelectedNameTemplate
                : StringNames.CommandDanmakuVoteOptionNameTemplate, option.Text, countText, percent);
            row.IsPending = vm.IsSubmitting && ReferenceEquals(row, _pendingVoteRow);
            row.CanChoose = canChoose && option.Index is > 0;
            row.IsChoice = choosing;
        }

        if (_wasVoteSubmitting && !vm.IsSubmitting)
        {
            // Sending is over. If it did not go through, give keyboard focus back to the option that was
            // picked, so Enter tries again; after a successful vote focus stays on the results.
            if (choosing && _pendingVoteButton is { } button && VoteScroller.FocusState != FocusState.Unfocused)
                button.Focus(_pendingVoteFocus == FocusState.Unfocused ? FocusState.Programmatic : _pendingVoteFocus);
            _pendingVoteRow = null;
            _pendingVoteButton = null;
        }

        _wasVoteSubmitting = vm.IsSubmitting;

        // The list itself takes focus only when its rows cannot: while sending and when showing results.
        VoteScroller.IsTabStop = !choosing || vm.IsSubmitting;

        // "Sending" stays until the vote has fully settled, so the total is announced once, with the new vote in it.
        var failed = choosing && vm.HasSubmissionFailed;
        var status = vm.IsSubmitting
            ? Text(StringNames.CommandDanmakuVoteSubmitting)
            : !choosing
                ? (total > 0 ? Format(StringNames.CommandDanmakuVoteSummaryTemplate, total.ToString("N0", CultureInfo.CurrentCulture)) : string.Empty)
                : failed
                    ? Text(vm.VoteSubmissionFailure == CommandDanmakuVoteFailure.SignInRequired
                        ? StringNames.CommandDanmakuVoteSignInRequired : StringNames.CommandDanmakuVoteFailed)
                    : string.Empty;
        var changed = VoteStatusText.Text != status;
        VoteStatusText.Text = status;
        VoteStatus.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        VoteFailedIcon.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        if (announce && changed && status.Length > 0)
            FrameworkElementAutomationPeer.FromElement(VoteStatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void BuildGrade(CommandDanmakuItemViewModel vm)
    {
        _ = FindName(nameof(GradeSection));
        var parts = new List<string>(2);
        if (vm.AverageStars is { } average && average > 0)
        {
            var stars = Math.Clamp(average, 0, 5);
            GradeRating.PlaceholderValue = stars;
            parts.Add(Format(StringNames.CommandDanmakuGradeAverageTemplate, stars.ToString("0.#", CultureInfo.CurrentCulture)));
        }

        if (vm.GradeParticipantCount is { } people && people > 0)
            parts.Add(Format(StringNames.CommandDanmakuGradeCountTemplate, people.ToString("N0", CultureInfo.CurrentCulture)));
        GradeSummary.Text = string.Join(" · ", parts);
        GradeSummary.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateGrade(announce: false);
    }

    private void UpdateGrade(bool announce)
    {
        if (GradeSection is null || _viewModel is not { Kind: CommandDanmakuKind.Grade } vm) return;
        if (vm.IsSubmitted && vm.SubmittedStars is { } submitted && GradeRating.Value != submitted)
        {
            _isRatingSynchronizing = true;
            GradeRating.Value = submitted;
            _isRatingSynchronizing = false;
        }

        // Stars stay editable only while a rating can still be sent: not while sending, not afterwards.
        GradeRating.IsReadOnly = !vm.CanSubmit;
        GradeSubmitButton.Visibility = vm.IsSubmitted || string.IsNullOrEmpty(vm.GradeId) ? Visibility.Collapsed : Visibility.Visible;
        GradeSubmitButton.Content = Text(vm.IsSubmitting ? StringNames.CommandDanmakuGradeSubmitting
            : vm.HasSubmissionFailed ? StringNames.CommandDanmakuGradeRetry : StringNames.CommandDanmakuGradeSubmit);
        GradeSubmitButton.IsEnabled = vm.CanSubmit && GetSelectedStars() > 0;

        var status = vm.IsSubmitted && vm.SubmittedStars is { } stars
            ? Format(StringNames.CommandDanmakuGradeSubmittedTemplate, stars)
            : vm.HasSubmissionFailed
                ? Text(vm.SubmissionFailure == CommandDanmakuGradeFailure.SignInRequired
                    ? StringNames.CommandDanmakuGradeSignInRequired : StringNames.CommandDanmakuGradeFailed)
                : string.Empty;
        var changed = GradeStatusText.Text != status;
        GradeStatusText.Text = status;
        GradeStatus.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        GradeDoneIcon.Visibility = vm.IsSubmitted ? Visibility.Visible : Visibility.Collapsed;
        GradeFailedIcon.Visibility = vm.IsSubmitted ? Visibility.Collapsed : Visibility.Visible;
        if (announce && changed && status.Length > 0)
            FrameworkElementAutomationPeer.FromElement(GradeStatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private int GetSelectedStars()
    {
        var value = GradeRating.Value;
        return value >= 1 ? (int)Math.Round(Math.Min(value, 5)) : 0;
    }

    private void UpdateRemaining()
    {
        if (_viewModel is not { } vm) return;
        var seconds = vm.RemainingSeconds;
        if (seconds == _shownSeconds) return;
        _shownSeconds = seconds;

        // One short string a second into a fixed-width block: nothing around it is measured again.
        RemainingText.Text = seconds > 0 ? string.Format(CultureInfo.CurrentCulture, _remainingTemplate, seconds) : string.Empty;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _observed)) return;
        var name = e.PropertyName;
        if (DispatcherQueue.HasThreadAccess) ApplyViewModelChange(sender, name);
        else DispatcherQueue.TryEnqueue(() => ApplyViewModelChange(sender, name));
    }

    private void ApplyViewModelChange(object? sender, string? name)
    {
        if (!ReferenceEquals(sender, _observed) || _observed is not { } vm) return;
        if (!vm.IsActive)
        {
            Freeze();
            return;
        }

        if (name is null or "" or nameof(CommandDanmakuItemViewModel.RemainingSeconds)) UpdateRemaining();
        if (name is null or "" or nameof(CommandDanmakuItemViewModel.GradeState) or nameof(CommandDanmakuItemViewModel.SubmissionFailure)
            or nameof(CommandDanmakuItemViewModel.SubmittedStars) or nameof(CommandDanmakuItemViewModel.CanSubmit))
        {
            UpdateGrade(announce: true);
        }

        if (name is null or "" or nameof(CommandDanmakuItemViewModel.VoteState) or nameof(CommandDanmakuItemViewModel.VoteSubmissionFailure)
            or nameof(CommandDanmakuItemViewModel.MyVote) or nameof(CommandDanmakuItemViewModel.CanSubmit))
        {
            UpdateVote(announce: true);
        }
    }

    private void OnVoteOptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A count or the user's choice changed: after a vote, and again when fresh counts arrive.
        if (DispatcherQueue.HasThreadAccess) ApplyVoteOptionChange();
        else DispatcherQueue.TryEnqueue(ApplyVoteOptionChange);
    }

    private void ApplyVoteOptionChange()
    {
        if (_observed is { IsActive: true }) UpdateVote(announce: false);
    }

    private void OnConnectorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _observedConnector)
            || e.PropertyName is not (null or "" or nameof(VideoConnectorViewModel.IsFollow)
                or nameof(VideoConnectorViewModel.IsLiked) or nameof(VideoConnectorViewModel.IsCoined))) return;
        if (DispatcherQueue.HasThreadAccess) ApplyConnectorChange(sender);
        else DispatcherQueue.TryEnqueue(() => ApplyConnectorChange(sender));
    }

    private void ApplyConnectorChange(object? sender)
    {
        if (ReferenceEquals(sender, _observedConnector)) UpdateAttention();
    }

    /// <summary>
    /// The item just left the timeline and the card is about to fade out. Keep it looking as it did:
    /// stop listening and drop the commands, so nothing greys out or reacts during the fade.
    /// </summary>
    private void Freeze()
    {
        Observe(null);
        IsHitTestVisible = false;
        CloseButton.Command = null;
        if (LinkSection is not null) LinkSection.Command = null;
        if (AttentionSection is not null)
        {
            FollowButton.Command = null;
            TripleButton.Command = null;
            LikeButton.Command = null;
            CoinOneButton.Command = null;
            CoinTwoButton.Command = null;
        }
    }

    private void OnGradeValueChanged(RatingControl sender, object args)
    {
        if (!_isRatingSynchronizing && _viewModel is { } vm) GradeSubmitButton.IsEnabled = vm.CanSubmit && GetSelectedStars() > 0;
    }

    private void OnGradeSubmitClick(object sender, RoutedEventArgs e)
    {
        var stars = GetSelectedStars();
        if (_viewModel is { } vm && stars > 0 && vm.SubmitGradeCommand.CanExecute(stars)) vm.SubmitGradeCommand.Execute(stars);
    }

    private void OnVoteOptionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CommandDanmakuVoteRow row } button || _viewModel is not { } vm
            || !vm.SubmitVoteCommand.CanExecute(row.Option)) return;

        // Every option is about to be disabled. Park focus on the list first so it is not thrown out of
        // the card, and remember the option so its progress ring shows and focus can return on failure.
        _pendingVoteRow = row;
        _pendingVoteButton = button;
        _pendingVoteFocus = button.FocusState;
        if (button.FocusState != FocusState.Unfocused)
        {
            VoteScroller.IsTabStop = true;
            VoteScroller.Focus(button.FocusState);
        }

        vm.SubmitVoteCommand.Execute(row.Option);
    }

    /// <summary>
    /// The card became taller or shorter (a poll turning into results, a status line appearing).
    /// Layout has already jumped to the final size and place; only the picture catches up: the surface
    /// stretches from its old height while the content is uncovered with it. Together with the glide the
    /// panel started in the same pass, the visible card moves straight from its old rectangle to the new
    /// one and never reaches outside either of them.
    /// </summary>
    private void OnCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var from = e.PreviousSize.Height;
        var to = e.NewSize.Height;
        if (!_glidedInArrange || !(from > 0) || !(to > 0) || Math.Abs(to - from) < 1
            || e.PreviousSize.Width != e.NewSize.Width) return;

        var content = ElementCompositionPreview.GetElementVisual(CardContent);
        var compositor = content.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
        var duration = TimeSpan.FromMilliseconds(MoveMilliseconds);
        var version = ++_resizeVersion;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        var stretch = compositor.CreateVector3KeyFrameAnimation();
        stretch.Target = "Scale";
        stretch.Duration = duration;
        stretch.InsertKeyFrame(0f, new Vector3(1f, (float)(from / to), 1f));
        stretch.InsertKeyFrame(1f, Vector3.One, easing);
        CardSurface.Scale = Vector3.One;
        CardSurface.StartAnimation(stretch);

        if (to > from)
        {
            // Growing: keep what lies below the old height covered until the surface gets there.
            var clip = compositor.CreateInsetClip();
            var uncover = compositor.CreateScalarKeyFrameAnimation();
            uncover.Duration = duration;
            uncover.InsertKeyFrame(0f, (float)(to - from));
            uncover.InsertKeyFrame(1f, 0f, easing);
            content.Clip = clip;
            clip.StartAnimation("BottomInset", uncover);
        }
        else
        {
            content.Clip = null;
        }

        batch.End();
        batch.Completed += (_, _) =>
        {
            // Only the latest change may clean up; an older one must not uncover a newer animation.
            if (version == _resizeVersion) content.Clip = null;
        };
    }

    // A press, tap or double tap on the card belongs to the card: the player must not pause,
    // open its menu or toggle full screen because of it.
    private void OnCardPointerHandled(object sender, PointerRoutedEventArgs e) => e.Handled = true;

    private void OnCardTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void OnCardDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => e.Handled = true;

    private void OnCardRightTapped(object sender, RightTappedRoutedEventArgs e) => e.Handled = true;

    private void OnCardHolding(object sender, HoldingRoutedEventArgs e) => e.Handled = true;
}

/// <summary>One option of a poll as the card shows it: a button to vote with, or its result.</summary>
public sealed partial class CommandDanmakuVoteRow : ObservableObject
{
    private bool _isChoice;
    private bool _canChoose;
    private bool _isPending;
    private bool _isSelected;
    private double _percent;
    private string _stats = string.Empty;
    private string _automationName = string.Empty;

    internal CommandDanmakuVoteRow(CommandDanmakuVoteOptionViewModel option) => Option = option;

    /// <summary>Gets the option text as sent by the server.</summary>
    public string Text => Option.Text;

    /// <summary>Gets or sets a value indicating whether the row is a button to vote with.</summary>
    public bool IsChoice
    {
        get => _isChoice;
        set
        {
            if (SetProperty(ref _isChoice, value)) OnPropertyChanged(nameof(IsResult));
        }
    }

    /// <summary>Gets a value indicating whether the row shows the result of the option.</summary>
    public bool IsResult => !_isChoice;

    /// <summary>Gets or sets a value indicating whether the option can be picked right now.</summary>
    public bool CanChoose
    {
        get => _canChoose;
        set => SetProperty(ref _canChoose, value);
    }

    /// <summary>Gets or sets a value indicating whether a vote for this option is being sent.</summary>
    public bool IsPending
    {
        get => _isPending;
        set => SetProperty(ref _isPending, value);
    }

    /// <summary>Gets or sets the vote count and share, ready to show.</summary>
    public string Stats
    {
        get => _stats;
        set => SetProperty(ref _stats, value);
    }

    /// <summary>Gets or sets the share of all votes, 0-100.</summary>
    public double Percent
    {
        get => _percent;
        set => SetProperty(ref _percent, value);
    }

    /// <summary>Gets or sets a value indicating whether this is the option the signed-in user chose.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Gets or sets the sentence a screen reader says for the result of this option.</summary>
    public string AutomationName
    {
        get => _automationName;
        set => SetProperty(ref _automationName, value);
    }

    /// <summary>Gets the option this row stands for; the object the vote command expects.</summary>
    internal CommandDanmakuVoteOptionViewModel Option { get; }
}

/// <summary>Presents a card to assistive technology as one named group.</summary>
internal sealed partial class InteractiveDanmakuCardAutomationPeer(InteractiveDanmakuCard owner) : FrameworkElementAutomationPeer(owner)
{
    /// <inheritdoc/>
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

    /// <inheritdoc/>
    protected override string GetClassNameCore() => nameof(InteractiveDanmakuCard);
}
