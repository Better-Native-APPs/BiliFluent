// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Media;

/// <summary>
/// 播放器格式信息.
/// </summary>
public sealed class PlayerFormatInformation(int quality, string description, bool needVip, bool needLogin, int eotf)
{
    /// <summary>
    /// 创建默认 SDR 格式，保留现有依赖使用的构造函数.
    /// </summary>
    /// <param name="quality">画质.</param>
    /// <param name="description">描述.</param>
    /// <param name="needVip">是否需要大会员.</param>
    /// <param name="needLogin">是否需要登录.</param>
    public PlayerFormatInformation(int quality, string description, bool needVip, bool needLogin)
        : this(quality, description, needVip, needLogin, 0)
    {
    }

    /// <summary>
    /// 视频清晰度.
    /// </summary>
    public int Quality { get; } = quality;

    /// <summary>
    /// 清晰度描述.
    /// </summary>
    public string Description { get; } = description;

    /// <summary>
    /// 是否需要登录或大会员才能观看.
    /// </summary>
    public bool NeedVip { get; } = needVip;

    /// <summary>
    /// 是否需要登录才能观看.
    /// </summary>
    public bool NeedLogin { get; } = needLogin;

    /// <summary>
    /// 直播流的电光传递函数，0-SDR, 1-PQ, 2-HLG；点播格式默认为 0.
    /// </summary>
    public int Eotf { get; } = eotf;

    /// <summary>
    /// 是否为 HDR 直播格式.
    /// </summary>
    public bool IsHdr => Eotf > 0;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PlayerFormatInformation information
        && Quality == information.Quality && Eotf == information.Eotf;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Quality, Eotf);
}
