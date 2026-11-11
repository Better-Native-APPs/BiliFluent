// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Richasy.BiliKernel.Bili.Search;
using Richasy.BiliKernel.Services.Search;

namespace Richasy.BiliKernel;

/// <summary>
/// 内核构建器扩展.
/// </summary>
public static class KernelBuilderExtensions
{
    /// <summary>
    /// 添加搜索服务.
    /// </summary>
    public static IServiceCollection AddSearchService(this IServiceCollection builder)
    {
        builder.AddSingleton<ISearchService, SearchService>();
        return builder;
    }
}
