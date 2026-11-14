// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using Richasy.BiliKernel.Authenticator;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Http;
using Richasy.BiliKernel.Models;
using Richasy.BiliKernel.Models.Danmaku;
using Richasy.BiliKernel.Services.Media.Core;

namespace Richasy.BiliKernel.Services.Media;

/// <summary>
/// 弹幕服务.
/// </summary>
public sealed class DanmakuService : IDanmakuService
{
    private readonly DanmakuClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="DanmakuService"/> class.
    /// </summary>
    public DanmakuService(
        BiliHttpClient httpClient,
        BiliAuthenticator authenticator,
        IBiliTokenResolver tokenResolver)
    {
        _client = new DanmakuClient(httpClient, authenticator, tokenResolver);
    }

    /// <inheritdoc/>
    public Task<DanmakuMeta> GetDanmakuMetaAsync(string aid, string cid, CancellationToken cancellationToken = default)
        => _client.GetDanmakuMetaAsync(aid, cid, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<DanmakuInformation>> GetSegmentDanmakusWithGrpcAsync(string aid, string cid, int segmentIndex, CancellationToken cancellationToken = default)
        => _client.GetSegmentDanmakusWithGrpcAsync(aid, cid, segmentIndex, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<DanmakuInformation>> GetSegmentDanmakusWithWebAsync(string aid, string cid, int segmentIndex, CancellationToken cancellationToken = default)
        => _client.GetSegmentDanmakusWithWebAsync(aid, cid, segmentIndex, cancellationToken);

    /// <inheritdoc/>
    public Task LikeDanmakuAsync(string danmakuId, string cid, bool isLike, CancellationToken cancellationToken = default)
        => _client.LikeDanmakuAsync(danmakuId, cid, isLike, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<string, (int Likes, bool UserLiked)>> GetDanmakuLikeStatesAsync(string cid, IEnumerable<string> danmakuIds, CancellationToken cancellationToken = default)
        => _client.GetDanmakuLikeStatesAsync(cid, danmakuIds, cancellationToken);

    /// <inheritdoc/>
    public Task ReportDanmakuAsync(string danmakuId, string cid, int reason, string? content = null, CancellationToken cancellationToken = default)
        => _client.ReportDanmakuAsync(danmakuId, cid, reason, content, cancellationToken);

    /// <inheritdoc/>
    public Task SendLiveDanmakuAsync(string content, string roomId, string color, bool isStandardSize = true, DanmakuLocation location = DanmakuLocation.Scroll, CancellationToken cancellationToken = default)
        => _client.SendLiveDanmakuAsync(content, roomId, color, isStandardSize, location, cancellationToken);

    /// <inheritdoc/>
    public Task SendVideoDanmakuAsync(string content, string aid, string cid, int progress, string color, bool isStandardSize = true, DanmakuLocation location = DanmakuLocation.Scroll, CancellationToken cancellationToken = default)
        => _client.SendVideoDanmakuAsync(content, aid, cid, progress, color, isStandardSize, location, cancellationToken);
}
