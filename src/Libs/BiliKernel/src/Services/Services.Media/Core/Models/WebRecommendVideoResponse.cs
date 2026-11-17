// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

using Richasy.BiliKernel.Services.Media.Core.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Richasy.BiliKernel.Services.Media.Core;

internal sealed class WebRecommendVideoResponse
{
    [JsonPropertyName("item")]
    public IList<WebRecommendCard>? Items { get; set; }
}

internal sealed class WebRecommendCard
{
    [JsonPropertyName("id")]
    public long AvId { get; set; }

    [JsonPropertyName("bvid")]
    public string? BvId { get; set; }

    [JsonPropertyName("cid")]
    public long? Cid { get; set; }

    [JsonPropertyName("goto")]
    public string? Goto { get; set; }

    [JsonPropertyName("pic")]
    public string? Cover { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("duration")]
    public int? Duration { get; set; }

    [JsonPropertyName("pubdate")]
    public long? PublishTime { get; set; }

    [JsonPropertyName("owner")]
    public PublisherInfo? Owner { get; set; }

    [JsonPropertyName("stat")]
    public VideoStatusInfo? Stat { get; set; }

    [JsonPropertyName("rcmd_reason")]
    public CuratedPlaylistReason? RecommendReason { get; set; }

    [JsonPropertyName("business_info")]
    public JsonElement BusinessInfo { get; set; }

    [JsonPropertyName("is_ad")]
    public int IsAd { get; set; }
}
