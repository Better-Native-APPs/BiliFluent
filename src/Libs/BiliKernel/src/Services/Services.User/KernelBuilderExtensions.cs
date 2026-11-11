// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Services.User;

namespace Richasy.BiliKernel;

/// <summary>
/// 内核构建器扩展.
/// </summary>
public static class KernelBuilderExtensions
{
    /// <summary>
    /// 添加个人资料服务，获取已登录用户的基础信息.
    /// </summary>
    public static IServiceCollection AddMyProfileService(this IServiceCollection builder)
    {
        builder.AddSingleton<IMyProfileService, MyProfileService>();
        return builder;
    }

    /// <summary>
    /// 添加用户关系服务，处理诸如关注、粉丝等关系操作.
    /// </summary>
    public static IServiceCollection AddRelationshipService(this IServiceCollection builder)
    {
        builder.AddSingleton<IRelationshipService, RelationshipService>();
        return builder;
    }

    /// <summary>
    /// 添加稍后再看服务.
    /// </summary>
    public static IServiceCollection AddViewLaterService(this IServiceCollection builder)
    {
        builder.AddSingleton<IViewLaterService, ViewLaterService>();
        return builder;
    }

    /// <summary>
    /// 添加观看历史服务.
    /// </summary>
    public static IServiceCollection AddViewHistoryService(this IServiceCollection builder)
    {
        builder.AddSingleton<IViewHistoryService, ViewHistoryService>();
        return builder;
    }

    /// <summary>
    /// 添加消息服务.
    /// </summary>
    public static IServiceCollection AddMessageService(this IServiceCollection builder)
    {
        builder.AddSingleton<IMessageService, MessageService>();
        return builder;
    }

    /// <summary>
    /// 添加收藏管理服务.
    /// </summary>
    public static IServiceCollection AddFavoriteService(this IServiceCollection builder)
    {
        builder.AddSingleton<IFavoriteService, FavoriteService>();
        return builder;
    }

    /// <summary>
    /// 添加用户服务.
    /// </summary>
    public static IServiceCollection AddUserService(this IServiceCollection builder)
    {
        builder.AddSingleton<IUserService, UserService>();
        return builder;
    }
}
