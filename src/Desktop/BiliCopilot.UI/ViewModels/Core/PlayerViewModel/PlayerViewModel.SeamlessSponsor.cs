// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Resolvers;
using BiliCopilot.UI.SponsorBlock.Playback;
using BiliCopilot.UI.SponsorBlock.Rules;
using BiliCopilot.UI.SponsorBlock.Storage;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private bool ShouldPreloadSponsorSeek(double target)
    {
        if (!double.IsFinite(target) || target < 0) return false;
        if (_selectionRunning && Volatile.Read(ref _executingSelection) != 0) return true;
        var video = _selectedPlaybackState?.Video;
        var external = _seamlessVideoSelected || video is { IsExternal: true, OffsetMap: not null };
        var minimum = video is { IsExternal: true } ? video.MinimumSeekTimeSeconds : null;
        return SeamlessSwitchPolicy.RequiresReloadForSeek(target, external, minimum);
    }

    private async Task<bool> PrepareSponsorSeekAsync(double target, long version)
    {
        await CancelAndDrainSeamlessSelectionAsync();
        if (!CanUseSponsorContent || !IsCurrentPlayback(version)) return false;
        await RestorePhysicalPlayerInputAsync();
        if (!IsCurrentPlayback(version)) return false;
        // This is the ordinary user-seek path, before any controller reservation/native-lane job.
        await SeekSelectedOnDemandAsync(target, version, () => Task.CompletedTask);
        return IsCurrentPlayback(version) && !_onDemandReplayNeedsLoad;
    }

    private bool RequestSponsorActionWithSeamlessBoundary(PlaybackRequest request)
    {
        if (!CanUseSponsorContent || _sponsor is not { } controller) return false;
        if (request.Kind == SponsorRequestKind.Undo && request.Applied is { Kind: SponsorEffectKind.SeekInterval, WasPreview: false } applied
            && applied.Owner == request.Stamp.Owner && request.Stamp.HasSameContext(controller.Snapshot.Stamp)
            && ShouldPreloadSponsorSeek(applied.From))
        {
            var version = _mediaVersion;
            _ = OwnWork(ApplyAsync());
            // The synchronous port cannot claim controller admission while a load is pending.
            // Its original receipt will be rejected after rebind; retain the existing rejection path.
            return false;
            async Task ApplyAsync()
            {
                if (await PrepareSponsorSeekAsync(applied.From, version) && ReferenceEquals(controller, _sponsor))
                    controller.Request(request);
            }
        }

        if (request.Kind == SponsorRequestKind.JumpToPoint) LogUnresolvedSponsorSeek("jump-to-point");
        return controller.Request(request);
    }

    private Task<SponsorEditorReply> RequestSponsorPreviewWithSeamlessBoundaryAsync(PlaybackStamp expected, PreviewInterval preview)
    {
        if (CanUseSponsorContent && _sponsor is { } controller && expected.HasSameContext(controller.Snapshot.Stamp)
            && ShouldPreloadSponsorSeek(preview.Start))
        {
            var version = _mediaVersion;
            return OwnSponsorEditorWork(ApplyAsync());
            async Task<SponsorEditorReply> ApplyAsync()
            {
                if (!await PrepareSponsorSeekAsync(preview.Start, version) || !ReferenceEquals(controller, _sponsor))
                    return SponsorSourceReloadIncomplete();
                // Never rewrite the prepared interval, expected owner or native generation. The
                // existing editor stale path handles the old context after an ordinary replay.
                return await RequestSponsorPreviewCoreAsync(expected, preview);
            }
        }

        return RequestSponsorPreviewCoreAsync(expected, preview);
    }

    private Task<SponsorEditorReply> EndSponsorPreviewWithSeamlessBoundaryAsync(SponsorPreviewToken token, SponsorPreviewEndMode mode)
    {
        if (_sponsor is { } controller && mode == SponsorPreviewEndMode.Restore
            && controller.Snapshot.PreviewPlayback is { } preview && preview.Token == token
            && preview.Token.Owner == controller.Snapshot.Owner
            && preview.Phase is not (SponsorPreviewPhase.Ended or SponsorPreviewPhase.Interrupted))
        {
            if (preview.SavedPosition is { } target && ShouldPreloadSponsorSeek(target))
            {
                var version = _mediaVersion;
                return OwnSponsorEditorWork(ApplyAsync());
                async Task<SponsorEditorReply> ApplyAsync()
                {
                    if (!await PrepareSponsorSeekAsync(target, version) || !ReferenceEquals(controller, _sponsor))
                        return SponsorSourceReloadIncomplete();
                    return await EndSponsorPreviewCoreAsync(token, mode);
                }
            }

            if (preview.SavedPosition is null) LogUnresolvedSponsorSeek("preview-return");
        }

        return EndSponsorPreviewCoreAsync(token, mode);
    }

    private SponsorEditorReply SponsorSourceReloadIncomplete()
        => new(SponsorEditorOutcome.NeedsReconciliation, _sponsor?.Snapshot, Reason: "The selected source reload did not complete.");

    private void LogUnresolvedSponsorSeek(string action)
    {
        if (logger.IsEnabled(LogLevel.Debug) && (_seamlessVideoSelected || _selectedPlaybackState?.Video.IsExternal == true))
            logger.LogDebug("Playback | seamless-sponsor-seek action={Action} target=controller external-video=true", action);
    }
}
