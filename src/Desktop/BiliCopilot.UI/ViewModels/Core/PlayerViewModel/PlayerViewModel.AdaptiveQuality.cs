// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Resolvers;
using BiliCopilot.UI.Toolkits;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private AdaptiveQualityPolicy _adaptiveQuality = new(AdaptiveBandwidthHistory.Session);
    private bool? _adaptiveAutomaticIntent;
    private AdaptiveCacheMark? _adaptiveCacheMark;
    private TimeSpan _adaptiveNodeSettleUntil;
    private readonly HashSet<long> _adaptiveCommittedIntents = [];
    private AdaptiveReload? _adaptiveReload;
    private Task _adaptiveSwitchWork = Task.CompletedTask;

    partial void ResetAdaptiveQuality()
    {
        _adaptiveQuality = new(AdaptiveBandwidthHistory.Session);
        _adaptiveAutomaticIntent = null;
        _adaptiveCacheMark = null;
        _adaptiveNodeSettleUntil = default;
        _adaptiveCommittedIntents.Clear();
        _adaptiveReload = null;
        _adaptiveSwitchWork = Task.CompletedTask;
    }

    partial void OnAdaptiveSeek(TimeSpan now)
    {
        _adaptiveCacheMark = null;
        _adaptiveQuality.OnDiscontinuity(now);
    }

    partial void PrepareAdaptiveSelection(ref OnDemandSelectionRequest request, bool silent)
    {
        if (!CanSelectOnDemandQuality || OnDemandResolver is not IAdaptiveQualityResolver adaptive || Player is not { } player) return;
        var preference = SettingsToolkit.ReadLocalSetting(SettingNames.PreferQuality, PreferQualityType.Auto);
        _adaptiveAutomaticIntent = preference == PreferQualityType.Auto && request.Preferences.VideoQuality is not > 0;
        _adaptiveQuality.SetAutomatic(_adaptiveAutomaticIntent.Value);
        if (silent || !AdaptiveQualitySelection.IsEnabled(request, preference)) return;

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
        _adaptiveQuality.SetAutomatic(automatic);
        var nodeChanging = _failover.IsAwaitingLoad || nodeDecision.Action is CdnFailoverAction.SwitchNode or CdnFailoverAction.RefreshPlayUrl;
        var reloading = IsConnecting || _onDemandReplayNeedsLoad || !_hasLoadedFile || !player.IsPlaybackInitialized;
        var switching = IsSeamlessSwitching || _selectionIntent is not null || !_adaptiveSwitchWork.IsCompleted;
        var videoNode = raw.VideoRelay?.Current.Url ?? streams.Video.Url;
        var audioNode = raw.AudioRelay?.Current.Url ?? streams.Audio?.Url;
        var nodeChanged = _adaptiveCacheMark is { } mark && mark.StreamEpoch == raw.StreamEpoch
            && (mark.VideoNode != videoNode || mark.AudioNode != audioNode);
        if (nodeChanging || nodeChanged)
            _adaptiveNodeSettleUntil = now + (reloading ? AdaptiveQualityThresholds.ReloadObservationSettle : AdaptiveQualityThresholds.NodeObservationSettle);

        var externalVideo = raw.SelectedState?.Video.IsExternal ?? _selectedPlaybackState?.Video.IsExternal ?? _seamlessVideoSelected;
        var growing = _adaptiveCacheMark is { } previous && previous.StreamEpoch == raw.StreamEpoch
            && previous.VideoNode == videoNode && previous.AudioNode == audioNode
            && now > previous.Time && now - previous.Time <= AdaptiveQualityThresholds.SampleGapReset
            && !sample.CacheIdle && (sample.CacheSeconds > previous.CacheSeconds
                || (!externalVideo && sample.ForwardBytes > previous.ForwardBytes));
        _adaptiveCacheMark = new(now, raw.StreamEpoch, videoNode, audioNode, sample.CacheSeconds, sample.ForwardBytes);

        // Upstream throughput excludes memory deliveries. A growing, non-idle video cache supplies demand;
        // concurrent audio downloads contribute to the same connection's measured throughput.
        var audioRate = raw.AudioRelay?.BytesPerSecond ?? 0;
        var throughput = ((raw.VideoRelay?.BytesPerSecond ?? raw.DownloadRate) + audioRate) * AdaptiveQualityThresholds.BitsPerByte;
        var standardAudio = AudioSelections.PickStandard(current.AudioSelections.Qualities)?.Segment?.Bandwidth ?? streams.AudioBandwidth;
        var observation = new AdaptiveQualitySample(current.Format.Quality, streams.AudioBandwidth, standardAudio, speed,
            sample.CacheSeconds, throughput, growing, raw.PausedForCache, raw.UserPaused, raw.Seeking,
            switching, reloading, nodeChanging || now < _adaptiveNodeSettleUntil,
            nodeDecision.Action == CdnFailoverAction.ReportBandwidthLimited,
            _failover.DownloadRate is { } mainRate ? (mainRate + audioRate) * AdaptiveQualityThresholds.BitsPerByte : null);
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
        _adaptiveCacheMark = null;
        if (_adaptiveReload is { } reload && IsCurrentPlayback(reload.Version) && ReferenceEquals(reload.Resolver, OnDemandResolver)
            && load is { } loading && loading.Selection.Format.Quality == reload.TargetQuality)
            _adaptiveReload = reload with { LoadGeneration = loading.Generation };
    }

    partial void OnAdaptiveFileLoaded(OnDemandSelectionLoad? load)
    {
        if (_adaptiveReload is not { } reload || load is not { } loaded || loaded.Generation != reload.LoadGeneration
            || !IsCurrentPlayback(reload.Version) || !ReferenceEquals(reload.Resolver, OnDemandResolver)) return;
        _adaptiveReload = null;
        if (loaded.Selection.Format.Quality != reload.FromQuality && _adaptiveCommittedIntents.Add(reload.IntentId))
            _adaptiveQuality.OnSelectionCompleted(NetworkNow, reload.FromQuality, loaded.Selection.Format.Quality);
    }

    partial void LogAdaptiveDecision(AdaptiveQualityDecision decision);
    partial void DescribeAdaptiveSample(TimeSpan now, bool automatic);

    private readonly record struct AdaptiveCacheMark(TimeSpan Time, long StreamEpoch, string? VideoNode, string? AudioNode, double CacheSeconds, long ForwardBytes);
    private sealed record AdaptiveReload(long IntentId, long Version, MediaSourceResolverBase Resolver, int FromQuality, int TargetQuality, long? LoadGeneration);
}
