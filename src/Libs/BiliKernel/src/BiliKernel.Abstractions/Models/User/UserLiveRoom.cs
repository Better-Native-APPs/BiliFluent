// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.User;

/// <summary>
/// 用户直播间状态.
/// </summary>
public sealed class UserLiveRoom
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UserLiveRoom"/> class.
    /// </summary>
    /// <param name="roomStatus">直播间开通状态.</param>
    /// <param name="liveStatus">直播状态.</param>
    /// <param name="roomId">直播间标识.</param>
    /// <param name="title">直播间标题.</param>
    public UserLiveRoom(int roomStatus, int liveStatus, long roomId, string? title)
    {
        RoomStatus = roomStatus;
        LiveStatus = liveStatus;
        RoomId = roomId;
        Title = title;
    }

    /// <summary>
    /// 直播间开通状态，1 表示已开通.
    /// </summary>
    public int RoomStatus { get; }

    /// <summary>
    /// 直播状态，1 表示正在直播.
    /// </summary>
    public int LiveStatus { get; }

    /// <summary>
    /// 直播间标识.
    /// </summary>
    public long RoomId { get; }

    /// <summary>
    /// 直播间标题.
    /// </summary>
    public string? Title { get; }

    /// <summary>
    /// 是否已开通直播间且正在直播.
    /// </summary>
    public bool IsLiving => RoomStatus == 1 && LiveStatus == 1;
}
