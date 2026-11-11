// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Services.Media;

namespace Richasy.BiliKernel;

/// <summary>
/// 内核构建器扩展.
/// </summary>
public static class KernelBuilderExtensions
{
    /// <summary>
    /// 添加视频探索服务.
    /// </summary>
    public static IServiceCollection AddVideoDiscoveryService(this IServiceCollection builder)
    {
        builder.AddSingleton<IVideoDiscoveryService, VideoDiscoveryService>();
        return builder;
    }

    /// <summary>
    /// 添加直播探索服务.
    /// </summary>
    public static IServiceCollection AddLiveDiscoveryService(this IServiceCollection builder)
    {
        builder.AddSingleton<ILiveDiscoveryService, LiveDiscoveryService>();
        return builder;
    }

    /// <summary>
    /// 添加动漫/电影/电视剧/纪录片服务.
    /// </summary>
    public static IServiceCollection AddEntertainmentDiscoveryService(this IServiceCollection builder)
    {
        builder.AddSingleton<IEntertainmentDiscoveryService, EntertainmentDiscoveryService>();
        return builder;
    }

    /// <summary>
    /// 添加播放服务.
    /// </summary>
    public static IServiceCollection AddPlayerService(this IServiceCollection builder)
    {
        builder.AddSingleton<IPlayerService, PlayerService>();
        return builder;
    }

    /// <summary>
    /// 添加弹幕服务.
    /// </summary>
    public static IServiceCollection AddDanmakuService(this IServiceCollection builder)
    {
        builder.AddSingleton<IDanmakuService, DanmakuService>();
        return builder;
    }

    /// <summary>
    /// 添加字幕服务.
    /// </summary>
    public static IServiceCollection AddSubtitleService(this IServiceCollection builder)
    {
        builder.AddSingleton<ISubtitleService, SubtitleService>();
        return builder;
    }
}
