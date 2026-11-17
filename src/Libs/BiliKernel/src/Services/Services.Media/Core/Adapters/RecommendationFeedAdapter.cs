// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

using Richasy.BiliKernel.Adapters;
using Richasy.BiliKernel.Models;
using Richasy.BiliKernel.Models.Media;
using System.Globalization;
using System.Text.Json;

namespace Richasy.BiliKernel.Services.Media.Core;

internal static class RecommendationFeedAdapter
{
    public static (IReadOnlyList<VideoInformation> Videos, long Offset) ToRecommendationPage(this RecommendVideoResponse? response, long offset)
    {
        var items = response?.Items ?? throw new KernelException("推荐视频数据为空");
        if (items.Count == 0) return (Array.Empty<VideoInformation>(), offset);

        // The cursor belongs to the raw feed, including cards the desktop cannot display.
        var nextOffset = items[items.Count - 1].Idx;
        var videos = items.Where(card => card.CardGoto == "av" && card.VideoId > 0
                && card.IsAd == 0 && IsEmpty(card.AdInfo))
            .Select(card => card.ToVideoInformation()).Distinct().ToList().AsReadOnly();
        return (videos, nextOffset);
    }

    public static (IReadOnlyList<VideoInformation> Videos, long Offset) ToRecommendationPage(this WebRecommendVideoResponse? response, long offset)
    {
        var items = response?.Items ?? throw new KernelException("推荐视频数据为空");
        if (items.Count == 0) return (Array.Empty<VideoInformation>(), offset);

        var videos = items.Where(card => card.Goto == "av" && card.AvId > 0
                && card.IsAd == 0 && IsEmpty(card.BusinessInfo))
            .Select(ToVideoInformation).Distinct().ToList().AsReadOnly();
        return (videos, offset + 1);
    }

    private static bool IsEmpty(JsonElement value) => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;

    private static VideoInformation ToVideoInformation(WebRecommendCard card)
    {
        var id = card.AvId.ToString(CultureInfo.InvariantCulture);
        var identifier = new MediaIdentifier(id, card.Title, card.Cover.ToVideoCover());
        var publisher = (card.Owner ?? new PublisherInfo { Publisher = string.Empty }).ToPublisherProfile();
        var stat = card.Stat;
        var community = new VideoCommunityInformation(id, stat?.PlayCount, stat?.DanmakuCount,
            stat?.LikeCount, stat?.FavoriteCount, stat?.CoinCount, stat?.ReplyCount, stat?.ShareCount);
        var publishTime = card.PublishTime.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds(card.PublishTime.Value).ToLocalTime()
            : (DateTimeOffset?)null;
        var video = new VideoInformation(identifier, publisher, card.Duration, card.BvId,
            publishTime, communityInformation: community);
        video.AddExtensionIfNotNull(VideoExtensionDataId.Cid, card.Cid);
        video.AddExtensionIfNotNull(VideoExtensionDataId.MediaType, MediaType.Video);
        video.AddExtensionIfNotNull(VideoExtensionDataId.RecommendReason, card.RecommendReason?.Content);
        return video;
    }
}
