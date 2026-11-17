using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Resolvers;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Items;
using Richasy.BiliKernel.Models.Media;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Player;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    /// <summary>Replay through the same real serial boundary as media replacement.</summary>
    public Task ReplayAsync(double? preferPosition = null)
    {
        if (!CanSeek && preferPosition.HasValue) return Task.CompletedTask;
        if (IsLiveMedia) return RefreshLiveAsync();
        CancelSeamlessSelection();
        var player = Player;
        return player is null ? Task.CompletedTask
            : RunWithoutPlayerInputAsync(() => ReplayWithSubtitleRestoreAsync(player, preferPosition), nativeReplay: true);
    }

    private Task SelectSourceAsync(SourceItemViewModel source)
    {
        if (!CanSelectOnDemandQuality || source is not { IsEnabled: true } || IsPlaybackClosing || IsConnecting || !Sources.Contains(source) || Player is null)
            return Task.CompletedTask;

        var snapshot = _snapshot;
        var resolver = OnDemandResolver;
        if (resolver is not (VideoMediaSourceResolver or PgcMediaSourceResolver))
        {
            return Task.CompletedTask;
        }

        // A quality chosen by hand is remembered for the media that follow. Choosing "automatic" forgets it.
        if (source.IsAuto)
        {
            SettingsToolkit.DeleteLocalSetting(SettingNames.ManualVideoQuality);
        }
        else
        {
            SettingsToolkit.WriteLocalSetting(SettingNames.ManualVideoQuality, source.Data.Quality);
        }

        var preferences = new OnDemandSelectionPreferences(source.IsAuto ? null : source.Data.Quality,
            source.IsAuto ? null : snapshot.PreferAudioQualityId ?? GetResolverAudioSelections(resolver).QualityId,
            snapshot.PreferAudioLanguageId, snapshot.IsAutomaticAudioStandardOnly);
        return SwitchOnDemandSelectionAsync(new(preferences), source.Name);
    }

    private static PlayerFormatInformation? GetPlayingFormat(MediaSourceResolverBase? resolver)
        => resolver switch
        {
            VideoMediaSourceResolver video => video.GetCurrentFormat(),
            PgcMediaSourceResolver pgc => pgc.GetCurrentFormat(),
            _ => default,
        };

    private async Task<bool> ReplayWithSubtitleRestoreAsync(MpvPlayer player, double? preferPosition)
    {
        var version = _mediaVersion;
        var token = _activeMediaToken;
        var client = player.Client;
        if (!IsCurrentPlayback(version) || !ReferenceEquals(player, Player) || !ReferenceEquals(client, Client)) return false;
        var observation = await ReadPlaybackObservationAsync(player);
        if (!IsCurrentPlayback(version)) return false;
        _onDemandReplayNeedsLoad = true;
        _lastOnDemandReplayIssued = false;

        ResetChapters();
        InitializeVideoChapters();
        ResetCommandDanmakuPlayback();
        _subtitleToRestore = null; // This reload restores the subtitle itself.
        var subtitle = CanUseSubtitles && IsSubtitleEnabled ? Subtitles.FirstOrDefault(item => item.IsSelected) : null;
        var video = Connector as VideoConnectorViewModel;
        var aid = video?._view.Information.Identifier.Id;
        var cid = video?._part.Identifier.Id;
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadingStarted = false;
        OnDemandSelectionLoad? loadedSelection = null;
        client.ReachFileLoading += OnLoading;
        client.ReachFileLoaded += OnLoaded;
        client.ReachFileEnd += OnEnded;
        client.ErrorOccurred += OnError;
        try
        {
            if (!await player.ReplayAsync(preferPosition, resume: observation is not { Paused: true }, speed: observation?.Speed))
            {
                OnDemandResolver?.AbandonSelectionLoad();
                if (OnDemandResolver is { } previous) ApplyCurrentOnDemandSelection(previous);
                return false;
            }
            _lastOnDemandReplayIssued = true;
            // Replay returns after issuing loadfile, not after the replacement file is ready.
            bool isLoaded;
            try
            {
                isLoaded = await loaded.Task.WaitAsync(PlaybackNetworkPolicy.SubtitleRestoreWait, token);
            }
            catch (TimeoutException)
            {
                // A node that hangs must not keep the media gate: the next loaded file restores the subtitle.
                if (subtitle is not null && CanRestore()) _subtitleToRestore = subtitle;
                return false;
            }

            if (!isLoaded)
            {
                if (_failover.IsAwaitingLoad)
                {
                    // The address failed and another one is tried: the file that loads restores the subtitle.
                    if (subtitle is not null && CanRestore()) _subtitleToRestore = subtitle;
                    return false;
                }

                OnDemandResolver?.AbandonSelectionLoad();
                if (subtitle is not null && CanRestore()) ShowSubtitleFailure(MpvResult.Fail("Mpv | replay ended before file-loaded"), selecting: true);
                return false;
            }

            if (IsCurrentPlayback(version) && OnDemandResolver is { } resolver)
            {
                resolver.CompleteSelectionLoad(loadedSelection);
                _onDemandReplayNeedsLoad = false;
                ApplyCurrentOnDemandSelection(resolver);
            }

            if (subtitle is null || !CanRestore()) return true;
            // RunWithoutPlayerInputAsync already owns the media gate. Do not acquire it again.
            await SelectSubtitleCoreAsync(subtitle, version, token);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (CanRestore()) ShowSubtitleFailure(MpvResult.Fail("Mpv | subtitle restore failed: " + ex.GetType().Name), selecting: true);
        }
        finally
        {
            client.ReachFileLoading -= OnLoading;
            client.ReachFileLoaded -= OnLoaded;
            client.ReachFileEnd -= OnEnded;
            client.ErrorOccurred -= OnError;
        }

        return false;

        bool CanRestore() => IsCurrentPlayback(version) && ReferenceEquals(player, Player) && ReferenceEquals(client, Client)
            && IsSubtitleEnabled && subtitle is { IsSelected: true } && Subtitles.Any(item => ReferenceEquals(item, subtitle))
            && Connector is VideoConnectorViewModel current && current._view.Information.Identifier.Id == aid
            && current._part.Identifier.Id == cid;

        void OnLoading(object? sender, EventArgs args)
        {
            OnDemandResolver?.ArmSelectionLoad();
            loadingStarted = true;
        }
        void OnLoaded(object? sender, EventArgs args)
        {
            if (loadingStarted)
            {
                loadedSelection = OnDemandResolver?.CaptureSelectionLoad();
                loaded.TrySetResult(true);
            }
        }

        void OnEnded(object? sender, EventArgs args)
        {
            // The previous file ends before the replacement starts; that is not a load failure.
            if (loadingStarted) loaded.TrySetResult(false);
        }

        void OnError(object? sender, MpvError error)
        {
            // MpvPlayer reports source/loadfile exceptions with this error, even if no new file starts.
            if (error == MpvError.VoInitFailed) loaded.TrySetResult(false);
        }
    }
}
