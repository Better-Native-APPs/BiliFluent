// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Richasy.BiliKernel.Authorizers.TV;
using Richasy.BiliKernel.Bili.Authorization;

namespace Richasy.BiliKernel;

/// <summary>
/// 内核构建器扩展.
/// </summary>
public static class KernelBuilderExtensions
{
    /// <summary>
    /// 添加电视端授权器.
    /// </summary>
    public static IServiceCollection AddTVAuthenticationService(this IServiceCollection builder)
    {
        builder.AddSingleton<IAuthenticationService, TVAuthenticationService>();
        return builder;
    }
}
