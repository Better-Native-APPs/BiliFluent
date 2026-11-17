// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.Comment;
using Richasy.BiliKernel.Models.Comment;
using System.Collections.ObjectModel;
using System.Globalization;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// The poll attached to a comment: the entry shown under the comment text, and the poll itself once it is opened.
/// </summary>
public sealed partial class CommentVoteViewModel : ObservableObject
{
    // Polls without a real deadline carry an end time decades away; a date like that is not worth showing.
    private static readonly TimeSpan _farDeadline = TimeSpan.FromDays(3650);

    private readonly CommentVote _vote;
    private readonly ICommentService _service;
    private CommentVoteDetail? _detail;
    private int _loadGeneration;
    private bool _justVoted;
    private StringNames? _failure;
    private string? _failureReason;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommentVoteViewModel"/> class.
    /// </summary>
    public CommentVoteViewModel(CommentVote vote, ICommentService service)
    {
        _vote = vote;
        _service = service;
        EntryTitle = EntryCaption = EntryName = string.Empty;
        Description = Meta = Status = string.Empty;
        Title = string.IsNullOrEmpty(vote.Title) ? Text(StringNames.CommentVoteLabel) : vote.Title!;
        UpdateEntry(vote.Title, vote.ParticipantCount);
    }

    /// <summary>Gets the options of the poll, once it has been loaded.</summary>
    public ObservableCollection<CommentVoteOptionViewModel> Options { get; } = [];

    /// <summary>Gets a value indicating whether the poll is being fetched and there is nothing to show yet.</summary>
    public bool IsLoading => Phase == CommentVotePhase.Loading;

    /// <summary>Gets a value indicating whether the poll is on screen.</summary>
    public bool IsReady => Phase == CommentVotePhase.Ready;

    /// <summary>Gets a value indicating whether the poll could not be fetched.</summary>
    public bool IsFailed => Phase == CommentVotePhase.Failed;

    /// <summary>Gets a value indicating whether the poll was deleted or may not be shown.</summary>
    public bool IsUnavailable => Phase == CommentVotePhase.Unavailable;

    /// <summary>Gets a value indicating whether the entry has a second line.</summary>
    public bool HasEntryCaption => EntryCaption.Length > 0;

    /// <summary>Gets a value indicating whether the publisher wrote an introduction.</summary>
    public bool HasDescription => Description.Length > 0;

    /// <summary>Gets a value indicating whether there is a line of facts under the title.</summary>
    public bool HasMeta => Meta.Length > 0;

    /// <summary>Gets a value indicating whether there is something to say under the options.</summary>
    public bool HasStatus => Status.Length > 0;

    /// <summary>Gets or sets the first line of the entry: the poll title, or a plain "Poll".</summary>
    [ObservableProperty]
    public partial string EntryTitle { get; private set; }

    /// <summary>Gets or sets the second line of the entry.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEntryCaption))]
    public partial string EntryCaption { get; private set; }

    /// <summary>Gets or sets what a screen reader says for the entry.</summary>
    [ObservableProperty]
    public partial string EntryName { get; private set; }

    /// <summary>Gets or sets what the poll view is showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsReady), nameof(IsFailed), nameof(IsUnavailable))]
    public partial CommentVotePhase Phase { get; private set; }

    /// <summary>Gets or sets the poll title.</summary>
    [ObservableProperty]
    public partial string Title { get; private set; }

    /// <summary>Gets or sets the introduction written by the publisher.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    public partial string Description { get; private set; }

    /// <summary>Gets or sets the line of facts: how many may be chosen, how many voted, when it ends.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMeta))]
    public partial string Meta { get; private set; }

    /// <summary>Gets or sets a value indicating whether the user can choose and vote; otherwise results are shown.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial bool IsChoosing { get; private set; }

    /// <summary>Gets or sets a value indicating whether a vote is on its way.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial bool IsSubmitting { get; private set; }

    /// <summary>Gets or sets a value indicating whether to vote without showing the user's name.</summary>
    [ObservableProperty]
    public partial bool IsAnonymous { get; set; }

    /// <summary>Gets or sets the line under the options: what can still be chosen, sending, what went wrong.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; private set; }

    /// <summary>Gets or sets a value indicating whether <see cref="Status"/> reports a vote that did not go through.</summary>
    [ObservableProperty]
    public partial bool IsStatusFailure { get; private set; }

    /// <summary>
    /// Chooses an option or gives it up. One choice replaces the other in a single-choice poll;
    /// in a multiple-choice poll nothing more can be chosen once the limit is reached.
    /// </summary>
    public void ToggleOption(CommentVoteOptionViewModel option)
    {
        if (!IsChoosing || IsSubmitting || _detail is not { } detail || option is null)
        {
            return;
        }

        if (option.IsChecked)
        {
            option.IsChecked = false;
        }
        else if (detail.MaxChoiceCount <= 1)
        {
            foreach (var other in Options)
            {
                other.IsChecked = ReferenceEquals(other, option);
            }
        }
        else if (Options.Count(p => p.IsChecked) < detail.MaxChoiceCount)
        {
            option.IsChecked = true;
        }

        ClearFailure();
        UpdateRows();
        UpdateStatus();
        SubmitCommand.NotifyCanExecuteChanged();
    }

    private static string Text(StringNames name) => ResourceToolkit.GetLocalizedString(name);

    private static string Format(StringNames template, params object[] values)
        => string.Format(CultureInfo.CurrentCulture, Text(template), values);

    private static string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static bool HasEnded(CommentVoteDetail detail)
        => detail.EndTime is { } end && end <= DateTimeOffset.UtcNow;

    /// <summary>
    /// Fetches the poll. Shows what is already known while a newer copy is on its way.
    /// </summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsSubmitting)
        {
            return;
        }

        var generation = ++_loadGeneration;
        var hadDetail = _detail is not null;
        if (!hadDetail)
        {
            Phase = CommentVotePhase.Loading;
        }

        try
        {
            var detail = await _service.GetVoteDetailAsync(_vote.Id);
            if (generation != _loadGeneration || IsSubmitting)
            {
                return;
            }

            if (detail is null)
            {
                _detail = null;
                Options.Clear();
                IsChoosing = false;
                Phase = CommentVotePhase.Unavailable;
                return;
            }

            Apply(detail, keepSelection: hadDetail);
            Phase = CommentVotePhase.Ready;
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration)
            {
                return;
            }

            this.Get<ILogger<CommentVoteViewModel>>().LogWarning("Failed to load a comment poll: {ErrorType}", ex.GetType().Name);
            if (!hadDetail)
            {
                Phase = CommentVotePhase.Failed;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        if (!CanSubmit() || _detail is not { } detail)
        {
            return;
        }

        if (HasEnded(detail))
        {
            // The deadline passed while the poll was open.
            Apply(detail, keepSelection: false);
            return;
        }

        var chosen = Options.Where(p => p.IsChecked).Select(p => p.Index).ToList();
        var voterId = GetSignedInUserId();
        ClearFailure();
        _loadGeneration++;
        IsSubmitting = true;
        UpdateRows();
        UpdateStatus();
        try
        {
            var result = await _service.SubmitVoteAsync(detail, chosen, voterId ?? string.Empty, IsAnonymous);
            _justVoted = true;

            // Results first, then "no longer sending": the view moves keyboard focus on that order.
            Apply(result, keepSelection: false);
            IsSubmitting = false;
            UpdateRows();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            IsSubmitting = false;
            (_failure, _failureReason) = ex switch
            {
                UnauthorizedAccessException => (StringNames.CommandDanmakuVoteSignInRequired, default(string)),
                CommentVoteRejectedException { IsSupporterOnly: true } => (StringNames.CommentVoteSupporterOnly, null),
                CommentVoteRejectedException { Reason: { Length: > 0 } reason } => (StringNames.CommentVoteFailedReasonTemplate, reason),
                _ => (StringNames.CommandDanmakuVoteFailed, null),
            };
            this.Get<ILogger<CommentVoteViewModel>>().LogWarning("Failed to send a comment poll vote: {ErrorType}", ex.GetType().Name);
            UpdateRows();
            UpdateStatus();

            if (ex is CommentVoteRejectedException)
            {
                // The server knows something this view does not (already voted, closed): look again.
                // The reason stays on screen for as long as voting still looks possible.
                await LoadAsync();
            }
        }
    }

    private bool CanSubmit() => IsChoosing && !IsSubmitting && Options.Any(p => p.IsChecked);

    private string? GetSignedInUserId()
        => this.Get<IBiliTokenResolver>().GetToken() is { UserId: > 0 } token
            ? token.UserId.ToString(CultureInfo.InvariantCulture)
            : null;

    private void Apply(CommentVoteDetail detail, bool keepSelection)
    {
        var isMulti = detail.MaxChoiceCount > 1;
        var previous = keepSelection && _detail is not null
            ? Options.Where(p => p.IsChecked).Select(p => p.Index).ToHashSet()
            : new HashSet<int>();
        var sameOptions = _detail is not null && Options.Count == detail.Options.Count
            && _detail.MaxChoiceCount == detail.MaxChoiceCount
            && Options.Select(p => (p.Index, p.Text, p.Image)).SequenceEqual(detail.Options.Select(p => (p.Index, p.Text, p.Image)));
        _detail = detail;

        if (sameOptions)
        {
            // Only the numbers moved: keep the rows, and with them keyboard focus and scroll position.
            for (var i = 0; i < Options.Count; i++)
            {
                Options[i].Count = detail.Options[i].Count;
                Options[i].IsChecked = previous.Contains(Options[i].Index);
            }
        }
        else
        {
            Options.Clear();
            foreach (var option in detail.Options)
            {
                Options.Add(new CommentVoteOptionViewModel(option, isMulti) { IsChecked = previous.Contains(option.Index) });
            }
        }

        var userId = GetSignedInUserId();
        var isMine = userId is not null && userId == detail.PublisherId;
        IsChoosing = userId is not null && !isMine && detail.MyVotes.Count == 0 && !HasEnded(detail);
        if (!IsChoosing)
        {
            ClearFailure();
        }

        Title = detail.Title;
        Description = detail.Description ?? string.Empty;
        UpdateEntry(detail.Title, detail.ParticipantCount);
        UpdateMeta(detail);
        UpdateRows();
        UpdateStatus();
        SubmitCommand.NotifyCanExecuteChanged();
    }

    private void UpdateEntry(string? title, long? participantCount)
    {
        var label = Text(StringNames.CommentVoteLabel);
        var hasTitle = !string.IsNullOrWhiteSpace(title);
        var hasCount = participantCount is > 0;
        var people = hasCount ? Format(StringNames.CommentVoteParticipantsTemplate, AppToolkit.FormatCount(participantCount!.Value)) : string.Empty;
        EntryTitle = hasTitle ? title! : label;
        EntryCaption = hasTitle ? (hasCount ? $"{label} · {people}" : label) : people;
        EntryName = hasTitle
            ? (hasCount
                ? Format(StringNames.CommentVoteEntryNameWithCountTemplate, title!, Number(participantCount!.Value))
                : Format(StringNames.CommentVoteEntryNameTemplate, title!))
            : (hasCount ? $"{label}, {people}" : label);
    }

    private void UpdateMeta(CommentVoteDetail detail)
    {
        var parts = new List<string>
        {
            detail.MaxChoiceCount > 1
                ? Format(StringNames.CommentVoteMultiChoiceTemplate, detail.MaxChoiceCount)
                : Text(StringNames.CommentVoteSingleChoice),
            Format(StringNames.CommentVoteParticipantsTemplate, Number(detail.ParticipantCount)),
        };

        if (HasEnded(detail))
        {
            parts.Add(Text(StringNames.CommentVoteEnded));
        }
        else if (detail.EndTime is { } end && end - DateTimeOffset.UtcNow < _farDeadline)
        {
            parts.Add(Format(StringNames.CommentVoteDeadlineTemplate, end.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        }

        Meta = string.Join(" · ", parts);
    }

    private void UpdateRows()
    {
        if (_detail is not { } detail)
        {
            return;
        }

        // Shares are of all votes cast, as on the official page; in a multiple-choice poll that is more than the voters.
        long total = 0;
        foreach (var option in Options)
        {
            total += Math.Max(0, option.Count);
        }

        var checkedCount = Options.Count(p => p.IsChecked);
        var isFull = detail.MaxChoiceCount > 1 && checkedCount >= detail.MaxChoiceCount;
        foreach (var option in Options)
        {
            var percent = total > 0 ? Math.Round(option.Count * 100d / total) : 0;
            var count = Number(option.Count);
            option.IsMine = detail.MyVotes.Contains(option.Index);
            option.Percent = percent;
            option.Stats = Format(StringNames.CommandDanmakuVoteStatsTemplate, count, percent);
            option.ResultName = Format(
                option.IsMine ? StringNames.CommandDanmakuVoteOptionSelectedNameTemplate : StringNames.CommandDanmakuVoteOptionNameTemplate,
                option.Text,
                count,
                percent);
            option.CanChoose = IsChoosing && !IsSubmitting && (option.IsChecked || !isFull);
            option.IsChoice = IsChoosing;
        }
    }

    private void UpdateStatus()
    {
        if (_detail is not { } detail)
        {
            Status = string.Empty;
            IsStatusFailure = false;
            return;
        }

        var failed = false;
        string status;
        if (IsSubmitting)
        {
            status = Text(StringNames.CommandDanmakuVoteSubmitting);
        }
        else if (IsChoosing)
        {
            if (_failure is { } failure)
            {
                failed = true;
                status = _failureReason is { } reason ? Format(failure, reason) : Text(failure);
            }
            else if (detail.MaxChoiceCount > 1)
            {
                var left = detail.MaxChoiceCount - Options.Count(p => p.IsChecked);
                status = left > 0
                    ? Format(StringNames.CommentVoteRemainingTemplate, left)
                    : Format(StringNames.CommentVoteLimitReachedTemplate, detail.MaxChoiceCount);
            }
            else
            {
                status = detail.IsSupporterOnly ? Text(StringNames.CommentVoteSupporterOnly) : string.Empty;
            }
        }
        else if (_justVoted)
        {
            status = Text(StringNames.CommentVoteDone);
        }
        else if (HasEnded(detail) || detail.MyVotes.Count > 0)
        {
            status = string.Empty;
        }
        else
        {
            var userId = GetSignedInUserId();
            status = userId is null
                ? Text(StringNames.CommandDanmakuVoteSignInRequired)
                : userId == detail.PublisherId ? Text(StringNames.CommentVoteOwn) : string.Empty;
        }

        IsStatusFailure = failed;
        Status = status;
    }

    private void ClearFailure()
    {
        _failure = null;
        _failureReason = null;
    }
}

/// <summary>
/// What the view of a comment poll is showing.
/// </summary>
public enum CommentVotePhase
{
    /// <summary>Not opened yet.</summary>
    Idle,

    /// <summary>The poll is being fetched and nothing is known yet.</summary>
    Loading,

    /// <summary>The poll is on screen.</summary>
    Ready,

    /// <summary>The poll could not be fetched.</summary>
    Failed,

    /// <summary>The poll was deleted or may not be shown.</summary>
    Unavailable,
}
