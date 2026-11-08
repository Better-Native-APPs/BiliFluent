// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Persistence;
using BiliCopilot.UI.SponsorBlock.Playback;
using BiliCopilot.UI.SponsorBlock.Rules;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

public sealed partial class SponsorPlaybackViewModel
{
    private sealed record SegmentProjection(PlaybackOwner Owner, ImmutableArray<SponsorSegment> Raw,
        SponsorLocalSettings Settings, SponsorPlaybackRules.PreparedSelection? Selection,
        ImmutableArray<PlaybackMarker> Markers, ImmutableArray<PlaybackDiagnostic> Diagnostics,
        double? Duration, ImmutableArray<SponsorAvailability> Availability);

    private sealed record NoticeProjection(PlaybackOwner? Owner, ImmutableArray<PlaybackNotice> Notices,
        SponsorSkipStrategy Strategy, bool CanSeek);

    private SegmentProjection? _segmentProjection;
    private NoticeProjection? _noticeProjection;

    private static bool SameItems<T>(ImmutableArray<T> left, ImmutableArray<T> right)
    {
        if (left.Equals(right)) return true;
        if (left.IsDefault || right.IsDefault || left.Length != right.Length) return false;
        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < left.Length; i++)
            if (!comparer.Equals(left[i], right[i])) return false;
        return true;
    }

    private static SponsorPlayerView? PlayerFor(SponsorApplicationView view, Guid attachment)
    {
        if (!view.Players.IsDefault)
            foreach (var player in view.Players)
                if (player.Attachment == attachment) return player;
        return null;
    }

    // APP-thread comparison of immutable values only. The complete APP snapshot/tickets continue
    // advancing; only this playback view's redundant dispatcher hop is omitted.
    private static bool SamePlaybackView(SponsorApplicationView left, SponsorApplicationView right, Guid attachment)
    {
        if (!ReferenceEquals(left.Settings, right.Settings) || left.Closed != right.Closed) return false;
        var before = PlayerFor(left, attachment);
        var after = PlayerFor(right, attachment);
        if (before is null || after is null) return before == after;
        // Automatic fetch readiness uses complete stamps until a real read outcome is published.
        if (before.ReadStatus == ReadStatus.Disabled || after.ReadStatus == ReadStatus.Disabled) return false;
        return before.ReadStatus == after.ReadStatus && before.RawSegments.Equals(after.RawSegments)
            && before.Channel == after.Channel && before.KnownMusic == after.KnownMusic
            && before.Editing == after.Editing && before.Preview == after.Preview && before.Attached == after.Attached
            && before.DeliveryStatus == after.DeliveryStatus && before.VerifiedUpUid == after.VerifiedUpUid
            && before.CleanupPending == after.CleanupPending && SameItems(before.EditorCapabilities, after.EditorCapabilities)
            && SameControllerView(before.Controller, after.Controller);
    }

    private static bool SameStamp(PlaybackStamp? left, PlaybackStamp? right)
        => left == right || left is not null && left.HasSameContext(right);

    private static bool SameControllerView(SponsorControllerResult? left, SponsorControllerResult? right)
    {
        if (left is null || right is null) return left == right;
        // Failures are always delivered, including repeated reconciliation failures.
        if (left.Failure is not null || right.Failure is not null) return false;
        return left.Owner == right.Owner && SameStamp(left.Stamp, right.Stamp) && SameActualView(left.Actual, right.Actual)
            && left.AppliedId == right.AppliedId && left.RateReceiptId == right.RateReceiptId
            && left.Applied == right.Applied && left.RateReceipt == right.RateReceipt && left.RequestResult == right.RequestResult
            && left.MuteLease == right.MuteLease && left.RateLease == right.RateLease
            && SameItems(left.RateClaims, right.RateClaims) && SameItems(left.SeekClaims, right.SeekClaims)
            && left.PreviewPlayback == right.PreviewPlayback && SameOperationView(left.Operation, right.Operation)
            && SamePlanView(left.Plan, right.Plan);
    }

    private static bool SameOperationView(SponsorOperation? left, SponsorOperation? right)
        => left == right || left is { Kind: NativeOperationKind.Read, Purpose: NativeAdmissionPurpose.Observation,
            Phase: SponsorOperationPhase.SucceededUnobserved }
            && right is { Kind: NativeOperationKind.Read, Purpose: NativeAdmissionPurpose.Observation,
                Phase: SponsorOperationPhase.SucceededUnobserved }
            && left.Generation == right.Generation && left.Failure == right.Failure && SameStamp(left.Stamp, right.Stamp);

    private static bool SameActualView(NativePlaybackSnapshot? left, NativePlaybackSnapshot? right)
    {
        if (left is null || right is null) return left == right;
        // The playback view consumes notices, not raw position. Native reads/evaluation still run;
        // command handlers obtain fresh tickets from APP rather than this presentation snapshot.
        return left.NativeGeneration == right.NativeGeneration && Nullable.Equals(left.Duration, right.Duration)
            && left.Rate == right.Rate && left.State == right.State && left.Muted == right.Muted
            && left.CanSeekExactly == right.CanSeekExactly && left.CanWriteMute == right.CanWriteMute
            && left.CanWriteRate == right.CanWriteRate && left.CanStepFrames == right.CanStepFrames;
    }

    private static bool SameWakeView(ExecutionWake? left, ExecutionWake? right)
        => left == right || left is MediaExecutionWake before && right is MediaExecutionWake after && before.Position == after.Position
            || left is MonotonicExecutionWake earlier && right is MonotonicExecutionWake later
                && earlier.DueMonotonicSeconds == later.DueMonotonicSeconds;

    private static bool SamePlanView(PlaybackExecutionPlan? left, PlaybackExecutionPlan? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        var before = left.OrdinaryPlan;
        var after = right.OrdinaryPlan;
        return left.Validity == right.Validity && left.RateDecision == right.RateDecision
            && left.RateDesire == right.RateDesire && left.PendingManualMute == right.PendingManualMute
            && SameWakeView(left.ExecutionWake, right.ExecutionWake) && SameItems(left.Retirements, right.Retirements)
            && before.IsCurrent == after.IsCurrent && SameStamp(before.Stamp, after.Stamp) && before.Channel == after.Channel
            && ReferenceEquals(before.Selection, after.Selection) && before.NextBoundary == after.NextBoundary
            && SameItems(before.Effects, after.Effects) && SameItems(before.Notices, after.Notices)
            && SameItems(before.Markers, after.Markers) && SameItems(before.Diagnostics, after.Diagnostics);
    }

    private static bool HasPendingSeek(ImmutableArray<SponsorSeekStatus> claims)
    {
        foreach (var claim in claims)
            if (claim.Phase is SponsorOperationPhase.Pending or SponsorOperationPhase.SucceededUnobserved) return true;
        return false;
    }
}
