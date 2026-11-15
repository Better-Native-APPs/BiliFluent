// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Appearance;
using Richasy.BiliKernel.Models.Media;
using System.Collections.ObjectModel;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private CommandDanmakuTimeline? _commandDanmakuTimeline;
    private CommandDanmakuItemViewModel[] _commandDanmakuItems = [];
    private long _commandDanmakuLoadVersion = -1;

    public ObservableCollection<CommandDanmakuItemViewModel> ActiveCommandDanmakus { get; } = [];

    [ObservableProperty]
    public partial bool IsCommandDanmakuEnabled { get; set; } = true;

    /// <summary>
    /// Width / height of the current picture according to the media metadata, or null when unknown.
    /// Read by the overlay while it lays out command danmaku cards; not observable and never
    /// touched on the position path. The metadata ignores a rotation flag and per-part differences.
    /// </summary>
    public double? CommandDanmakuVideoAspectRatio
    {
        get
        {
            var ratio = Connector switch
            {
                VideoConnectorViewModel video => video._view?.AspectRatio,
                PgcConnectorViewModel { _episode: { } episode }
                    => episode.GetExtensionIfNotNull<MediaAspectRatio?>(EpisodeExtensionDataId.AspectRatio),
                _ => null,
            };
            return ratio is { Width: > 0, Height: > 0 } value ? (double)value.Width / value.Height : null;
        }
    }

    partial void OnIsCommandDanmakuEnabledChanged(bool value)
    {
        SettingsToolkit.WriteLocalSetting(SettingNames.IsCommandDanmakuEnabled, value);
        if (value) UpdateCommandDanmakuPosition(Player?.Position ?? 0, force: true);
        else ClearActiveCommandDanmakus();
    }

    partial void OnIsConnectingChanged(bool value)
    {
        if (value) ClearActiveCommandDanmakus();
        else UpdateCommandDanmakuPosition(Player?.Position ?? 0, force: true);
    }

    private void ResetCommandDanmakuMedia()
    {
        ClearCommandDanmakuMedia();
        IsCommandDanmakuEnabled = SettingsToolkit.ReadLocalSetting(SettingNames.IsCommandDanmakuEnabled, true);
    }

    private void ClearCommandDanmakuMedia()
    {
        ClearActiveCommandDanmakus();
        _commandDanmakuTimeline = null;
        _commandDanmakuItems = [];
        _commandDanmakuLoadVersion = -1;
    }

    private void ResetCommandDanmakuPlayback()
    {
        ClearActiveCommandDanmakus();
        _commandDanmakuTimeline?.Reset();
        foreach (var item in _commandDanmakuItems) item.ResetForReplay();
    }

    private void InitializeCommandDanmaku()
    {
        var version = _mediaVersion;
        if (!IsCurrentPlayback(version) || _commandDanmakuLoadVersion == version
            || !TryGetCommandDanmakuMedia(out var aid, out var cid)) return;
        _commandDanmakuLoadVersion = version;
        var connector = Connector as VideoConnectorViewModel;
        _ = OwnWork(LoadCommandDanmakuAsync(version, _activeMediaToken, aid, cid, connector));
    }

    private async Task LoadCommandDanmakuAsync(long version, CancellationToken token, string aid, string cid,
        VideoConnectorViewModel? connector)
    {
        try
        {
            var service = this.Get<IDanmakuService>();
            var result = await Task.Run(async () =>
            {
                var meta = await service.GetDanmakuMetaAsync(aid, cid, token);
                token.ThrowIfCancellationRequested();
                var commands = meta.CommandDanmakus;
                var items = new CommandDanmakuItemViewModel[commands.Count];
                var entries = new CommandDanmakuTimelineEntry[commands.Count];
                for (var index = 0; index < commands.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var item = new CommandDanmakuItemViewModel(this, commands[index], index, version, connector);
                    items[index] = item;
                    entries[index] = new CommandDanmakuTimelineEntry(item.ProgressMs, item.DurationMs, item.Kind);
                }

                return (Items: items, Timeline: new CommandDanmakuTimeline(entries, token));
            }, token);
            token.ThrowIfCancellationRequested();
            if (!IsCurrentPlayback(version)) return;
            _commandDanmakuItems = result.Items;
            _commandDanmakuTimeline = result.Timeline;
            UpdateCommandDanmakuPosition(Player?.Position ?? 0, force: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            // Kernel exceptions may include raw responses. Keep this failure isolated from playback.
            logger.LogWarning("Failed to load command danmaku metadata.");
        }
    }

    private bool TryGetCommandDanmakuMedia(out string aid, out string cid)
    {
        aid = string.Empty;
        cid = string.Empty;
        if (_snapshot.Type == BiliMediaType.Video && Connector is VideoConnectorViewModel { _part: { } part } video)
        {
            aid = video._view.Information.Identifier.Id;
            cid = part.Identifier.Id;
        }
        else if (_snapshot.Type == BiliMediaType.Pgc && Connector is PgcConnectorViewModel { _episode: { } episode })
        {
            aid = episode.GetExtensionIfNotNull<long>(EpisodeExtensionDataId.Aid).ToString(System.Globalization.CultureInfo.InvariantCulture);
            cid = episode.GetExtensionIfNotNull<long>(EpisodeExtensionDataId.Cid).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return !string.IsNullOrEmpty(aid) && aid != "0" && !string.IsNullOrEmpty(cid) && cid != "0";
    }

    // Called on the existing UI-thread position path. No queries, LINQ, parsing, or timers here.
    private void UpdateCommandDanmakuPosition(double position, bool force = false)
    {
        if (!IsCommandDanmakuEnabled || IsConnecting || !IsCurrentPlayback(_commandDanmakuLoadVersion)
            || Player?.IsPlaybackInitialized != true)
        {
            if (ActiveCommandDanmakus.Count > 0) ClearActiveCommandDanmakus();
            return;
        }

        if (_commandDanmakuTimeline is not { } timeline || !double.IsFinite(position)) return;
        var positionMs = (long)(position * 1000);
        if (timeline.Update(positionMs) || force)
        {
            var first = timeline.FirstActiveIndex < 0 ? null : _commandDanmakuItems[timeline.FirstActiveIndex];
            var second = timeline.SecondActiveIndex < 0 ? null : _commandDanmakuItems[timeline.SecondActiveIndex];
            for (var index = ActiveCommandDanmakus.Count - 1; index >= 0; index--)
            {
                var item = ActiveCommandDanmakus[index];
                if (ReferenceEquals(item, first) || ReferenceEquals(item, second)) continue;
                item.SetActive(false, positionMs);
                ActiveCommandDanmakus.RemoveAt(index);
            }

            if (first is not null) PutActiveCommandDanmaku(first, 0, positionMs);
            if (second is not null) PutActiveCommandDanmaku(second, 1, positionMs);
        }

        for (var index = 0; index < ActiveCommandDanmakus.Count; index++)
            ActiveCommandDanmakus[index].UpdateRemainingTime(positionMs);
    }

    private void PutActiveCommandDanmaku(CommandDanmakuItemViewModel item, int index, long positionMs)
    {
        if (ActiveCommandDanmakus.Count > index && ReferenceEquals(ActiveCommandDanmakus[index], item)) return;
        var existingIndex = ActiveCommandDanmakus.IndexOf(item);
        if (existingIndex >= 0) ActiveCommandDanmakus.Move(existingIndex, index);
        else
        {
            item.SetActive(true, positionMs);
            ActiveCommandDanmakus.Insert(index, item);
        }
    }

    private void ClearActiveCommandDanmakus()
    {
        if (ActiveCommandDanmakus.Count == 0) return;
        for (var index = 0; index < ActiveCommandDanmakus.Count; index++)
            ActiveCommandDanmakus[index].SetActive(false, 0);
        ActiveCommandDanmakus.Clear();
    }

    internal bool IsCurrentCommandDanmakuItem(CommandDanmakuItemViewModel item)
        => IsCurrentPlayback(item.MediaGeneration) && !IsConnecting
            && item.Index >= 0 && item.Index < _commandDanmakuItems.Length
            && ReferenceEquals(_commandDanmakuItems[item.Index], item);

    internal void DismissCommandDanmaku(CommandDanmakuItemViewModel item)
    {
        if (!IsCurrentCommandDanmakuItem(item) || !item.IsActive || _commandDanmakuTimeline is not { } timeline) return;
        if (!timeline.Dismiss(item.Index)) return;
        item.MarkDismissed();
        UpdateCommandDanmakuPosition(Player.Position);
    }

    internal Task<CommandDanmakuGradeFailure> SubmitCommandDanmakuGradeAsync(CommandDanmakuItemViewModel item, int stars)
    {
        if (!IsCurrentCommandDanmakuItem(item) || !item.IsActive || stars is < 1 or > 5
            || string.IsNullOrEmpty(item.GradeId) || !TryGetCommandDanmakuMedia(out var aid, out var cid))
            return Task.FromResult(CommandDanmakuGradeFailure.Canceled);
        var progressMs = (int)Math.Clamp(Player.Position * 1000, 0, int.MaxValue);
        var operation = SubmitCommandDanmakuGradeCoreAsync(item, aid, cid, progressMs, stars * 2, _activeMediaToken);
        _ = OwnWork(operation);
        return operation;
    }

    private async Task<CommandDanmakuGradeFailure> SubmitCommandDanmakuGradeCoreAsync(CommandDanmakuItemViewModel item,
        string aid, string cid, int progressMs, int score, CancellationToken token)
    {
        try
        {
            if (this.Get<IBiliTokenResolver>().GetToken() is null) return CommandDanmakuGradeFailure.SignInRequired;
            await this.Get<IDanmakuService>().GradeDanmakuAsync(aid, cid, progressMs, item.GradeId!, score, token);
            return CommandDanmakuGradeFailure.None;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return CommandDanmakuGradeFailure.Canceled; }
        catch (Exception)
        {
            logger.LogWarning("Failed to submit a command danmaku rating.");
            return CommandDanmakuGradeFailure.RequestFailed;
        }
    }

    internal Task RunCommandDanmakuAttentionAsync(CommandDanmakuItemViewModel item, IAsyncRelayCommand command, object? parameter)
    {
        if (!IsCurrentCommandDanmakuItem(item) || !item.IsActive
            || !ReferenceEquals(item.AttentionConnector, Connector) || !command.CanExecute(parameter)) return Task.CompletedTask;
        return OwnWork(command.ExecuteAsync(parameter));
    }

    internal void OpenCommandDanmakuLinkedVideo(CommandDanmakuItemViewModel item, VideoInformation video)
    {
        if (!IsCurrentCommandDanmakuItem(item) || !item.IsActive) return;
        InitializeCommand.Execute(new MediaSnapshot(video, _snapshot.IsPrivate));
    }
}
