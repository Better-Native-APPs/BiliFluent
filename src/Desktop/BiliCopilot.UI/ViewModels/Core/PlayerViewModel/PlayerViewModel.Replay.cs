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
        var player = Player;
        return player is null ? Task.CompletedTask
            : RunWithoutPlayerInputAsync(() => ReplayWithSubtitleRestoreAsync(player, preferPosition), nativeReplay: true);
    }

    private Task SelectSourceAsync(SourceItemViewModel source)
    {
        if (source is not { IsEnabled: true } || IsPlaybackClosing || IsConnecting || !Sources.Contains(source) || Player is null)
            return Task.CompletedTask;

        var player = Player;
        var position = player.Position;
        var snapshot = _snapshot;
        var resolver = _sourceResolver;
        if (resolver is not (VideoMediaSourceResolver or PgcMediaSourceResolver))
        {
            foreach (var item in Sources) item.IsSelected = item.Equals(source);
            SelectedSource = source;
            return Task.CompletedTask;
        }

        // A quality chosen by hand is remembered for the media that follow. Choosing "automatic" forgets it.
        var playing = GetPlayingFormat(resolver);
        if (source.IsAuto)
        {
            snapshot.PreferQuality = default;
            SettingsToolkit.DeleteLocalSetting(SettingNames.ManualVideoQuality);
        }
        else
        {
            snapshot.PreferQuality = source.Data.Quality;
            SettingsToolkit.WriteLocalSetting(SettingNames.ManualVideoQuality, source.Data.Quality);
        }

        if (resolver is VideoMediaSourceResolver videoResolver) videoResolver.ResetSnapshot(snapshot);
        else if (resolver is PgcMediaSourceResolver pgcResolver) pgcResolver.ResetSnapshot(snapshot);

        // "Automatic" may pick the quality that already plays, and the other way round. Nothing is replayed
        // then: a replay retires the danmaku and skip-segment bindings, and only a load brings them back.
        if (GetPlayingFormat(resolver) is not { } next || next.Quality == playing?.Quality)
        {
            MarkSelectedSource();
            return Task.CompletedTask;
        }

        return RunWithoutPlayerInputAsync(async () =>
        {
            if (!Sources.Contains(source) || !ReferenceEquals(resolver, _sourceResolver)) return;
            MarkSelectedSource();
            // Replay itself consumes this captured position. Never save it in a VM field
            // or wait for IsPlaybackInitialized (Replay need not toggle that property).
            await ReplayWithSubtitleRestoreAsync(player, position);
        }, nativeReplay: true);
    }

    private static PlayerFormatInformation? GetPlayingFormat(MediaSourceResolverBase? resolver)
        => resolver switch
        {
            VideoMediaSourceResolver video => video.GetCurrentFormat(),
            PgcMediaSourceResolver pgc => pgc.GetCurrentFormat(),
            _ => default,
        };

    // Marks the entry of the quality list that is in effect: the automatic entry, named after the quality
    // it picked, or the quality chosen by hand.
    private void MarkSelectedSource()
    {
        var resolver = _sourceResolver;
        var playing = GetPlayingFormat(resolver);
        var isAuto = resolver switch
        {
            VideoMediaSourceResolver video => video.IsAutoQuality(),
            PgcMediaSourceResolver pgc => pgc.IsAutoQuality(),
            _ => false,
        };

        foreach (var item in Sources)
        {
            if (item.IsAuto)
            {
                item.ShowAutoQuality(isAuto ? playing : default);
                item.IsSelected = isAuto && playing is not null;
            }
            else
            {
                item.IsSelected = !isAuto && item.Data.Quality == playing?.Quality;
            }
        }

        SelectedSource = Sources.FirstOrDefault(item => item.IsSelected);
    }

    private async Task ReplayWithSubtitleRestoreAsync(MpvPlayer player, double? preferPosition)
    {
        var version = _mediaVersion;
        var token = _activeMediaToken;
        var client = player.Client;
        if (!IsCurrentPlayback(version) || !ReferenceEquals(player, Player) || !ReferenceEquals(client, Client)) return;

        ResetCommandDanmakuPlayback();
        var subtitle = IsSubtitleEnabled ? Subtitles.FirstOrDefault(item => item.IsSelected) : null;
        if (subtitle is null || Connector is not VideoConnectorViewModel video)
        {
            await player.ReplayAsync(preferPosition);
            return;
        }

        var aid = video._view.Information.Identifier.Id;
        var cid = video._part.Identifier.Id;
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadingStarted = false;
        client.ReachFileLoading += OnLoading;
        client.ReachFileLoaded += OnLoaded;
        client.ReachFileEnd += OnEnded;
        client.ErrorOccurred += OnError;
        try
        {
            await player.ReplayAsync(preferPosition);
            // Replay returns after issuing loadfile, not after the replacement file is ready.
            if (!await loaded.Task.WaitAsync(token))
            {
                if (CanRestore()) ShowSubtitleFailure(MpvResult.Fail("Mpv | replay ended before file-loaded"), selecting: true);
                return;
            }

            if (!CanRestore()) return;
            // RunWithoutPlayerInputAsync already owns the media gate. Do not acquire it again.
            await SelectSubtitleCoreAsync(subtitle, version, token);
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

        bool CanRestore() => IsCurrentPlayback(version) && ReferenceEquals(player, Player) && ReferenceEquals(client, Client)
            && IsSubtitleEnabled && subtitle.IsSelected && Subtitles.Any(item => ReferenceEquals(item, subtitle))
            && Connector is VideoConnectorViewModel current && current._view.Information.Identifier.Id == aid
            && current._part.Identifier.Id == cid;

        void OnLoading(object? sender, EventArgs args) => loadingStarted = true;
        void OnLoaded(object? sender, EventArgs args)
        {
            if (loadingStarted) loaded.TrySetResult(true);
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
