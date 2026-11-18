using System.Collections.Immutable;

namespace BiliCopilot.UI.SponsorBlock.Rules;

/// <summary>A half-open natural-playback interval whose automatic action is an exact seek.</summary>
public readonly record struct PlaybackSeekInterval(double Start, double End);

public static partial class SponsorPlaybackRules
{
    /// <summary>
    /// Projects natural playback through the existing evaluator, without admitting any effect.
    /// The controller supplies the claim ledger that survives the next natural visit.
    /// </summary>
    public static ImmutableArray<PlaybackSeekInterval> ProjectAutomaticSeekIntervals(PlaybackInput input,
        PlaybackExecutionContext context, PlaybackInteraction nextVisit)
        => ProjectAutomaticSeekIntervals(input, context, nextVisit, null);

    internal static ImmutableArray<PlaybackSeekInterval> ProjectAutomaticSeekIntervals(PlaybackInput input,
        PlaybackExecutionContext context, PlaybackInteraction nextVisit, PreparedSelection? prepared)
    {
        if (input.Actual.State is not (SponsorPlaybackState.Playing or SponsorPlaybackState.Paused or SponsorPlaybackState.Waiting)
            || context.Stamp != input.Stamp || !ValidExecutionContext(input, context)) return [];

        // A forecast is never sent to the native lane. Pausing does not change what a resumed
        // natural visit will do, and an explicit request is not an automatic playback action.
        var natural = input with { Actual = input.Actual with { State = SponsorPlaybackState.Playing }, Request = null };
        var common = EvaluateCore(natural, context, prepared);
        if (!common.CanExecute || !common.Plan.IsCurrent) return [];
        var position = input.Actual.Position;
        var component = ConnectedAtPosition(common.Selected.Where(item => item.Policy is CategoryPolicy.AutoSkip or CategoryPolicy.ManualSkip
            && item.Segment.ActionType is "skip" or "mute"), position);
        var currentEnd = component.IsDefaultOrEmpty ? position : component.Max(item => item.End);
        var intervals = ImmutableArray.CreateBuilder<PlaybackSeekInterval>();
        foreach (var item in common.Selected)
        {
            if (item.Segment.ActionType != "skip" || item.End <= position) continue;
            var futureVisit = item.Start > position && item.Start >= currentEnd;
            var at = Math.Max(position, item.Start);
            var forecast = natural with
            {
                Actual = natural.Actual with { Position = at },
                Interaction = futureVisit ? nextVisit : natural.Interaction,
            };
            var forecastContext = futureVisit ? context with
            {
                // Confirmed ownership retires on the natural component transition. Pending,
                // failed and unknown work remains protected, as in the controller's visit ledger.
                RateLease = context.RateLease?.Phase == PlaybackLeasePhase.Confirmed ? null : context.RateLease,
                MuteLease = context.MuteLease?.Phase == PlaybackLeasePhase.Confirmed ? null : context.MuteLease,
                RateClaims = context.RateClaims.Where(claim => claim.Phase != PlaybackLeasePhase.Confirmed).ToImmutableArray(),
            } : context;

            // Reuse the full execution evaluator, including source alignment, undo protection,
            // normalization and SpeedUp's actual seek-only fallback. No parallel category rules.
            var plan = EvaluateExecution(forecast, forecastContext, common.Plan.Selection);
            foreach (var effect in plan.OrdinaryPlan.Effects)
            {
                if (effect.Kind != SponsorEffectKind.SeekInterval || effect.RequestId is not null
                    || !effect.Contributors.Any(contributor => contributor.Key == item.Contributor.Key)) continue;
                var start = input.Policy.SkipStrategy == SponsorSkipStrategy.Seek ? item.Start : at;
                var interval = new PlaybackSeekInterval(start, effect.To);
                if (intervals.Count > 0 && interval.Start <= intervals[^1].End)
                    intervals[^1] = intervals[^1] with { End = Math.Max(intervals[^1].End, interval.End) };
                else intervals.Add(interval);
            }
        }

        return intervals.ToImmutable();
    }
}
