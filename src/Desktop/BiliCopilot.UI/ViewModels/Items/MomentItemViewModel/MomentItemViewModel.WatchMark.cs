// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Services.WatchRecords;
using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// 动态条目视图模型：视频封面上的已看标记.
/// </summary>
public sealed partial class MomentItemViewModel
{
    private readonly bool _showWatchMark;

    /// <summary>
    /// Refreshes the marks of the items that show the given video, or of all items when no video is given.
    /// </summary>
    internal static void RefreshWatchMarks(IEnumerable<MomentItemViewModel> items, WatchRecordIndex index, string? aid)
    {
        foreach (var item in items)
        {
            if (aid is null || item.FindInnerContent<VideoInformation>()?.Identifier.Id == aid)
            {
                item.RefreshWatchMark(index);
            }
        }
    }

    /// <summary>
    /// Call after a feed page has been added to the list. It pulls watch records in the background,
    /// which then update the marks through <see cref="WatchRecordIndex.Changed"/>, and writes the diagnostic line of this load.
    /// </summary>
    /// <param name="index">Watch record index.</param>
    /// <param name="added">Items this load added.</param>
    /// <param name="feedStart">True for the first page of a feed.</param>
    internal static void TrackFeedLoad(WatchRecordIndex index, IReadOnlyCollection<MomentItemViewModel> added, bool feedStart)
        => _ = TrackFeedLoadAsync(index, added, feedStart);

    /// <summary>
    /// Works out the mark again: the watch record of the video when there is one, otherwise what the card itself reported.
    /// Episodes only use what the card reported, because their watch records cannot be matched to a card reliably.
    /// </summary>
    internal void RefreshWatchMark(WatchRecordIndex index)
    {
        if (!_showWatchMark)
        {
            return;
        }

        WatchMark? mark = null;
        if (FindInnerContent<VideoInformation>() is VideoInformation video)
        {
            var fromCard = video.GetExtensionIfNotNull<VideoWatchProgress>(VideoExtensionDataId.WatchProgress);
            mark = WatchMark.Create(index.Resolve(video.Identifier.Id, fromCard));
        }
        else if (FindInnerContent<EpisodeInformation>() is EpisodeInformation episode)
        {
            mark = WatchMark.Create(episode.GetExtensionIfNotNull<VideoWatchProgress>(EpisodeExtensionDataId.WatchProgress));
        }

        WatchMark = mark;
        switch (InnerContent)
        {
            case VideoItemViewModel inner:
                inner.WatchMark = mark;
                break;
            case EpisodeItemViewModel inner:
                inner.WatchMark = mark;
                break;
            case MomentItemViewModel forward:
                forward.RefreshWatchMark(index);
                break;
        }
    }

    private static async Task TrackFeedLoadAsync(WatchRecordIndex index, IReadOnlyCollection<MomentItemViewModel> added, bool feedStart)
    {
        // Never throws: signed out, throttled and failed pulls all end quietly.
        await index.SyncAsync(feedStart);

        var cards = 0;
        var serverField = 0;
        var matched = 0;
        foreach (var item in added)
        {
            if (item.FindInnerContent<VideoInformation>() is VideoInformation video)
            {
                cards++;
                serverField += video.GetExtensionIfNotNull<bool>(VideoExtensionDataId.HasShowProgressFlag) ? 1 : 0;
                matched += index.Contains(video.Identifier.Id) ? 1 : 0;
            }
            else if (item.FindInnerContent<EpisodeInformation>() is EpisodeInformation episode)
            {
                cards++;
                serverField += episode.GetExtensionIfNotNull<bool>(EpisodeExtensionDataId.HasShowProgressFlag) ? 1 : 0;
            }
        }

        index.ReportFeedLoad(cards, serverField, matched);
    }
}
