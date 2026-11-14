// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Richasy.BiliKernel.Services.Media.Core.Models;

/// <summary>
/// The like state of a video danmaku.
/// </summary>
internal sealed class DanmakuLikeState
{
    /// <summary>
    /// Gets or sets the number of likes.
    /// </summary>
    [JsonPropertyName("likes")]
    public int Likes { get; set; }

    /// <summary>
    /// Gets or sets whether the current user has liked the danmaku (0 or 1).
    /// </summary>
    [JsonPropertyName("user_like")]
    public int UserLike { get; set; }
}
