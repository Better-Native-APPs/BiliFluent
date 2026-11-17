// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using Richasy.BiliKernel.Models;
using Richasy.BiliKernel.Models.Appearance;
using Richasy.BiliKernel.Models.Comment;

namespace Richasy.BiliKernel.Bili.Comment;

/// <summary>
/// 评论服务.
/// </summary>
public interface ICommentService
{
    /// <summary>
    /// 获取表情包列表.
    /// </summary>
    Task<IReadOnlyList<EmotePackage>> GetEmotePackagesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 切换点赞状态.
    /// </summary>
    Task ToggleLikeAsync(bool isLike, string commentId, string targetId, CommentTargetType type, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取评论列表.
    /// </summary>
    Task<CommentView> GetCommentsAsync(
        string targetId,
        CommentTargetType type,
        CommentSortType sort,
        long offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取评论详情.
    /// </summary>
    Task<CommentView> GetDetailCommentsAsync(
        string targetId,
        CommentTargetType type,
        CommentSortType sort,
        string rootId,
        long offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 发送文本评论.
    /// </summary>
    Task SendTextCommentAsync(
        string message,
        string targetId,
        CommentTargetType type,
        string rootId,
        string parentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取投票详情（评论里的投票）. 已登录时会带上“我投了哪些”.
    /// </summary>
    /// <param name="voteId">投票 Id.</param>
    /// <param name="cancellationToken">终止令牌.</param>
    /// <returns>投票详情；投票已被删除或不可展示时返回 null.</returns>
    Task<CommentVoteDetail?> GetVoteDetailAsync(string voteId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 提交投票.
    /// </summary>
    /// <param name="vote">要投的投票（来自 <see cref="GetVoteDetailAsync"/>）.</param>
    /// <param name="optionIndexes">选中的选项序号（<see cref="CommentVoteOption.Index"/>）.</param>
    /// <param name="voterId">当前登录用户的 Id.</param>
    /// <param name="anonymous">是否匿名投票.</param>
    /// <param name="cancellationToken">终止令牌.</param>
    /// <returns>投票后的最新详情.</returns>
    /// <exception cref="UnauthorizedAccessException">未登录.</exception>
    /// <exception cref="CommentVoteRejectedException">服务端拒绝了这次投票.</exception>
    Task<CommentVoteDetail> SubmitVoteAsync(
        CommentVoteDetail vote,
        IReadOnlyList<int> optionIndexes,
        string voterId,
        bool anonymous = false,
        CancellationToken cancellationToken = default);
}
