// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Richasy.BiliKernel.Authenticator;
using Richasy.BiliKernel.Http;

namespace Richasy.BiliKernel;

/// <summary>
/// 内核扩展.
/// </summary>
public static class KernelExtensions
{
    /// <summary>
    /// 添加基础Bili客户端.
    /// </summary>
    /// <param name="builder">内核构建器.</param>
    /// <returns></returns>
    public static IServiceCollection AddBiliClient(this IServiceCollection builder)
    {
        builder.AddSingleton<BiliHttpClient>();
        return builder;
    }

    /// <summary>
    /// 添加Bili认证器.
    /// </summary>
    /// <param name="builder">内核构建器.</param>
    /// <returns><see cref="IServiceCollection"/>.</returns>
    public static IServiceCollection AddBiliAuthenticator(this IServiceCollection builder)
    {
        builder.AddSingleton<BiliAuthenticator>();
        return builder;
    }
}
