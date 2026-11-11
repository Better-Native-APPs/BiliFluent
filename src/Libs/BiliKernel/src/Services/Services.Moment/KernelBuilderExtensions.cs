// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Richasy.BiliKernel.Bili.Moment;
using Richasy.BiliKernel.Services.Moment;

namespace Richasy.BiliKernel;

/// <summary>
/// 内核构建器扩展.
/// </summary>
public static class KernelBuilderExtensions
{
    /// <summary>
    /// 添加动态发现服务.
    /// </summary>
    public static IServiceCollection AddMomentDiscoveryService(this IServiceCollection builder)
    {
        builder.AddSingleton<IMomentDiscoveryService, MomentDiscoveryService>();
        return builder;
    }

    /// <summary>
    /// 添加动态操作服务.
    /// </summary>
    public static IServiceCollection AddMomentOperationService(this IServiceCollection builder)
    {
        builder.AddSingleton<IMomentOperationService, MomentOperationService>();
        return builder;
    }
}
