using System.Collections.Immutable;
using BiliCopilot.UI.SponsorBlock.Rules;

namespace BiliCopilot.UI.SponsorBlock.Playback;

internal sealed partial class SponsorPlaybackController
{
    private PlaybackLeaseSnapshot<bool>? _mute;
    private PlaybackLeaseSnapshot<double>? _rate;
    private PlaybackEffect? _muteEffect;
    private ImmutableArray<PlaybackRateClaim> _rateClaims = [];
    private ImmutableArray<PlaybackRateCooldown> _cooldowns = [];
    private ImmutableArray<SponsorSeekStatus> _seekClaims = [];
    private RateSample? _rateSample;
    private SponsorCoordinatorInput? _selectionFacts;
    private double? _selectionDuration;
    private SponsorPlaybackRules.PreparedSelection? _selection;
    private sealed record RateSample(string Token, long Revision, long Visit, double Position, double Time);

    private async Task ObserveAsync()
    {
        var facts = _input;
        var stamp = CurrentStamp;
        var intentId = _intentId;
        var request = _request;
        var sponsorOpen = _sponsorOpen;
        NativePlaybackSnapshot actual;
        try { actual = await ReadAsync(NativeAdmissionPurpose.Observation); }
        catch (Exception ex)
        {
            if (_preview is not { } preview) throw;
            FailPreview(preview, ex.Message);
            Publish();
            return;
        }
        // Preview transport ownership is independent of ordinary observation/field epochs.
        // A policy change can request token-based restoration even while ordinary admission is closed.
        if (await ObservePreviewAsync(actual))
        {
            Publish();
            return;
        }
        // Progress prompts queue a follow-up read, but must not starve this one. Changed facts,
        // visits, user intents or admission still invalidate a decision made across the await.
        if (!CanUseBinding || !ReferenceEquals(facts, _input) || intentId != _intentId
            || !ReferenceEquals(request, _request) || sponsorOpen != _sponsorOpen
            || stamp is not null && !stamp.HasSameContext(CurrentStamp)) return;
        ConfirmLeases(actual);
        ConfirmSeeks(actual);
        if (!_sponsorOpen || _input is null || _interaction is null || _intents.Count != 0)
        {
            Publish();
            return;
        }
        var input = MakeInput(actual);
        // Bound ledgers conservatively: never evict ambiguity merely to make room for more work.
        if (_rateClaims.Length >= SponsorPlaybackRules.MaximumRecords
            || _seekClaims.Length >= SponsorPlaybackRules.MaximumRecords
            || _interaction.AdmittedRequestIds.Length >= SponsorPlaybackRules.MaximumRecords)
        {
            CloseAdmission(RetirementReason.InvalidSnapshot);
            await RestoreBothAsync();
            _failure = "Sponsor claim capacity reached; explicit reconciliation/new binding required.";
            Publish();
            return;
        }
        if (!_cooldowns.IsDefaultOrEmpty)
        {
            var now = _clock();
            _cooldowns = _cooldowns.Where(c => now < c.CancelledAtMonotonicSeconds + 5
                && actual.Position >= c.Start - .3 && actual.Position < c.End + .5).ToImmutableArray();
        }
        var context = new PlaybackExecutionContext(input.Stamp, _clock())
        {
            Speed = _input.Speed,
            CanOwnRate = actual.CanWriteRate && _physical is null,
            MuteLease = _mute,
            RateLease = _rate,
            RateClaims = _rateClaims,
            RateCooldowns = _cooldowns,
            Source2 = _input.AlignedEligibleSource2 is { } keys ? new(input.Stamp, keys) : null,
        };
        var duration = actual.Duration is double total && double.IsFinite(total) && total > 0 ? actual.Duration : null;
        // The immutable facts identity includes owner, input/policy revisions, segments, music and
        // all local/draft exclusions. Duration is normalization's only native-dependent input.
        if (!ReferenceEquals(_selectionFacts, _input) || !Nullable.Equals(_selectionDuration, duration))
        {
            var selection = SponsorPlaybackRules.PrepareSelection(input, execution: true);
            _selectionFacts = _input;
            _selectionDuration = duration;
            _selection = selection;
        }
        var plan = _plan = SponsorPlaybackRules.EvaluateExecution(input, context, _selection);
        if (input.Request is { } explicitRequest && plan.OrdinaryPlan.Effects.IsDefaultOrEmpty)
        {
            ConsumeRequest(explicitRequest.Id); // Rejected explicit requests never wake into replays.
            ResolveRequest(explicitRequest.Id, SponsorRequestPhase.Rejected,
                plan.OrdinaryPlan.Diagnostics.LastOrDefault(d => d.Contributor is null)?.Reason ?? "PlaybackRequestRejected");
        }
        if (plan.Validity != PlaybackExecutionValidity.Current || !plan.OrdinaryPlan.IsCurrent)
        {
            CancelWake();
            Publish();
            return;
        }
        if (!plan.Retirements.IsDefaultOrEmpty)
        {
            PlaybackRetirementDesire? muteRetirement = null;
            PlaybackRetirementDesire? rateRetirement = null;
            // Publish BOTH retirements before any await. An unavailable field must not starve
            // independent cleanup, and retained retirement prevents conflicting admissions.
            foreach (var retirement in plan.Retirements)
            {
                if (retirement.Field == PlaybackLeaseField.Mute && _mute is { } mute
                    && mute.Owner == retirement.Owner && mute.Token == retirement.LeaseToken && mute.UserEpoch == retirement.LeaseUserEpoch)
                {
                    _mute = mute with { Retirement = new(retirement.Reason, _clock()) };
                    muteRetirement = retirement;
                }
                if (retirement.Field == PlaybackLeaseField.Rate && _rate is { } rate
                    && rate.Owner == retirement.Owner && rate.Token == retirement.LeaseToken && rate.UserEpoch == retirement.LeaseUserEpoch)
                {
                    _rate = rate with { Retirement = new(retirement.Reason, _clock()) };
                    rateRetirement = retirement;
                }
            }
            CancelWake();
            var errors = new List<Exception>();
            if (muteRetirement is { } mr)
            {
                try { await RestoreMuteAsync(mr.Disposition == LeaseRetirementDisposition.RelinquishToUser
                    && mr.Reason == RetirementReason.ExternalChange); }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (rateRetirement is { } rr)
            {
                try { await RestoreRateAsync(rr.Disposition == LeaseRetirementDisposition.RelinquishToUser
                    && rr.Reason == RetirementReason.ExternalChange); }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (errors.Count != 0)
                throw new AggregateException("Sponsor retirement failed; native ownership retained.", errors);
            // A retirement barrier never falls through to an old seek/admission proposal.
            RequestObservation();
            Publish();
            return;
        }
        // Required cleanup always precedes a visit-driven reread, even if every read moves.
        if (!ObserveIntervalVisit(actual, plan.OrdinaryPlan))
        {
            CancelWake();
            Publish();
            return;
        }
        if (plan.PendingManualMute is { } pending && _mute?.Token == pending.LeaseToken)
            _mute = _mute with { Coverage = pending.Coverage };
        foreach (var effect in plan.OrdinaryPlan.Effects)
        {
            if (input.Stamp != CurrentStamp || !_sponsorOpen || !CanUseBinding || _intents.Count != 0) return;
            if (effect.Kind is SponsorEffectKind.SeekInterval or SponsorEffectKind.SeekPoint or SponsorEffectKind.SeekUndo)
            {
                await AdmitSeekAsync(effect, input.Stamp);
                Publish();
                return;
            }
            if (effect.Kind == SponsorEffectKind.HoldMute)
            {
                if (_mute is not null)
                {
                    if (effect.RequestId is { } requestId)
                    {
                        if (_mute.Phase != PlaybackLeasePhase.Confirmed || actual.Muted != _mute.Target
                            || _mute.UserEpoch != _muteEpoch || _mute.VisitRevision != _visit
                            || _mute.Retirement is not null || _mute.Restoration is not null)
                        {
                            await RestoreMuteAsync();
                            RequestObservation();
                            Publish();
                            return;
                        }
                        // Reuse this actual confirmation, not another setter or invented receipt.
                        _mute = _mute with { Origin = PlaybackLeaseOrigin.Manual, RequestId = requestId };
                        _muteEffect = effect;
                        ConsumeRequest(requestId);
                        PromoteManualMute(effect.Contributors);
                        ApplyReviews(effect);
                        ResolveRequest(requestId, SponsorRequestPhase.Completed);
                    }
                    _mute = _mute with { Coverage = new(effect.Contributors.Min(c => c.Segment.Segment[0]),
                        effect.Contributors.Max(c => c.Segment.Segment[1]), effect.Contributors) };
                }
                else
                {
                    await AdmitMuteAsync(effect, input.Stamp, actual);
                    Publish();
                    return;
                }
            }
            if (effect.Kind == SponsorEffectKind.ReleaseMute)
            {
                await RestoreMuteAsync();
                // The release is a real conditional-restore outcome, not an automatic unmute.
                if (effect.RequestId is not null) ConsumeRequest(effect.RequestId);
                ApplyReviews(effect);
                ResolveRequest(effect.RequestId, SponsorRequestPhase.Completed);
                RequestObservation();
                Publish();
                return;
            }
        }
        if (plan.RateDesire is { } desire && input.Stamp == CurrentStamp && _intents.Count == 0)
        {
            if (_rate is { } lease && desire.LeaseToken == lease.Token)
            {
                _rate = lease with { Coverage = desire.Coverage };
                SyncRateClaim();
            }
            else if (desire.LeaseToken is null && _rate is null && _physical is null)
            {
                await AdmitRateAsync(desire, input.Stamp);
                Publish();
                return;
            }
        }
        ArmWake(plan.ExecutionWake);
        Publish(); // Reentrant N4 calls only invalidate stamps/queue new work after this decision.
    }

    private PlaybackInput MakeInput(NativePlaybackSnapshot actual)
    {
        var stamp = CurrentStamp!;
        var facts = _input!;
        ExpireRequest();
        PlaybackRequest? request = null;
        if (_request is { } pending)
            request = pending with { Stamp = stamp }; // Re-evaluate an accepted intent against this actual reread.
        return new(stamp, new(actual.Position, actual.Duration, actual.Rate, actual.State, SponsorMediaKind.Vod),
            new(stamp.Owner, facts.Segments), facts.Policy,
            new(actual.CanSeekExactly, actual.CanWriteMute && actual.Muted.HasValue), facts.Channel, _interaction!)
        {
            KnownMusic = facts.KnownMusic,
            LocalHidden = facts.LocalHidden,
            LocalDownvoted = facts.LocalDownvoted,
            ExternalSourceExcluded = facts.ExternalSourceExcluded,
            DraftSegments = facts.DraftSegments,
            IgnorePlaybackForEditing = facts.IgnorePlaybackForEditing || _preview is not null,
            Preview = _preview?.Status.Interval ?? facts.Preview,
            Request = request,
        };
    }

    private async Task AdmitRateAsync(PlaybackRateDesire desire, PlaybackStamp stamp)
    {
        var token = "rate-" + (_operationId + 1);
        _rate = new(desire.Owner, token, desire.UserEpoch, stamp.VisitRevision, desire.Baseline,
            desire.Target, desire.Coverage, PlaybackLeasePhase.Pending);
        SyncRateClaim();
        try
        {
            await NativeAsync(NativeOperationKind.WriteRate, NativeAdmissionPurpose.Sponsor,
                permit => _backend!.WriteRateAsync(permit, desire.Target), stamp);
            _rate = _rate with { Phase = PlaybackLeasePhase.SucceededUnobserved };
        }
        catch (Exception ex)
        {
            _rate = _rate with { Phase = FailurePhase(ex) };
            throw;
        }
        finally { SyncRateClaim(); }
        RequestObservation();
    }

    private async Task AdmitMuteAsync(PlaybackEffect effect, PlaybackStamp stamp, NativePlaybackSnapshot actual)
    {
        if (actual.Muted is not { } baseline) return;
        var token = "mute-" + (_operationId + 1);
        var coverage = new PlaybackCoverage(effect.Contributors.Min(c => c.Segment.Segment[0]),
            effect.Contributors.Max(c => c.Segment.Segment[1]), effect.Contributors);
        _mute = new(stamp.Owner, token, stamp.MuteUserEpoch, stamp.VisitRevision, baseline, true,
            coverage, PlaybackLeasePhase.Pending,
            effect.RequestId is null ? PlaybackLeaseOrigin.Automatic : PlaybackLeaseOrigin.Manual, effect.RequestId);
        _muteEffect = effect;
        if (effect.RequestId is not null) ConsumeRequest(effect.RequestId);
        try
        {
            await NativeAsync(NativeOperationKind.WriteMute, NativeAdmissionPurpose.Sponsor,
                permit => _backend!.WriteMuteAsync(permit, true), stamp);
            _mute = _mute with { Phase = PlaybackLeasePhase.SucceededUnobserved };
        }
        catch (Exception ex)
        {
            _mute = _mute with { Phase = FailurePhase(ex) };
            ResolveRequest(effect.RequestId, SponsorRequestPhase.Failed, "PlaybackRequestFailed");
            throw;
        }
        RequestObservation();
    }

    private async Task AdmitSeekAsync(PlaybackEffect effect, PlaybackStamp stamp)
    {
        var claim = new SponsorSeekStatus("seek-" + (_operationId + 1), effect, stamp, SponsorOperationPhase.Pending)
        { OperationId = _operationId + 1, PositionIntentId = _positionIntentId };
        _seekClaims = _seekClaims.Add(claim);
        _interaction = _interaction! with
        { AdmittedSeeks = _interaction.AdmittedSeeks.Add(new(effect.Contributors.Select(c => c.Key).ToImmutableArray())) };
        if (effect.RequestId is not null) ConsumeRequest(effect.RequestId);
        try
        {
            await NativeAsync(NativeOperationKind.SeekExact, NativeAdmissionPurpose.Sponsor,
                permit => _backend!.SeekExactAsync(permit, effect.To), stamp);
            _seekClaims = _seekClaims.Replace(claim, claim with { Phase = SponsorOperationPhase.SucceededUnobserved });
        }
        catch (Exception ex)
        {
            _seekClaims = _seekClaims.Replace(claim, claim with { Phase = ex is OperationCanceledException
                ? SponsorOperationPhase.Unknown : SponsorOperationPhase.Failed });
            ResolveRequest(effect.RequestId, SponsorRequestPhase.Failed, "PlaybackRequestFailed");
            throw;
        }
        RequestObservation();
    }

    private void ConfirmLeases(NativePlaybackSnapshot actual)
    {
        if (_mute is { Phase: PlaybackLeasePhase.SucceededUnobserved } mute && actual.Muted == mute.Target)
        {
            _mute = mute with { Phase = PlaybackLeasePhase.Confirmed };
            if (mute.UserEpoch == _muteEpoch && mute.VisitRevision == _visit && mute.Retirement is null && _muteEffect is { } effect)
            {
                if (mute.Origin == PlaybackLeaseOrigin.Manual && _interaction is not null)
                    PromoteManualMute(mute.Coverage.Contributors);
                ConfirmEffect(effect, mute.Owner);
            }
            else ResolveRequest(mute.RequestId, SponsorRequestPhase.Rejected, "PlaybackRequestSuperseded");
        }
        if (_rate is { Phase: PlaybackLeasePhase.SucceededUnobserved } rate && SameRate(actual.Rate, rate.Target))
        {
            _rate = rate with { Phase = PlaybackLeasePhase.Confirmed };
            SyncRateClaim();
        }
        RecordObservedRate(actual);
    }

    private void ConfirmSeeks(NativePlaybackSnapshot actual)
    {
        if (_operation is not { Kind: NativeOperationKind.Read, Phase: SponsorOperationPhase.SucceededUnobserved } read
            || read.Generation != actual.NativeGeneration || _intents.Count != 0) return;
        var latestSeekId = _seekClaims.IsDefaultOrEmpty ? 0 : _seekClaims.Max(c => c.OperationId);
        foreach (var claim in _seekClaims)
        {
            // The serial native lane must have completed a NEW read after the seek acknowledgement.
            if (claim.Phase != SponsorOperationPhase.SucceededUnobserved || claim.Stamp.Owner != _owner
                || read.Id <= claim.OperationId) continue;
            if (claim.OperationId != latestSeekId || claim.Stamp.VisitRevision != _visit || claim.PositionIntentId != _positionIntentId
                || claim.Stamp.MuteUserEpoch != _muteEpoch || claim.Stamp.RateUserEpoch != _rateEpoch)
            {
                // A completed seek superseded by a visit or newer transport action owns no field to restore.
                // This read releases its ambiguity without inventing a successful-effect receipt.
                _seekClaims = _seekClaims.Replace(claim, claim with { Phase = SponsorOperationPhase.Reconciled });
                ResolveRequest(claim.Effect.RequestId, SponsorRequestPhase.Rejected, "PlaybackRequestSuperseded");
                continue;
            }
            var forwardTolerance = actual.State == SponsorPlaybackState.Playing ? 2 : .25;
            if (actual.Position < claim.Effect.To - .25 || actual.Position > claim.Effect.To + forwardTolerance) continue;
            _seekClaims = _seekClaims.Replace(claim, claim with { Phase = SponsorOperationPhase.Confirmed });
            ConfirmEffect(claim.Effect, claim.Stamp.Owner);
            // A real confirmed seek advances visit but keeps admitted claims through delayed position events.
            ++_visit;
            _interaction = _interaction! with { VisitRevision = _visit, ManualMute = null };
            _visitObservation = null; // This observed landing is not a natural exit/new entry too.
            _visitRebasePending = false;
        }
    }

    private void RecordObservedRate(NativePlaybackSnapshot actual)
    {
        var previous = _rateSample;
        _rateSample = null;
        if (_rate is not { Phase: PlaybackLeasePhase.Confirmed, Retirement: null } rate
            || rate.UserEpoch != _rateEpoch || rate.VisitRevision != _visit || _intents.Count != 0
            || actual.State != SponsorPlaybackState.Playing || !SameRate(actual.Rate, rate.Target)) return;
        var now = _clock();
        _rateSample = new(rate.Token, _input?.Revision ?? 0, _visit, actual.Position, now);
        if (previous is null || previous.Token != rate.Token || previous.Revision != _input?.Revision
            || previous.Visit != _visit || now <= previous.Time || now - previous.Time > 2
            || actual.Position <= previous.Position || previous.Position < rate.Coverage.Start || actual.Position > rate.Coverage.End
            || Math.Abs((actual.Position - previous.Position) - (now - previous.Time) * rate.Target) > .05) return;
        // Only contributors intersecting BOTH observed endpoints are evidence; future neighbors never count.
        var contributors = rate.Coverage.Contributors.Where(c => c.Segment.Segment[0] <= previous.Position
            && c.Segment.Segment[1] >= actual.Position).ToImmutableArray();
        if (contributors.IsDefaultOrEmpty) return;
        _rateReceipt = new(rate.Owner, rate.Token, rate.UserEpoch, rate.Baseline, rate.Target,
            [new(previous.Position, actual.Position, previous.Time, now, contributors)]);
        _rateReceiptId = ++_receiptId;
    }

    private void ConfirmEffect(PlaybackEffect effect, PlaybackOwner owner)
    {
        _applied = new(owner, effect.Kind, effect.From, effect.To, effect.Contributors, _input?.Preview is not null);
        _appliedId = ++_receiptId;
        ApplyReviews(effect);
        ResolveRequest(effect.RequestId, SponsorRequestPhase.Completed);
    }

    private void ApplyReviews(PlaybackEffect effect)
    {
        if (_interaction is null) return;
        var reviews = _interaction.Reviews;
        if (!effect.ReleaseReviewsAfterApply.IsDefaultOrEmpty)
            reviews = reviews.Where(r => !effect.ReleaseReviewsAfterApply.Contains(r.Id)).ToImmutableArray();
        if (effect.ReviewAfterApply is { } review && !reviews.Any(r => r.Id == review.Id)) reviews = reviews.Add(review);
        _interaction = _interaction with { Reviews = reviews };
    }

    private void ConsumeRequest(string id)
    {
        if (_interaction is not null && !_interaction.AdmittedRequestIds.Contains(id))
            _interaction = _interaction with { AdmittedRequestIds = _interaction.AdmittedRequestIds.Add(id) };
        if (_request?.Id == id) _request = null;
    }

    private void ResolveRequest(string? id, SponsorRequestPhase phase, string? reason = null)
    {
        if (id is not null && _requestResult?.Id == id)
            _requestResult = new(id, phase, reason);
    }

    private void ExpireRequest()
    {
        if (_request is not { } pending) return;
        var stamp = CurrentStamp;
        // Observation prompts necessarily advance ObservationRevision. Only that revision is
        // rebound to the fresh read; accepted facts/policy/visit/field ownership may not change.
        if (_requestRevision != _input?.Revision || !pending.Stamp.HasSameContext(stamp))
        {
            ConsumeRequest(pending.Id);
            ResolveRequest(pending.Id, SponsorRequestPhase.Rejected, "PlaybackRequestExpired");
        }
    }

    private void PromoteManualMute(ImmutableArray<PlaybackContributor> contributors)
    {
        if (_interaction is not null)
            _interaction = _interaction with { MuteSuppressed = false,
                ManualMute = new(_muteEpoch, contributors.Select(c => c.Key).ToImmutableArray()) };
    }

    private void RetireLeases(RetirementReason reason)
    {
        var retirement = new PlaybackLeaseRetirement(reason, _clock());
        if (_mute is not null) _mute = _mute with { Retirement = _mute.Retirement ?? retirement };
        if (_rate is not null) _rate = _rate with { Retirement = _rate.Retirement ?? retirement };
        _rateSample = null;
    }

    private async Task RestoreBothAsync()
    {
        RetireLeases(RetirementReason.UserIntent);
        Exception? error = null;
        try { await RestoreMuteAsync(); } catch (Exception ex) { error = ex; }
        try { await RestoreRateAsync(); } catch (Exception ex)
        { if (error is not null) throw new AggregateException(error, ex); throw; }
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private async Task RestoreMuteAsync(bool external = false)
    {
        if (_mute is not { } lease) return;
        RequireRestorable(lease.Phase, lease.Restoration);
        _mute = lease = lease with { Retirement = lease.Retirement ?? new(RetirementReason.UserIntent, _clock()) };
        var actual = await ReadAsync(NativeAdmissionPurpose.Cleanup);
        if (actual.Muted is null) throw new InvalidOperationException("Mute ownership is unknown; binding retained.");
        if (lease.Phase == PlaybackLeasePhase.SucceededUnobserved && actual.Muted == lease.Target)
            _mute = lease = lease with { Phase = PlaybackLeasePhase.Confirmed };
        if (external || _muteWrittenEpoch > lease.UserEpoch || actual.Muted != lease.Target)
        {
            _mute = null;
            ClearManualMute();
            return;
        }
        if (actual.Muted != lease.Baseline)
        {
            var restoration = new PlaybackLeaseRestoration(RestorationPhase.Pending, "restore-mute-" + (_operationId + 1));
            _mute = lease with { Restoration = restoration };
            try
            {
                await NativeAsync(NativeOperationKind.WriteMute, NativeAdmissionPurpose.Cleanup,
                    permit => _backend!.WriteMuteAsync(permit, lease.Baseline));
                _mute = _mute with { Restoration = restoration with { Phase = RestorationPhase.SucceededUnobserved } };
                actual = await ReadAsync(NativeAdmissionPurpose.Cleanup);
                if (actual.Muted != lease.Baseline)
                    throw new InvalidOperationException("Mute restoration is not observed; binding retained.");
            }
            catch (Exception ex)
            {
                _mute = _mute! with { Restoration = restoration with { Phase = ex is OperationCanceledException
                    ? RestorationPhase.Unknown : RestorationPhase.Failed } };
                throw;
            }
        }
        _mute = null;
        ClearManualMute();
    }

    private async Task RestoreRateAsync(bool external = false)
    {
        if (_rate is not { } lease) return;
        RequireRestorable(lease.Phase, lease.Restoration);
        _rate = lease = lease with { Retirement = lease.Retirement ?? new(RetirementReason.UserIntent, _clock()) };
        var actual = await ReadAsync(NativeAdmissionPurpose.Cleanup);
        if (lease.Phase == PlaybackLeasePhase.SucceededUnobserved)
        {
            if (SameRate(actual.Rate, lease.Target))
            {
                _rate = lease = lease with { Phase = PlaybackLeasePhase.Confirmed };
                SyncRateClaim();
            }
            else if (!SameRate(actual.Rate, lease.Baseline))
            {
                _rate = lease with { Phase = PlaybackLeasePhase.Unknown };
                SyncRateClaim();
                throw new InvalidOperationException("Unobserved rate effect has an unknown actual value; binding retained.");
            }
        }
        var ownsActualRate = lease.Phase == PlaybackLeasePhase.Confirmed
            ? Math.Abs(actual.Rate - lease.Target) <= .12 : SameRate(actual.Rate, lease.Target);
        if (!external && _rateWrittenEpoch <= lease.UserEpoch && ownsActualRate
            && !SameRate(actual.Rate, lease.Baseline))
        {
            var restoration = new PlaybackLeaseRestoration(RestorationPhase.Pending, "restore-rate-" + (_operationId + 1));
            _rate = lease with { Restoration = restoration };
            try
            {
                await NativeAsync(NativeOperationKind.WriteRate, NativeAdmissionPurpose.Cleanup,
                    permit => _backend!.WriteRateAsync(permit, lease.Baseline));
                _rate = _rate with { Restoration = restoration with { Phase = RestorationPhase.SucceededUnobserved } };
                actual = await ReadAsync(NativeAdmissionPurpose.Cleanup);
                if (!SameRate(actual.Rate, lease.Baseline))
                    throw new InvalidOperationException("Rate restoration is not observed; binding retained.");
            }
            catch (Exception ex)
            {
                _rate = _rate! with { Restoration = restoration with { Phase = ex is OperationCanceledException
                    ? RestorationPhase.Unknown : RestorationPhase.Failed } };
                throw;
            }
        }
        if (lease.UserEpoch != _rateEpoch || external)
            _cooldowns = _cooldowns.AddRange(lease.Coverage.Contributors.Select(c => new PlaybackRateCooldown(c.Key,
                c.Segment.Segment[0], c.Segment.Segment[1], _clock())));
        _rate = null;
        _rateSample = null;
    }

    private static void RequireRestorable(PlaybackLeasePhase phase, PlaybackLeaseRestoration? restoration)
    {
        if (phase is PlaybackLeasePhase.Failed or PlaybackLeasePhase.Unknown or PlaybackLeasePhase.Pending
            || restoration?.Phase is RestorationPhase.Failed or RestorationPhase.Unknown or RestorationPhase.Pending)
            throw new InvalidOperationException("Failed or unknown Sponsor work requires explicit reconciliation; no automatic retry.");
    }

    private void ClearManualMute()
    {
        _muteEffect = null;
        if (_interaction is not null) _interaction = _interaction with { ManualMute = null };
    }

    private void SyncRateClaim()
    {
        if (_rate is not { } lease) return;
        var old = _rateClaims.FirstOrDefault(c => c.Token == lease.Token);
        var claim = new PlaybackRateClaim(lease.Owner, lease.Token, lease.Coverage, lease.Phase);
        _rateClaims = old is null ? _rateClaims.Add(claim) : _rateClaims.Replace(old, claim);
    }

    private sealed record VisitInterval(PlaybackSegmentKey Key, double Start, double End);
    private sealed record VisitObservation(long Revision, double Position, SponsorPlaybackState State,
        ImmutableArray<VisitInterval> Intervals);
    private VisitObservation? _visitObservation;
    private ImmutableArray<PlaybackMarker> _visitMarkers;
    private ImmutableArray<VisitInterval> _visitIntervals = [];
    private bool _visitRebasePending;

    // Markers are the frozen evaluator's normalized, source-keyed projection, NOT authority.
    // A changed visit always forces another real evaluation before any effect can dispatch.
    private bool ObserveIntervalVisit(NativePlaybackSnapshot actual, PlaybackPlan plan)
    {
        if (!_visitMarkers.Equals(plan.Markers))
        {
            var built = ImmutableArray.CreateBuilder<VisitInterval>();
            foreach (var marker in plan.Markers)
                if (marker.Policy is CategoryPolicy.AutoSkip or CategoryPolicy.ManualSkip
                    && marker.Contributor.Segment.ActionType is "skip" or "mute")
                    built.Add(new(marker.Contributor.Key, marker.Contributor.Segment.Segment[0], marker.Contributor.Segment.Segment[1]));
            _visitMarkers = plan.Markers;
            _visitIntervals = built.ToImmutable(); // Markers already carry the normalized interval order.
        }
        var intervals = _visitIntervals;
        var previous = _visitObservation;
        _visitObservation = new(_input!.Revision, actual.Position, actual.State, intervals);
        if (previous is null || previous.Revision != _input.Revision
            || !previous.Intervals.Equals(intervals) && !previous.Intervals.SequenceEqual(intervals)
            || previous.State != SponsorPlaybackState.Playing || actual.State != SponsorPlaybackState.Playing
            || previous.Position == actual.Position
            || _seekClaims.Any(c => c.Phase == SponsorOperationPhase.Pending)) return true;
        if (IntervalAt(previous.Intervals, previous.Position) == IntervalAt(intervals, actual.Position)) return true;
        if (_visitRebasePending)
        {
            // The first component transition after a typed seek may be old position -> landing.
            // Rebase on this ACTUAL sample, retaining suppression; do not confirm a seek or
            // advance the visit. Explicit effects and actual-position wakes remain available.
            // If the first sample already overshot the target, this conservatively protects
            // one extra component. A later observed exit/entry recovers without target equality.
            _visitRebasePending = false;
            return true;
        }
        AdvanceVisit(seekSuppressed: false);
        _visitObservation = new(_input.Revision, actual.Position, actual.State, intervals);
        RequestObservation();
        return false;
    }

    private static (double Start, double End)? IntervalAt(ImmutableArray<VisitInterval> intervals, double position)
    {
        double? start = null;
        var end = 0d;
        foreach (var interval in intervals)
        {
            if (start is null || interval.Start > end)
            {
                if (start is { } value && value <= position && position < end) return (value, end);
                start = interval.Start;
                end = interval.End;
            }
            else end = Math.Max(end, interval.End);
        }
        return start is { } last && last <= position && position < end ? (last, end) : null;
    }

    private void AdvanceVisit(bool seekSuppressed = true)
    {
        ++_visit;
        _visitObservation = null;
        _rateSample = null;
        _rateClaims = _rateClaims.Where(c => c.Phase != PlaybackLeasePhase.Confirmed).ToImmutableArray();
        _seekClaims = _seekClaims.Where(c => c.Phase is not (SponsorOperationPhase.Confirmed or SponsorOperationPhase.Reconciled)).ToImmutableArray();
        if (_interaction is not null)
            _interaction = _interaction with
            {
                VisitRevision = _visit,
                SeekSuppressed = seekSuppressed,
                MuteSuppressed = false,
                ManualMute = null,
                // Completed-but-unobserved seeks suppress only their original visit. A new actual
                // read reconciles them; failed/unknown native work still requires explicit recovery.
                AdmittedSeeks = _seekClaims.Where(c => c.Phase != SponsorOperationPhase.SucceededUnobserved || c.Stamp.VisitRevision == _visit)
                    .Select(c => new PlaybackClaim(c.Effect.Contributors.Select(v => v.Key).ToImmutableArray())).ToImmutableArray(),
            };
    }

    private static PlaybackLeasePhase FailurePhase(Exception ex) => ex is OperationCanceledException
        ? PlaybackLeasePhase.Unknown : PlaybackLeasePhase.Failed;
}
