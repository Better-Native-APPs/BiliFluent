// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Media;

/// <summary>
/// 直播线路信息.
/// </summary>
public sealed class LiveLineInformation(
    string name,
    int quality,
    IList<int> acceptQualities,
    IList<LivePlayUrl> urls,
    int eotf,
    string? format = null)
{
    /// <summary>
    /// 创建默认 SDR 线路，保留现有依赖使用的构造函数.
    /// </summary>
    /// <param name="name">编码名称.</param>
    /// <param name="quality">当前画质.</param>
    /// <param name="acceptQualities">支持的画质.</param>
    /// <param name="urls">播放地址.</param>
    public LiveLineInformation(string name, int quality, IList<int> acceptQualities, IList<LivePlayUrl> urls)
        : this(name, quality, acceptQualities, urls, 0, null)
    {
    }

    /// <summary>
    /// 解码名.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// 清晰度标识.
    /// </summary>
    public int Quality { get; } = quality;

    /// <summary>
    /// 电光传递函数，0-SDR, 1-PQ, 2-HLG.
    /// </summary>
    public int Eotf { get; } = eotf;

    /// <summary>
    /// 是否为 HDR 直播线路.
    /// </summary>
    public bool IsHdr => Eotf > 0;

    /// <summary>
    /// 接口返回的媒体格式名称.
    /// </summary>
    public string? Format { get; } = format;

    /// <summary>
    /// 支持的清晰度标识.
    /// </summary>
    public IList<int> AcceptQualities { get; } = acceptQualities;

    /// <summary>
    /// 播放地址列表.
    /// </summary>
    public IList<LivePlayUrl> Urls { get; } = urls;
}
