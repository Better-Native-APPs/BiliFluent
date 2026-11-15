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
    /// Submits a rating at the specified playback progress in milliseconds.
    /// </summary>
    /// <param name="aid">The video aid.</param>
    /// <param name="cid">The video cid.</param>
    /// <param name="progressMs">The playback progress in milliseconds.</param>
    /// <param name="gradeId">The rating identifier from the command payload.</param>
    /// <param name="gradeScore">An even score: 2, 4, 6, 8, or 10.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the score is submitted.</returns>
    Task GradeDanmakuAsync(string aid, string cid, int progressMs, string gradeId, int gradeScore, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits an original server-supplied option index for a voting danmaku.
    /// </summary>
    /// <param name="aid">The video aid.</param>
    /// <param name="cid">The video cid.</param>
    /// <param name="progressMs">The playback progress in milliseconds.</param>
    /// <param name="voteId">The voting identifier from vote_id.</param>
    /// <param name="voteType">The voting type from vote_type, or zero when absent.</param>
    /// <param name="commandId">The command danmaku identifier as a string.</param>
    /// <param name="optionId">The option's original idx, not its array position.</param>
    /// <param name="hasSelfDefinition">Whether the selected option has has_self_def.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the vote is submitted.</returns>
    /// <exception cref="UnauthorizedAccessException">The server requires sign-in.</exception>
    Task VoteDanmakuAsync(string aid, string cid, int progressMs, string voteId, int voteType,
        string commandId, int optionId, bool hasSelfDefinition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets voting information; missing participant and option counts remain null.
    /// </summary>
    Task<VoteCommandDanmakuPayload> GetDanmakuVoteInfoAsync(string voteId, CancellationToken cancellationToken = default);

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
