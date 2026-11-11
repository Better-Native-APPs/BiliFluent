// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Extensions;
using BiliCopilot.UI.Resolvers;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel;
using Richasy.WinUIKernel.Share;
using Richasy.WinUIKernel.Share.Toolkits;
using Richasy.WinUIKernel.Share.ViewModels;
using Serilog;

namespace BiliCopilot.UI;

internal static class GlobalDependencies
{
    /// <summary>
    /// 获取服务提供程序.
    /// </summary>
    public static IServiceProvider Services { get; private set; }

    public static void Initialize()
    {
        if (Services is not null)
        {
            return;
        }

        Services = new ServiceCollection()
            .AddSerilog()
            .AddDispatcherQueue()
            .AddShareToolkits()
            .AddXamlRootProvider()

            .AddBiliClient()
            .AddBiliAuthenticator()
            .AddWinUICookiesResolver()
            .AddWinUITokenResolver()
            .AddWinUIQRCodeResolver(RenderQRCodeAsync)
            .AddTVAuthenticationService()
            .AddMyProfileService()
            .AddUserService()
            .AddSingleton<BiliCopilot.UI.Services.UserSpace.UserSpaceService>()
            .AddRelationshipService()
            .AddViewLaterService()
            .AddViewHistoryService()
            .AddCommentService()
            .AddVideoDiscoveryService()
            .AddLiveDiscoveryService()
            .AddEntertainmentDiscoveryService()
            .AddArticleDiscoveryService()
            .AddSingleton<BiliCopilot.UI.Services.Articles.ArticleRawHtmlClient>()
            .AddSingleton<BiliCopilot.UI.Services.Articles.ArticleDetailReader>()
            .AddArticleOperationService()
            .AddMomentDiscoveryService()
            .AddMomentOperationService()
            .AddMessageService()
            .AddMessageNotifications()
            .AddFavoriteService()
            .AddSearchService()
            .AddDanmakuService()
            .AddSubtitleService()
            .AddPlayerService()

            .AddTransient<CommentMainViewModel>()
            .AddSingleton<PinnerViewModel>()
            .AddSingleton<AppViewModel>()
            .AddSingleton<BiliCopilot.UI.Live.Application.LiveApplicationBridge>()
            .AddSingleton<BiliCopilot.UI.Classic.ClassicPlayerLauncher>()
            .AddSingleton<EmoteModuleViewModel>()
            .AddSingleton<NavigationViewModel>()
            .AddSingleton<StartupPageViewModel>()
            .AddSingleton<AccountViewModel>()
            .AddSingleton<SearchBoxViewModel>()
            .AddSingleton<PopularPageViewModel>()
            .AddSingleton<MomentPageViewModel>()
            .AddSingleton<VideoPartitionPageViewModel>()
            .AddSingleton<LivePartitionPageViewModel>()
            .AddSingleton<ArticlePartitionPageViewModel>()
            .AddSingleton<AnimePageViewModel>()
            .AddSingleton<CinemaPageViewModel>()
            .AddSingleton<FansPageViewModel>()
            .AddSingleton<FollowsPageViewModel>()
            .AddSingleton<SearchPageViewModel>()
            .AddSingleton<ArticleReaderPageViewModel>()
            .AddSingleton<ViewLaterPageViewModel>()
            .AddSingleton<HistoryPageViewModel>()
            .AddSingleton<FavoritesPageViewModel>()
            .AddSingleton<MessagePageViewModel>()
            .AddSingleton<SettingsPageViewModel>()
            .AddSingleton<BiliCopilot.UI.SponsorBlock.Content.SponsorContentProjectionService>()
            .AddSingleton<BiliCopilot.UI.SponsorBlock.Integration.SponsorRuntime>()
            .AddSingleton<UserSpacePageViewModel>()
            .AddSingleton<NotificationViewModel>()
            .AddTransient<UserMomentDetailViewModel>()
            .AddTransient<VideoMediaSourceResolver>()
            .AddTransient<VideoMediaHistoryResolver>()
            .AddTransient<PgcMediaSourceResolver>()
            .AddTransient<PgcMediaHistoryResolver>()
            .AddTransient<PlayerViewModel>()
            .AddTransient<DanmakuSendViewModel>()
            .AddNotificationViewModel()
            .BuildServiceProvider();

        // The shared controls still require this adapter; all registrations and resolution use Microsoft DI.
        new RichasyKernel.Kernel(Services).InitializeShareKernel(Services.GetRequiredService<ILogger<App>>());
    }

    public static IServiceCollection AddDispatcherQueue(this IServiceCollection services)
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        return services.AddSingleton(queue);
    }

    public static IServiceCollection AddMessageNotifications(this IServiceCollection services)
        => services.AddSingleton<BiliCopilot.UI.Services.Messages.IMessageNotificationService, BiliCopilot.UI.Services.Messages.MessageNotificationService>();

    public static IServiceCollection AddNotificationViewModel(this IServiceCollection services)
        => services.AddSingleton<INotificationViewModel, NotificationViewModel>();

    public static IServiceCollection AddShareToolkits(this IServiceCollection services)
        => services.AddSingleton<IAppToolkit, AppToolkit>()
            .AddSingleton<ISettingsToolkit, SettingsToolkit>()
            .AddSingleton<IFileToolkit, FileToolkit>()
            .AddSingleton<IResourceToolkit, ResourceToolkit>()
            .AddSingleton<IFontToolkit, SharedFontToolkit>();

    public static IServiceCollection AddXamlRootProvider(this IServiceCollection services)
        => services.AddSingleton<IXamlRootProvider, XamlRootProvider>();

    public static IServiceCollection AddSerilog(this IServiceCollection services)
    {
        var loggerPath = Path.Combine(Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalFolder.Path, "Logger");
        if (!Directory.Exists(loggerPath))
        {
            Directory.CreateDirectory(loggerPath);
        }

        // Create a logger with current date.
        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(Path.Combine(loggerPath, $"log-{DateTimeOffset.Now:yyyy-MM-dd}.txt"))
            .CreateLogger();

        return services.AddLogging(b => b.AddSerilog(dispose: true));
    }

    public static T Get<T>(this object ele)
        where T : class
        => Services.GetRequiredService<T>();

    private static Task RenderQRCodeAsync(byte[] imageData)
    {
        var vm = Services.GetRequiredService<StartupPageViewModel>();
        vm.RenderQRCodeCommand.Execute(imageData);
        return Task.CompletedTask;
    }
}
