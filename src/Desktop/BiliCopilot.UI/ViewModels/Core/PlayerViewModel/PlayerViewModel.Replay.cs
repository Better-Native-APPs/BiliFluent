using BiliCopilot.UI.Resolvers;
using BiliCopilot.UI.ViewModels.Items;
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
        if (source is null || IsPlaybackClosing || IsConnecting || !Sources.Contains(source) || Player is null)
            return Task.CompletedTask;

        var player = Player;
        var position = player.Position;
        var snapshot = _snapshot;
        var resolver = _sourceResolver;
        return RunWithoutPlayerInputAsync(async () =>
        {
            if (!Sources.Contains(source)) return;
            foreach (var item in Sources) item.IsSelected = item.Equals(source);
            SelectedSource = source;
            if (resolver is VideoMediaSourceResolver videoResolver)
            {
                snapshot.PreferQuality = source.Data.Quality;
                videoResolver.ResetSnapshot(snapshot);
                // Replay itself consumes this captured position. Never save it in a VM field
                // or wait for IsPlaybackInitialized (Replay need not toggle that property).
                await ReplayWithSubtitleRestoreAsync(player, position);
            }
        }, nativeReplay: resolver is VideoMediaSourceResolver);
    }

    private async Task ReplayWithSubtitleRestoreAsync(MpvPlayer player, double? preferPosition)
    {
        var version = _mediaVersion;
        var token = _activeMediaToken;
        var client = player.Client;
        if (!IsCurrentPlayback(version) || !ReferenceEquals(player, Player) || !ReferenceEquals(client, Client)) return;

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
