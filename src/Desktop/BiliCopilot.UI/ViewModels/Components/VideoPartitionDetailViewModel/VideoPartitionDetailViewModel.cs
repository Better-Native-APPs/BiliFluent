// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models;
using Richasy.BiliKernel.Models.Media;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 视频分区详情视图模型.
/// </summary>
public sealed partial class VideoPartitionDetailViewModel : ViewModelBase<PartitionViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="VideoPartitionDetailViewModel"/> class.
    /// </summary>
    public VideoPartitionDetailViewModel(
        PartitionViewModel partition,
        IVideoDiscoveryService service)
        : base(partition)
    {
        _service = service;
        _logger = this.Get<ILogger<VideoPartitionDetailViewModel>>();
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (Videos.Count > 0)
        {
            return;
        }

        if (Data.Children is not null)
        {
            var children = Data.Children.ToList();
            var rcmdPartition = new Partition(Data.Data.Id, ResourceToolkit.GetLocalizedString(StringNames.Recommend));
            children.Insert(0, new PartitionViewModel(rcmdPartition));
            Children = [.. children];
        }

        SortTypes = [.. Enum.GetValues<PartitionVideoSortType>()];
        SelectedSortType = PartitionVideoSortType.Default;
        await ChangeChildPartitionAsync(Children.First());
        Initialized?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task ChangeSortTypeAsync(PartitionVideoSortType sortType)
    {
        if (IsRecommend)
        {
            return;
        }

        SelectedSortType = sortType;
        _videoGeneration++;
        _childPartitionVideoCache.Clear();
        _recentChildPartitions.Clear();
        Videos.Clear();
        _childPartitionOffsetCache.Clear();
        await LoadVideosAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _videoGeneration++;
        if (IsRecommend)
        {
            _recommendOffset = 0;
            _recommendVideoCache = null;
        }
        else
        {
            EvictChildPartition(CurrentPartition.Data.Id);
        }

        Videos.Clear();
        await LoadVideosAsync();
    }

    [RelayCommand]
    private async Task ChangeChildPartitionAsync(PartitionViewModel partition)
    {
        if (partition is null || partition.Data.Equals(CurrentPartition?.Data))
        {
            return;
        }

        CacheCurrentPartition();
        IsRecommend = partition.Data.Id == Data.Data.Id;
        CurrentPartition = partition;
        _videoGeneration++;
        Videos.Clear();
        if (IsRecommend && _recommendVideoCache is not null && _recommendVideoCache.Count > 0)
        {
            foreach (var item in _recommendVideoCache)
            {
                Videos.Add(item);
            }

            _recommendVideoCache = null;
        }
        else if (!IsRecommend && _childPartitionVideoCache.Remove(CurrentPartition.Data.Id, out var cache))
        {
            _recentChildPartitions.Remove(CurrentPartition.Data.Id);
            foreach (var item in cache)
            {
                Videos.Add(item);
            }
        }

        while (_recentChildPartitions.Count > ChildPartitionCacheLimit)
        {
            EvictChildPartition(_recentChildPartitions.Last!.Value);
        }

        if (Videos.Count == 0)
        {
            await LoadVideosAsync();
        }
        else
        {
            VideoListUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CacheCurrentPartition()
    {
        if (CurrentPartition is null) return;
        // Keep whole lists with their exact cursors, or restart from the first page.
        if (IsRecommend)
        {
            _recommendVideoCache = Videos.Count is > 0 and <= VideoCacheLimit ? Videos.ToList() : null;
            if (_recommendVideoCache is null) _recommendOffset = 0;
        }
        else if (Videos.Count is > 0 and <= VideoCacheLimit)
        {
            var id = CurrentPartition.Data.Id;
            _childPartitionVideoCache[id] = Videos.ToList();
            _recentChildPartitions.Remove(id);
            _recentChildPartitions.AddFirst(id);
        }
        else
        {
            EvictChildPartition(CurrentPartition.Data.Id);
        }
    }

    private void EvictChildPartition(string id)
    {
        _childPartitionVideoCache.Remove(id);
        _childPartitionOffsetCache.Remove(id);
        _recentChildPartitions.Remove(id);
    }

    [RelayCommand]
    private async Task LoadVideosAsync()
    {
        if (IsVideoLoading)
        {
            return;
        }

        IsVideoLoading = true;
        var generation = _videoGeneration;
        if (IsRecommend)
        {
            await LoadRecommendVideosAsync(generation);
        }
        else
        {
            await LoadChildPartitionVideosAsync(generation);
        }

        IsVideoLoading = false;
        if (generation != _videoGeneration && Videos.Count == 0)
        {
            // A switch/refresh during the old request still needs its own first page.
            await LoadVideosAsync();
            return;
        }

        VideoListUpdated?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadRecommendVideosAsync(long generation)
    {
        try
        {
            var (videos, offset) = await _service.GetPartitionRecommendVideoListAsync(Data.Data, _recommendOffset);
            if (generation != _videoGeneration) return;
            _recommendOffset = offset;
            TryAddVideos(videos, VideoCardStyle.Partition);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "尝试加载分区推荐视频时出错.");
        }
    }

    private async Task LoadChildPartitionVideosAsync(long generation)
    {
        var partition = CurrentPartition;
        try
        {
            var offset = 0L;
            var pn = 0;
            if (_childPartitionOffsetCache.TryGetValue(partition.Data.Id, out var offsetCache))
            {
                offset = offsetCache.Offset;
                pn = offsetCache.PageNumber;
            }

            var (videos, nextOffset, nextPn) = await _service.GetChildPartitionVideoListAsync(partition.Data, offset, pn, SelectedSortType);
            if (generation != _videoGeneration) return;
            _childPartitionOffsetCache[partition.Data.Id] = (nextOffset, nextPn);
            TryAddVideos(videos, VideoCardStyle.Partition);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"尝试加载 {Data.Data.Name}/{partition.Data.Name} 分区视频时出错.");
        }
    }

    private void TryAddVideos(IReadOnlyList<VideoInformation> videos, VideoCardStyle style)
    {
        if (videos is not null)
        {
            foreach (var item in videos)
            {
                Videos.Add(new VideoItemViewModel(item, style));
            }
        }
    }
}
