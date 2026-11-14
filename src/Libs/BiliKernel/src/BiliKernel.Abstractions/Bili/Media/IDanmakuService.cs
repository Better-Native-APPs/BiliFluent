// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using Richasy.BiliKernel.Models;
using Richasy.BiliKernel.Models.Danmaku;

namespace Richasy.BiliKernel.Bili.Media;

/// <summary>
/// 弹幕服务.
/// </summary>
public interface IDanmakuService
{
    /// <summary>
    /// 获取弹幕元信息.
    /// </summary>
    Task<DanmakuMeta> GetDanmakuMetaAsync(string aid, string cid, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取分段弹幕.
    /// </summary>
    Task<IReadOnlyList<DanmakuInformation>> GetSegmentDanmakusWithGrpcAsync(string aid, string cid, int segmentIndex, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取分段弹幕（Web）.
    /// </summary>
    Task<IReadOnlyList<DanmakuInformation>> GetSegmentDanmakusWithWebAsync(string aid, string cid, int segmentIndex, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or removes a like on a video danmaku.
    /// </summary>
    Task LikeDanmakuAsync(string danmakuId, string cid, bool isLike, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the like counts and the current user's like states for the requested danmakus.
    /// </summary>
    Task<IReadOnlyDictionary<string, (int Likes, bool UserLiked)>> GetDanmakuLikeStatesAsync(string cid, IEnumerable<string> danmakuIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports a video danmaku with an optional description.
    /// </summary>
    Task ReportDanmakuAsync(string danmakuId, string cid, int reason, string? content = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 发送视频弹幕.
    /// </summary>
    Task SendVideoDanmakuAsync(string content, string aid, string cid, int progress, string color, bool isStandardSize = true, DanmakuLocation location = DanmakuLocation.Scroll, CancellationToken cancellationToken = default);

    /// <summary>
    /// 发送直播弹幕.
    /// </summary>
    Task SendLiveDanmakuAsync(string content, string roomId, string color, bool isStandardSize = true, DanmakuLocation location = DanmakuLocation.Scroll, CancellationToken cancellationToken = default);
}
