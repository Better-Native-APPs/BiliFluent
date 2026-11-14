// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Danmaku;
using Richasy.BiliKernel.Models.Media;
using Richasy.WinUIKernel.Share.Base;
using System.ComponentModel;
using Windows.ApplicationModel.DataTransfer;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private long _danmakuCacheVersion = -1;
    private long _danmakuLoadingVersion = -1;
    private Dictionary<int, List<DanmakuInformation>> _danmakuSegments = [];
    private Task _danmakuLoadingTask = Task.CompletedTask;
    // The owned loading task disposes this source after all segment workers finish.
#pragma warning disable CA2213
    private CancellationTokenSource? _danmakuLoadingCancellation;
#pragma warning restore CA2213

    [RelayCommand]
    private Task InitializeDanmakuAsync()
    {
        var version = _mediaVersion;
        if (!IsCurrentPlayback(version)) return Task.CompletedTask;
        if (_danmakuCacheVersion != version)
        {
            // CancelAsync marks the token cancelled synchronously; only callbacks run later.
            _ = _danmakuLoadingCancellation?.CancelAsync();
            _danmakuSegments = [];
            Danmaku.PropertyChanged -= OnDanmakuVisibilityChanged;
            IsDanmakuEnabled = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.IsDanmakuEnabled, true);
            IsDanmakuControlVisible = IsDanmakuEnabled;
            Danmaku.ResetData();
            _danmakuCacheVersion = version;
            Danmaku.PropertyChanged += OnDanmakuVisibilityChanged;
        }

        if (!IsDanmakuEnabled || !Danmaku.IsShowDanmaku) return Task.CompletedTask;
        if (_danmakuLoadingVersion == version && !_danmakuLoadingTask.IsCompleted
            && _danmakuLoadingCancellation is { IsCancellationRequested: false }) return _danmakuLoadingTask;

        var previousTask = _danmakuLoadingTask;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_activeMediaToken);
        _danmakuLoadingCancellation = cancellation;
        _danmakuLoadingVersion = version;
        return _danmakuLoadingTask = OwnWork(InitializeDanmakuCoreAsync(version, cancellation, previousTask));
    }

    private async Task InitializeDanmakuCoreAsync(long version, CancellationTokenSource cancellation, Task previousTask)
    {
        using var loadingCancellation = cancellation;
        var token = cancellation.Token;
        var segments = _danmakuSegments;
        try
        {
            // A canceled visibility/media load must drain before its replacement starts requests.
#pragma warning disable VSTHRD003
            await previousTask;
#pragma warning restore VSTHRD003
            token.ThrowIfCancellationRequested();
            if (!IsCurrentDanmaku()) return;

            string aid;
            string cid;
            double duration;
            if (_snapshot.Type == Models.Constants.BiliMediaType.Video && Connector is VideoConnectorViewModel video)
            {
                aid = video._view.Information.Identifier.Id;
                cid = video._part.Identifier.Id;
                duration = video._view.Information.Duration ?? 0;
                Danmaku.Title = video._view.Information.Identifier.Title;
            }
            else if (_snapshot.Type == Models.Constants.BiliMediaType.Pgc && Connector is PgcConnectorViewModel pgc)
            {
                aid = pgc._episode.GetExtensionIfNotNull<long>(EpisodeExtensionDataId.Aid).ToString();
                cid = pgc._episode.GetExtensionIfNotNull<long>(EpisodeExtensionDataId.Cid).ToString();
                duration = pgc._episode.Duration ?? 0;
                Danmaku.Title = $"{pgc.SeasonTitle} - {pgc.EpisodeTitle}";
            }
            else
            {
                return;
            }

            var service = this.Get<IDanmakuService>();
            var segmentCount = Convert.ToInt32(Math.Ceiling(duration / 360));
            if (segmentCount <= 0)
            {
                await PublishDanmakuAsync();
                return;
            }

            IsDanmakuLoading = true;
            var currentSegment = Math.Clamp(Convert.ToInt32(Math.Floor((Player?.Position ?? 0) / 360)) + 1, 1, segmentCount);
            await Task.Run(() => GetDanmakuAsync(currentSegment, token), token);
            if (!await PublishDanmakuAsync()) return;
            // Enable the toggle after the current segment, not after the whole video's preload.
            IsDanmakuLoading = false;

            int[] remainingSegments;
            lock (segments)
            {
                remainingSegments = Enumerable.Range(1, segmentCount)
                    .Where(index => index != currentSegment && !segments.ContainsKey(index)).ToArray();
            }

            // Parallel.ForEachAsync uses background workers without one task per segment.
            await Parallel.ForEachAsync(remainingSegments,
                new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = token },
                async (index, workerToken) => await GetDanmakuAsync(index, workerToken));
            if (remainingSegments.Length > 0) await PublishDanmakuAsync();

            async Task GetDanmakuAsync(int index, CancellationToken segmentToken)
            {
                lock (segments)
                {
                    if (segments.ContainsKey(index)) return;
                }

                try
                {
                    segmentToken.ThrowIfCancellationRequested();
                    var danmakus = await service.GetSegmentDanmakusWithGrpcAsync(aid, cid, index, segmentToken);
                    segmentToken.ThrowIfCancellationRequested();
                    lock (segments)
                    {
                        segments[index] = danmakus?.ToList() ?? [];
                    }
                }
                catch (OperationCanceledException) when (segmentToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"获取弹幕分片失败： {index}");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize danmaku.");
            if (IsCurrentDanmaku()) IsDanmakuControlVisible = false;
        }
        finally
        {
            if (ReferenceEquals(_danmakuLoadingCancellation, cancellation))
            {
                _danmakuLoadingCancellation = null;
                if (IsCurrentPlayback(version)) IsDanmakuLoading = false;
            }
        }

        bool IsCurrentDanmaku()
            => IsCurrentPlayback(version) && ReferenceEquals(_danmakuSegments, segments)
                && ReferenceEquals(_danmakuLoadingCancellation, cancellation) && IsDanmakuEnabled && Danmaku.IsShowDanmaku;

        async Task<bool> PublishDanmakuAsync()
        {
            var danmakus = await Task.Run(() =>
            {
                lock (segments)
                {
                    return segments.OrderBy(segment => segment.Key).SelectMany(segment => segment.Value).ToList();
                }
            }, token);
            token.ThrowIfCancellationRequested();
            if (!IsCurrentDanmaku()) return false;

            Danmaku.DanmakuCountText = danmakus.Count > 0
                ? string.Format(ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.DanmakuCountTemplate), danmakus.Count.ToString("N0"))
                : ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.DanmakuEmpty);
            Danmaku.Initialize(danmakus);
            return true;
        }
    }

    private void OnDanmakuVisibilityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DanmakuRenderViewModel.IsShowDanmaku)) return;
        if (Danmaku.IsShowDanmaku)
        {
            _ = InitializeDanmakuAsync();
        }
        else
        {
            _danmakuLoadingCancellation?.Cancel();
            IsDanmakuLoading = false;
        }
    }

    partial void OnIsDanmakuEnabledChanged(bool value)
    {
        IsDanmakuControlVisible = value;
        if (!value)
        {
            _danmakuLoadingCancellation?.Cancel();
            IsDanmakuLoading = false;
        }
        else if (_danmakuCacheVersion == _mediaVersion && Player?.IsPlaybackInitialized == true)
        {
            _ = InitializeDanmakuAsync();
        }
    }

    [RelayCommand]
    private async Task ReloadDanmakuAsync()
    {
        if (Player is null || Window is null || Window.IsClosed || !Player.IsPlaybackInitialized || Danmaku is null)
        {
            return;
        }

        if (_danmakuLoadingCancellation is { } loadingCancellation)
        {
            await loadingCancellation.CancelAsync();
        }

        _danmakuSegments = [];
        Danmaku.ClearAll();
        await InitializeDanmakuAsync();
    }

    /// <summary>
    /// Whether the current account and media allow liking a danmaku.
    /// </summary>
    internal bool CanLikeDanmaku(out bool isSignedIn)
    {
        isSignedIn = this.Get<IBiliTokenResolver>().GetToken() is not null;
        return isSignedIn && TryGetDanmakuCid(out _);
    }

    /// <summary>
    /// Copies one danmaku to the clipboard and reports the result.
    /// </summary>
    internal void CopyDanmakuText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Window?.ShowTip(ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.Copied), InfoType.Success);
        }
        catch (Exception ex)
        {
            // The clipboard can be held by another process for a moment.
            logger.LogWarning(ex, "Failed to copy a danmaku.");
            Window?.ShowTip(ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.DanmakuCopyFailed), InfoType.Error);
        }
    }

    /// <summary>
    /// Reads the like count and the account's like state of one danmaku. Returns null when it is unknown.
    /// </summary>
    internal async Task<(int Likes, bool IsLiked)?> GetDanmakuLikeStateAsync(string danmakuId)
    {
        var version = _mediaVersion;
        if (string.IsNullOrEmpty(danmakuId) || !IsCurrentPlayback(version) || !TryGetDanmakuCid(out var cid)) return null;
        try
        {
            var states = await this.Get<IDanmakuService>().GetDanmakuLikeStatesAsync(cid, new[] { danmakuId }, _activeMediaToken);
            if (!IsCurrentPlayback(version)) return null;
            return states.TryGetValue(danmakuId, out var state) ? (state.Likes, state.UserLiked) : (0, false);
        }
        catch (OperationCanceledException) when (_activeMediaToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception)
        {
            // Kernel exceptions carry the raw response, so only a fixed message is logged.
            logger.LogWarning("Failed to read the danmaku like state.");
            return null;
        }
    }

    /// <summary>
    /// Likes or unlikes one danmaku. A failure is reported to the user and never touches playback.
    /// </summary>
    internal async Task<bool> SetDanmakuLikeAsync(string danmakuId, bool isLike)
    {
        var version = _mediaVersion;
        if (string.IsNullOrEmpty(danmakuId) || !IsCurrentPlayback(version) || !TryGetDanmakuCid(out var cid)) return false;
        if (this.Get<IBiliTokenResolver>().GetToken() is null)
        {
            Window?.ShowTip(ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.DanmakuLikeNeedSignIn), InfoType.Warning);
            return false;
        }

        try
        {
            // A request the user already confirmed is not cancelled by a later media change.
            await this.Get<IDanmakuService>().LikeDanmakuAsync(danmakuId, cid, isLike);
            return true;
        }
        catch (Exception)
        {
            // Kernel exceptions carry the raw response, so only a fixed message is logged.
            logger.LogWarning("Failed to change the danmaku like state.");
            if (IsCurrentPlayback(version))
            {
                Window?.ShowTip(ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.DanmakuLikeFailed), InfoType.Error);
            }

            return false;
        }
    }

    private bool TryGetDanmakuCid(out string cid)
    {
        cid = string.Empty;
        if (_snapshot?.Type == Models.Constants.BiliMediaType.Video && Connector is VideoConnectorViewModel { _part: { } part })
        {
            cid = part.Identifier.Id;
        }
        else if (_snapshot?.Type == Models.Constants.BiliMediaType.Pgc && Connector is PgcConnectorViewModel { _episode: { } episode })
        {
            cid = episode.GetExtensionIfNotNull<long>(EpisodeExtensionDataId.Cid).ToString();
        }

        return !string.IsNullOrEmpty(cid) && cid != "0";
    }
}
