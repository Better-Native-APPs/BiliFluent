// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Richasy.BiliKernel.Models.Danmaku;
using Richasy.BiliKernel.Models.Media;
using System.Globalization;
using System.Text.Json;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>A reusable card state for one command in the current media generation.</summary>
public sealed partial class CommandDanmakuItemViewModel : ObservableObject
{
    private readonly PlayerViewModel _parent;
    private readonly VideoInformation? _linkedVideo;

    internal CommandDanmakuItemViewModel(PlayerViewModel parent, CommandDanmakuInformation information,
        int index, long mediaGeneration, VideoConnectorViewModel? attentionConnector)
    {
        _parent = parent;
        Index = index;
        MediaGeneration = mediaGeneration;
        Id = information.Id;
        Kind = information.Kind;
        ProgressMs = information.ProgressMs;
        Content = information.Content ?? string.Empty;
        Title = (Kind switch
        {
            CommandDanmakuKind.Grade => information.Grade?.Message,
            CommandDanmakuKind.Vote => information.Vote?.Question,
            CommandDanmakuKind.Link => information.Link?.Title,
            _ => information.Content,
        }) ?? Content;

        var extra = ReadExtra(information.ExtraRaw);
        var duration = (Kind switch
        {
            CommandDanmakuKind.Grade => information.Grade?.DurationMs,
            CommandDanmakuKind.Vote => information.Vote?.DurationMs,
            CommandDanmakuKind.Attention => information.Attention?.DurationMs,
            _ => null,
        }) ?? extra.DurationMs;
        DurationMs = duration is > 0 ? duration.Value : CommandDanmakuTimeline.DefaultDurationMs;
        PosX = information.Attention?.PosX ?? extra.PosX;
        PosY = information.Attention?.PosY ?? extra.PosY;
        PosXPercent = extra.PosXPercent;
        PosYPercent = extra.PosYPercent;
        IconUrl = (Kind switch
        {
            CommandDanmakuKind.Up => information.Up?.IconUrl,
            CommandDanmakuKind.Link => information.Link?.IconUrl,
            CommandDanmakuKind.Attention => information.Attention?.IconUrl,
            _ => null,
        }) ?? extra.IconUrl;

        AttentionConnector = Kind == CommandDanmakuKind.Attention ? attentionConnector : null;
        AttentionType = information.Attention?.AttentionType;
        GradeId = information.Grade?.GradeId;
        AverageScore = information.Grade?.AverageScore;
        GradeParticipantCount = information.Grade?.Count;
        VoteId = information.Vote?.VoteId;
        VoteType = information.Vote?.VoteType ?? 0;
        MyVote = information.Vote?.MyVote;
        VoteParticipantCount = information.Vote?.ParticipantCount;
        VoteOptions = information.Vote is { } vote
            ? Array.AsReadOnly(vote.Options.Select(option => new CommandDanmakuVoteOptionViewModel(option, vote.MyVote)).ToArray())
            : Array.Empty<CommandDanmakuVoteOptionViewModel>();
        _linkedVideo = Kind == CommandDanmakuKind.Link ? CreateLinkedVideo(information.Link) : null;
        LinkedVideoAid = _linkedVideo?.Identifier.Id;
        LinkedVideoBvid = information.Link?.Bvid;
        if (information.Grade?.MyScore is 2 or 4 or 6 or 8 or 10)
        {
            SubmittedStars = information.Grade.MyScore.Value / 2;
            GradeState = CommandDanmakuGradeState.Submitted;
        }

        if (MyVote is > 0) VoteState = CommandDanmakuVoteState.Submitted;
    }

    internal int Index { get; }

    public long MediaGeneration { get; }

    public string Id { get; }

    public CommandDanmakuKind Kind { get; }

    public string Title { get; }

    /// <summary>The original server text, without replacing it with a localized label.</summary>
    public string Content { get; }

    public string? IconUrl { get; }

    /// <summary>
    /// Raw extra.posX in Bilibili's video-overlay coordinate system, not a pixel or WinUI DIP.
    /// The server value is preserved; this model does not assume a scale or normalize it.
    /// </summary>
    public double? PosX { get; }

    /// <summary>Raw extra.posY in the same server video-overlay coordinate system as PosX.</summary>
    public double? PosY { get; }

    /// <summary>
    /// Raw extra.posX_2: the same horizontal position as a percentage (0-100) of the picture width.
    /// Parsed once at construction for every kind; null when the server sent no such field.
    /// </summary>
    public double? PosXPercent { get; }

    /// <summary>Raw extra.posY_2: the vertical position as a percentage (0-100) of the picture height.</summary>
    public double? PosYPercent { get; }

    public string PositionCoordinateSystem => "BilibiliCommandExtra";

    public int ProgressMs { get; }

    public int DurationMs { get; }

    public long EndMs => (long)ProgressMs + DurationMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemainingTime))]
    public partial int RemainingSeconds { get; private set; }

    /// <summary>Ceiling-rounded playback time remaining; changes at most once per playback second.</summary>
    public TimeSpan RemainingTime => TimeSpan.FromSeconds(RemainingSeconds);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    [NotifyCanExecuteChangedFor(nameof(DismissCommand), nameof(SubmitGradeCommand), nameof(SubmitVoteCommand), nameof(FollowCommand),
        nameof(LikeCommand), nameof(CoinCommand), nameof(TripleCommand), nameof(OpenLinkedVideoCommand))]
    public partial bool IsActive { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    [NotifyCanExecuteChangedFor(nameof(SubmitGradeCommand), nameof(SubmitVoteCommand))]
    public partial bool IsDismissed { get; private set; }

    public string? GradeId { get; }

    /// <summary>The server's average on its original 0-10 score scale.</summary>
    public double? AverageScore { get; }

    public double? AverageStars => AverageScore / 2;

    public long? GradeParticipantCount { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSubmitting), nameof(IsSubmitted), nameof(HasSubmissionFailed), nameof(CanSubmit))]
    [NotifyCanExecuteChangedFor(nameof(SubmitGradeCommand))]
    public partial CommandDanmakuGradeState GradeState { get; private set; }

    public bool IsSubmitting => Kind == CommandDanmakuKind.Vote
        ? VoteState == CommandDanmakuVoteState.Submitting : GradeState == CommandDanmakuGradeState.Submitting;

    public bool IsSubmitted => Kind == CommandDanmakuKind.Vote
        ? VoteState == CommandDanmakuVoteState.Submitted : GradeState == CommandDanmakuGradeState.Submitted;

    public bool HasSubmissionFailed => Kind == CommandDanmakuKind.Vote
        ? VoteState == CommandDanmakuVoteState.Failed : GradeState == CommandDanmakuGradeState.Failed;

    [ObservableProperty]
    public partial CommandDanmakuGradeFailure SubmissionFailure { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubmittedScore))]
    public partial int? SubmittedStars { get; private set; }

    public int? SubmittedScore => SubmittedStars * 2;

    /// <summary>An active rating or unvoted voting card is writable; voting requests determine sign-in status.</summary>
    public bool CanSubmit => IsActive && !IsDismissed && !IsSubmitting && !IsSubmitted
        && _parent.IsCurrentCommandDanmakuItem(this) && (Kind switch
        {
            CommandDanmakuKind.Grade => !string.IsNullOrEmpty(GradeId),
            CommandDanmakuKind.Vote => !string.IsNullOrEmpty(VoteId) && MyVote == 0,
            _ => false,
        });

    public string? VoteId { get; }

    public int VoteType { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    [NotifyCanExecuteChangedFor(nameof(SubmitVoteCommand))]
    public partial int? MyVote { get; private set; }

    [ObservableProperty]
    public partial long? VoteParticipantCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSubmitting), nameof(IsSubmitted), nameof(HasSubmissionFailed), nameof(CanSubmit))]
    [NotifyCanExecuteChangedFor(nameof(SubmitVoteCommand))]
    public partial CommandDanmakuVoteState VoteState { get; private set; }

    [ObservableProperty]
    public partial CommandDanmakuVoteFailure VoteSubmissionFailure { get; private set; }

    public IReadOnlyList<CommandDanmakuVoteOptionViewModel> VoteOptions { get; }

    /// <summary>Bind nested IsFollow/IsLiked/IsCoined for the live states of the reused commands.</summary>
    public VideoConnectorViewModel? AttentionConnector { get; }

    public int? AttentionType { get; }

    public bool ShowsFollow => Kind == CommandDanmakuKind.Attention && AttentionType is null or 0 or 2;

    public bool ShowsTripleActions => Kind == CommandDanmakuKind.Attention && AttentionType is null or 1 or 2;

    public string? LinkedVideoAid { get; }

    public string? LinkedVideoBvid { get; }

    internal void SetActive(bool value, long positionMs)
    {
        IsActive = value;
        if (value) UpdateRemainingTime(positionMs);
        else RemainingSeconds = 0;
    }

    internal void UpdateRemainingTime(long positionMs)
        => RemainingSeconds = (int)((Math.Max(0, EndMs - positionMs) + 999) / 1000);

    internal void MarkDismissed() => IsDismissed = true;

    internal void ResetForReplay() => IsDismissed = false;

    [RelayCommand(CanExecute = nameof(CanDismiss))]
    private void Dismiss() => _parent.DismissCommandDanmaku(this);

    private bool CanDismiss() => IsActive && _parent.IsCurrentCommandDanmakuItem(this);

    [RelayCommand(CanExecute = nameof(CanSubmitGrade))]
    private async Task SubmitGradeAsync(int stars)
    {
        if (!CanSubmitGrade(stars)) return;
        SubmissionFailure = CommandDanmakuGradeFailure.None;
        GradeState = CommandDanmakuGradeState.Submitting;
        var failure = await _parent.SubmitCommandDanmakuGradeAsync(this, stars);
        if (!_parent.IsCurrentCommandDanmakuItem(this)) return;
        SubmissionFailure = failure;
        if (failure == CommandDanmakuGradeFailure.None)
        {
            SubmittedStars = stars;
            GradeState = CommandDanmakuGradeState.Submitted;
        }
        else
        {
            GradeState = failure == CommandDanmakuGradeFailure.Canceled
                ? CommandDanmakuGradeState.NotSubmitted : CommandDanmakuGradeState.Failed;
        }
    }

    private bool CanSubmitGrade(int stars) => Kind == CommandDanmakuKind.Grade && stars is >= 1 and <= 5 && CanSubmit;

    [RelayCommand(CanExecute = nameof(CanSubmitVote))]
    private async Task SubmitVoteAsync(CommandDanmakuVoteOptionViewModel? option)
    {
        if (!CanSubmitVote(option)) return;
        VoteSubmissionFailure = CommandDanmakuVoteFailure.None;
        VoteState = CommandDanmakuVoteState.Submitting;
        var failure = await _parent.SubmitCommandDanmakuVoteAsync(this, option!);
        if (!_parent.IsCurrentCommandDanmakuItem(this)) return;
        VoteSubmissionFailure = failure;
        if (failure == CommandDanmakuVoteFailure.None)
        {
            MyVote = option!.Index;
            foreach (var voteOption in VoteOptions) voteOption.MarkVoted(MyVote);
            if (VoteParticipantCount.HasValue) VoteParticipantCount++;
            VoteState = CommandDanmakuVoteState.Submitted;
            await _parent.RefreshCommandDanmakuVoteAsync(this);
        }
        else
        {
            VoteState = failure == CommandDanmakuVoteFailure.Canceled
                ? CommandDanmakuVoteState.NotSubmitted : CommandDanmakuVoteState.Failed;
        }
    }

    private bool CanSubmitVote(CommandDanmakuVoteOptionViewModel? option)
        => Kind == CommandDanmakuKind.Vote && CanSubmit && option is { Index: > 0 } && VoteOptions.Contains(option);

    internal void RefreshVoteCounts(VoteCommandDanmakuPayload vote)
    {
        if (vote.ParticipantCount.HasValue) VoteParticipantCount = vote.ParticipantCount;
        foreach (var option in vote.Options)
        {
            if (option.Index.HasValue && option.Count.HasValue)
                VoteOptions.FirstOrDefault(item => item.Index == option.Index)?.SetCount(option.Count.Value);
        }
    }

    [RelayCommand(CanExecute = nameof(CanFollow))]
    private Task FollowAsync() => CanFollow()
        ? _parent.RunCommandDanmakuAttentionAsync(this, AttentionConnector!.ToggleFollowCommand, null) : Task.CompletedTask;

    private bool CanFollow() => CanDismiss() && ShowsFollow && AttentionConnector is not null;

    [RelayCommand(CanExecute = nameof(CanUseTripleActions))]
    private Task LikeAsync() => CanUseTripleActions()
        ? _parent.RunCommandDanmakuAttentionAsync(this, AttentionConnector!.ToggleLikeCommand, null) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanCoin))]
    private Task CoinAsync(int count) => CanCoin(count)
        ? _parent.RunCommandDanmakuAttentionAsync(this, AttentionConnector!.CoinCommand, count) : Task.CompletedTask;

    private bool CanCoin(int count) => count is 1 or 2 && CanUseTripleActions();

    [RelayCommand(CanExecute = nameof(CanUseTripleActions))]
    private Task TripleAsync() => CanUseTripleActions()
        ? _parent.RunCommandDanmakuAttentionAsync(this, AttentionConnector!.TripleCommand, null) : Task.CompletedTask;

    private bool CanUseTripleActions() => CanDismiss() && ShowsTripleActions && AttentionConnector is not null;

    [RelayCommand(CanExecute = nameof(CanOpenLinkedVideo))]
    private void OpenLinkedVideo()
    {
        if (CanOpenLinkedVideo()) _parent.OpenCommandDanmakuLinkedVideo(this, _linkedVideo!);
    }

    private bool CanOpenLinkedVideo() => CanDismiss() && _linkedVideo is not null;

    private static VideoInformation? CreateLinkedVideo(LinkCommandDanmakuPayload? link)
    {
        if (link is null) return null;
        var aid = link.Aid;
        if (!long.TryParse(aid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericAid) || numericAid <= 0)
        {
            if (string.IsNullOrEmpty(link.Bvid)) return null;
            try { aid = IdToolkit.Bv2Av(link.Bvid).ToString(CultureInfo.InvariantCulture); }
            catch (Exception) { return null; }
        }

        // Like recommendation navigation, only the identifier is needed before the full page load.
        return new VideoInformation(new MediaIdentifier(aid, link.Title, default), default, bvId: link.Bvid);
    }

    private static (int? DurationMs, double? PosX, double? PosY, double? PosXPercent, double? PosYPercent, string? IconUrl) ReadExtra(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return default;
        try
        {
            using var document = JsonDocument.Parse(raw);
            var extra = document.RootElement;
            if (extra.ValueKind != JsonValueKind.Object) return default;
            var duration = ReadScalar(extra, "duration");
            return (int.TryParse(duration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds) ? milliseconds : null,
                ReadCoordinate(extra, "posX"), ReadCoordinate(extra, "posY"),
                ReadCoordinate(extra, "posX_2"), ReadCoordinate(extra, "posY_2"), ReadScalar(extra, "icon"));
        }
        catch (JsonException) { return default; }
    }

    private static double? ReadCoordinate(JsonElement extra, string name)
        => double.TryParse(ReadScalar(extra, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value) ? value : null;

    private static string? ReadScalar(JsonElement extra, string name)
    {
        if (!extra.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }
}

public enum CommandDanmakuGradeState
{
    NotSubmitted,
    Submitting,
    Submitted,
    Failed,
}

public enum CommandDanmakuGradeFailure
{
    None,
    SignInRequired,
    RequestFailed,
    Canceled,
}

public enum CommandDanmakuVoteState
{
    NotSubmitted,
    Submitting,
    Submitted,
    Failed,
}

public enum CommandDanmakuVoteFailure
{
    None,
    SignInRequired,
    RequestFailed,
    Canceled,
}

/// <summary>A server-supplied voting option with observable count and selection state.</summary>
public sealed partial class CommandDanmakuVoteOptionViewModel : ObservableObject
{
    internal CommandDanmakuVoteOptionViewModel(VoteCommandDanmakuOption option, int? myVote)
    {
        Index = option.Index;
        Text = option.Text ?? string.Empty;
        Count = option.Count;
        HasSelfDefinition = option.HasSelfDefinition;
        IsSelected = myVote is > 0 && option.Index == myVote;
    }

    public int? Index { get; }

    public string Text { get; }

    public bool HasSelfDefinition { get; }

    [ObservableProperty]
    public partial long? Count { get; private set; }

    [ObservableProperty]
    public partial bool IsSelected { get; private set; }

    internal void MarkVoted(int? myVote)
    {
        IsSelected = myVote is > 0 && Index == myVote;
        if (IsSelected) Count = (Count ?? 0) + 1;
    }

    internal void SetCount(long count) => Count = count;
}
