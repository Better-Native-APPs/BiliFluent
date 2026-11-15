// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Models.Constants;

/// <summary>
/// 偏好视频质量类型.
/// </summary>
public enum PreferQualityType
{
    /// <summary>
    /// 最高画质. 与 <see cref="Auto"/> 的选择相同，仅为兼容已保存的设置而保留，设置页不再列出.
    /// </summary>
    High,

    /// <summary>
    /// 4K 优先.
    /// </summary>
    UHD,

    /// <summary>
    /// 高清（1080P）优先.
    /// </summary>
    HD,

    /// <summary>
    /// 自动（每次播放都选当前账号能播的最高清晰度）.
    /// </summary>
    Auto,
}
