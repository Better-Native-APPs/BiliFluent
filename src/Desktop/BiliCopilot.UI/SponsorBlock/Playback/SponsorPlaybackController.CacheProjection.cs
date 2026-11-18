using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Rules;

namespace BiliCopilot.UI.SponsorBlock.Playback;

internal sealed partial class SponsorPlaybackController
{
    private ImmutableArray<PlaybackSeekInterval> ProjectAutomaticSeekIntervals()
    {
        if (!_sponsorOpen || !CanUseBinding || _intents.Count != 0 || _input is not { } facts
            || _interaction is not { } interaction || _actual is not { } actual || CurrentStamp is not { } stamp) return [];
        // Snapshot-only counterpart of MakeInput: do not expire/consume requests or touch a visit.
        var input = new PlaybackInput(stamp, new(actual.Position, actual.Duration, actual.Rate, actual.State, SponsorMediaKind.Vod),
            new(stamp.Owner, facts.Segments), facts.Policy,
            new(actual.CanSeekExactly, actual.CanWriteMute && actual.Muted.HasValue), facts.Channel, interaction)
        {
            KnownMusic = facts.KnownMusic,
            LocalHidden = facts.LocalHidden,
            LocalDownvoted = facts.LocalDownvoted,
            ExternalSourceExcluded = facts.ExternalSourceExcluded,
            DraftSegments = facts.DraftSegments,
            IgnorePlaybackForEditing = facts.IgnorePlaybackForEditing || _preview is not null,
            Preview = _preview?.Status.Interval ?? facts.Preview,
        };
        var context = new PlaybackExecutionContext(stamp, _clock())
        {
            Speed = facts.Speed,
            CanOwnRate = actual.CanWriteRate && _physical is null,
            MuteLease = _mute,
            RateLease = _rate,
            RateClaims = _rateClaims,
            RateCooldowns = _cooldowns,
            Source2 = facts.AlignedEligibleSource2 is { } keys ? new(stamp, keys) : null,
        };
        // AdvanceVisit retains ambiguous claims but releases completed seeks on a new entry.
        // Reviews survive unchanged, including the protection installed by Undo.
        var nextVisit = interaction with
        {
            SeekSuppressed = false,
            MuteSuppressed = false,
            ManualMute = null,
            AdmittedSeeks = _seekClaims.Where(claim => claim.Phase is SponsorOperationPhase.Pending
                or SponsorOperationPhase.Failed or SponsorOperationPhase.Unknown)
                .Select(claim => new PlaybackClaim(claim.Effect.Contributors.Select(contributor => contributor.Key).ToImmutableArray())).ToImmutableArray(),
        };
        var duration = actual.Duration is double total && double.IsFinite(total) && total > 0 ? actual.Duration : null;
        var prepared = ReferenceEquals(_selectionFacts, facts) && Nullable.Equals(_selectionDuration, duration) ? _selection : null;
        return SponsorPlaybackRules.ProjectAutomaticSeekIntervals(input, context, nextVisit, prepared);
    }
}
