// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Resolvers;
using BiliCopilot.UI.Toolkits;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private AdaptiveQualityPolicy _adaptiveQuality = new(AdaptiveBandwidthHistory.Session);
    private bool? _adaptiveAutomaticIntent;
    private AdaptiveNodeMark? _adaptiveNodeMark;
    private TimeSpan _adaptiveNodeSettleUntil;
    private bool _adaptiveHasLoadedOnce;
    private TimeSpan? _adaptiveEvidenceLogAt;
    private double _adaptiveLoggedEvidence;
    private bool _adaptiveEvidenceNodeChanged;
    private readonly HashSet<long> _adaptiveCommittedIntents = [];
    private AdaptiveReload? _adaptiveReload;
    private Task _adaptiveSwitchWork = Task.CompletedTask;

    partial void ResetAdaptiveQuality()
    {
        _adaptiveQuality = new(AdaptiveBandwidthHistory.Session);
        _adaptiveAutomaticIntent = null;
        _adaptiveNodeMark = null;
        _adaptiveNodeSettleUntil = default;
        _adaptiveHasLoadedOnce = false;
        _adaptiveEvidenceLogAt = null;
        _adaptiveLoggedEvidence = 0;
        _adaptiveEvidenceNodeChanged = false;
        _adaptiveCommittedIntents.Clear();
        _adaptiveReload = null;
        _adaptiveSwitchWork = Task.CompletedTask;
    }

    partial void OnAdaptiveSeek(TimeSpan now)
    {
        _adaptiveQuality.OnDiscontinuity(now);
    }

    partial void PrepareAdaptiveSelection(ref OnDemandSelectionRequest request, bool silent)
    {
        if (!CanSelectOnDemandQuality || OnDemandResolver is not IAdaptiveQualityResolver adaptive || Player is not { } player) return;
        var preference = SettingsToolkit.ReadLocalSetting(SettingNames.PreferQuality, PreferQualityType.Auto);
        _adaptiveAutomaticIntent = preference == PreferQualityType.Auto && request.Preferences.VideoQuality is not > 0;
        var relayed = _adaptiveAutomaticIntent.Value && player.WillRelayNextOnDemandLoad();
        _adaptiveQuality.SetAutomatic(_adaptiveAutomaticIntent.Value && relayed);
        if (!relayed || silent || !AdaptiveQualitySelection.IsEnabled(request, preference)) return;

        var audio = AudioSelections.PickQuality(GetResolverAudioSelections(OnDemandResolver).Qualities,
            request.ResolvedAudioQualityId ?? request.Preferences.AudioQualityId, request.Preferences.StandardAudioOnly);
        var decision = _adaptiveQuality.SelectStart(NetworkNow, adaptive.GetAdaptiveCandidates(),
            audio?.Segment?.Bandwidth ?? OnDemandResolver.CurrentStreams?.AudioBandwidth ?? 0, player.PlaybackRate);
        if (decision.Action != AdaptiveQualityAction.Start) return;
        request = request with { ResolvedVideoQuality = decision.ToQuality };
        LogAdaptiveDecision(decision with { FromQuality = OnDemandResolver.PlayingSelection?.Format.Quality ?? 0 });
    }

    partial void ObserveAdaptiveQuality(TimeSpan now, NetworkSample raw, PlaybackCacheSample sample, CdnFailoverDecision nodeDecision, double speed)
    {
        if (!CanSelectOnDemandQuality || OnDemandResolver is not IAdaptiveQualityResolver adaptive
            || OnDemandResolver.PlayingSelection is not { } current || OnDemandResolver.CurrentStreams is not { Video: not null } streams
            || Player is not { } player || IsPlaybackClosing) return;

        var preference = SettingsToolkit.ReadLocalSetting(SettingNames.PreferQuality, PreferQualityType.Auto);
        var automatic = preference == PreferQualityType.Auto && (_adaptiveAutomaticIntent ?? current.Preferences.VideoQuality is not > 0);
        _adaptiveQuality.SetAutomatic(automatic && raw.VideoRelay is not null);
        var nodeChanging = (_adaptiveHasLoadedOnce && _failover.IsAwaitingLoad)
            || nodeDecision.Action is CdnFailoverAction.SwitchNode or CdnFailoverAction.RefreshPlayUrl;
        var reloading = IsConnecting || _onDemandReplayNeedsLoad || !_hasLoadedFile || !player.IsPlaybackInitialized;
        var switching = IsSeamlessSwitching || _selectionIntent is not null || !_adaptiveSwitchWork.IsCompleted;
        DeferSponsorCacheAutomaticSelection(raw.Position, speed, ref switching);
        var videoNode = raw.VideoRelay?.Current.Url ?? streams.Video.Url;
        var audioNode = raw.AudioRelay?.Current.Url ?? streams.Audio?.Url;
        var nodeChanged = _adaptiveNodeMark is { } mark && mark.Quality == current.Format.Quality
            && (mark.VideoNode != videoNode || mark.AudioNode != audioNode);
        var videoNodeChanged = _adaptiveNodeMark is { } videoMark && videoMark.Quality == current.Format.Quality && videoMark.VideoNode != videoNode;
        if (nodeChanging || nodeChanged)
            _adaptiveNodeSettleUntil = now + (_adaptiveHasLoadedOnce && reloading ? AdaptiveQualityThresholds.ReloadObservationSettle : AdaptiveQualityThresholds.NodeObservationSettle);
        _adaptiveNodeMark = new(current.Format.Quality, videoNode, audioNode);
        _adaptiveEvidenceNodeChanged |= videoNodeChanged;

        // Rolling rates remain available only for the existing special-audio downgrade exception.
        var audioRate = raw.AudioRelay?.BytesPerSecond ?? 0;
        var throughput = ((raw.VideoRelay?.BytesPerSecond ?? raw.DownloadRate) + audioRate) * AdaptiveQualityThresholds.BitsPerByte;
        var standardAudio = AudioSelections.PickStandard(current.AudioSelections.Qualities)?.Segment?.Bandwidth ?? streams.AudioBandwidth;
        var observation = new AdaptiveQualitySample(current.Format.Quality, streams.AudioBandwidth, standardAudio, speed,
            sample.CacheSeconds, throughput, raw.VideoRelay?.ThroughputObservation, raw.PausedForCache, raw.UserPaused, raw.Seeking,
            switching, reloading, nodeChanging || now < _adaptiveNodeSettleUntil,
            nodeDecision.Action == CdnFailoverAction.ReportBandwidthLimited,
            _failover.DownloadRate is { } mainRate ? (mainRate + audioRate) * AdaptiveQualityThresholds.BitsPerByte : null, videoNodeChanged);
        var decision = _adaptiveQuality.OnSample(now, observation, adaptive.GetAdaptiveCandidates());
        DescribeAdaptiveSample(now, automatic);
        if (decision.ReportCap) LogAdaptiveDecision(decision);
        if (decision.Action is not (AdaptiveQualityAction.Up or AdaptiveQualityAction.Down)) return;

        LogAdaptiveDecision(decision);
        // Preserve the actual audio and automatic video mode. No manual preference is written.
        var request = new OnDemandSelectionRequest(current.Preferences with { VideoQuality = null }, decision.ToQuality, current.AudioSelections.QualityId);
        var version = _mediaVersion;
        var resolver = OnDemandResolver;
        var work = SwitchOnDemandSelectionAsync(request, decision.ToQuality.ToString(System.Globalization.CultureInfo.InvariantCulture), silent: true);
        var intent = _selectionIntent;
        _adaptiveSwitchWork = OwnWork(ObserveAdaptiveSwitchAsync(work, intent, resolver, version));
    }

    private async Task ObserveAdaptiveSwitchAsync(Task work, SelectionIntent? intent, MediaSourceResolverBase resolver, long version)
    {
        try
        {
#pragma warning disable VSTHRD003
            await work;
#pragma warning restore VSTHRD003
        }
        finally
        {
            if (IsCurrentPlayback(version) && ReferenceEquals(resolver, OnDemandResolver) && intent is { } request
                && !_adaptiveCommittedIntents.Contains(request.Id) && _adaptiveReload?.IntentId != request.Id)
                _adaptiveQuality.OnSelectionCompleted(NetworkNow, request.PreviousQuality, request.PreviousQuality);
        }
    }

    partial void OnAdaptiveSelectionCommitted(SelectionIntent intent, int fromQuality, int toQuality)
    {
        if (!intent.Silent || !IsCurrentPlayback(intent.Version) || fromQuality == toQuality || !_adaptiveCommittedIntents.Add(intent.Id)) return;
        _adaptiveQuality.OnSelectionCompleted(NetworkNow, fromQuality, toQuality);
    }

    partial void OnAdaptiveSelectionReloading(SelectionIntent intent, int? targetQuality)
    {
        if (intent.Silent && targetQuality is { } target && !_adaptiveCommittedIntents.Contains(intent.Id))
            _adaptiveReload = new(intent.Id, intent.Version, intent.Resolver, intent.PreviousQuality, target, null);
    }

    partial void OnAdaptiveSelectionFinished(SelectionIntent intent, bool loadIssued)
    {
        if (_adaptiveReload?.IntentId == intent.Id && (!loadIssued || !_onDemandReplayNeedsLoad))
        {
            _adaptiveReload = null;
            if (!_adaptiveCommittedIntents.Contains(intent.Id))
                _adaptiveQuality.OnSelectionCompleted(NetworkNow, intent.PreviousQuality, intent.PreviousQuality);
        }
    }

    partial void OnAdaptiveFileLoading(OnDemandSelectionLoad? load)
    {
        if (_adaptiveHasLoadedOnce) _adaptiveNodeSettleUntil = NetworkNow + AdaptiveQualityThresholds.ReloadObservationSettle;
        else if (OnDemandResolver is IAdaptiveQualityResolver { AdaptiveStartDecision: { } start }) _adaptiveQuality.OnStartSelected(start);
        if (_adaptiveReload is { } reload && IsCurrentPlayback(reload.Version) && ReferenceEquals(reload.Resolver, OnDemandResolver)
            && load is { } loading && loading.Selection.Format.Quality == reload.TargetQuality)
            _adaptiveReload = reload with { LoadGeneration = loading.Generation };
    }

    partial void OnAdaptiveFileLoaded(OnDemandSelectionLoad? load)
    {
        _adaptiveHasLoadedOnce = true;
        if (_adaptiveReload is not { } reload || load is not { } loaded || loaded.Generation != reload.LoadGeneration
            || !IsCurrentPlayback(reload.Version) || !ReferenceEquals(reload.Resolver, OnDemandResolver)) return;
        _adaptiveReload = null;
        if (loaded.Selection.Format.Quality != reload.FromQuality && _adaptiveCommittedIntents.Add(reload.IntentId))
            _adaptiveQuality.OnSelectionCompleted(NetworkNow, reload.FromQuality, loaded.Selection.Format.Quality);
    }

    partial void LogAdaptiveDecision(AdaptiveQualityDecision decision);
    partial void DescribeAdaptiveSample(TimeSpan now, bool automatic);
    partial void DeferSponsorCacheAutomaticSelection(double position, double speed, ref bool switching);

    private readonly record struct AdaptiveNodeMark(int Quality, string? VideoNode, string? AudioNode);
    private sealed record AdaptiveReload(long IntentId, long Version, MediaSourceResolverBase Resolver, int FromQuality, int TargetQuality, long? LoadGeneration);
}
