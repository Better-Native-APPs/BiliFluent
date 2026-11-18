// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Pages.Overlay;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Models.Article;
using Richasy.BiliKernel.Models.Media;
using Richasy.BiliKernel.Models.User;
using Richasy.WinUIKernel.Share.Base;
using Windows.System;

namespace BiliCopilot.UI.Toolkits;

/// <summary>
/// Opens what a link in a text leads to, through the same entries the rest of the app uses:
/// the player, the article reader, the user space, the search page, or the default browser.
/// </summary>
internal static class TextLinkNavigator
{
    /// <summary>
    /// Opens a destination. <paramref name="name"/> is what the link was shown as, used as the first
    /// title of the page while the real one loads.
    /// </summary>
    public static async Task OpenAsync(TextLinkDestination destination, string? name = null)
    {
        var services = GlobalDependencies.Services;
        try
        {
            var id = destination.Value ?? string.Empty;
            switch (destination.Kind)
            {
                case TextLinkDestinationKind.User:
                    services.GetRequiredService<NavigationViewModel>().NavigateToOver(typeof(UserSpacePage), new UserProfile(id, name, default));
                    break;
                case TextLinkDestinationKind.Search:
                    services.GetRequiredService<NavigationViewModel>().Search(id);
                    break;
                case TextLinkDestinationKind.Video:
                    var video = new VideoInformation(new MediaIdentifier(id, default, default), default);
                    services.GetRequiredService<AppViewModel>().OpenPlayerCommand.Execute(new MediaSnapshot(video));
                    break;
                case TextLinkDestinationKind.Episode:
                    var episode = new EpisodeInformation(new MediaIdentifier(id, default, default));
                    services.GetRequiredService<AppViewModel>().OpenPlayerCommand.Execute(new MediaSnapshot(default, episode));
                    break;
                case TextLinkDestinationKind.Season:
                    var season = new SeasonInformation(new MediaIdentifier(id, default, default));
                    services.GetRequiredService<AppViewModel>().OpenPlayerCommand.Execute(new MediaSnapshot(season, default));
                    break;
                case TextLinkDestinationKind.Live:
                    await services.GetRequiredService<AppViewModel>().OpenLiveAsync(new LiveInformation(new MediaIdentifier(id, default, default)));
                    break;
                case TextLinkDestinationKind.Article:
                    services.GetRequiredService<NavigationViewModel>().NavigateToOver(typeof(ArticleReaderPage), new ArticleIdentifier(id, name, default, default));
                    break;
                default:
                    // Only http(s) addresses get here (see TextLinkResolver); the system picks the browser.
                    if (destination.WebUri is not { } uri || !await Launcher.LaunchUriAsync(uri).AsTask())
                    {
                        ShowFailure();
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            // Addresses and names come from user content: log only what kind of failure it was.
            services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(TextLinkNavigator))
                .LogError("Failed to open a text link ({Kind}, {ErrorType})", destination.Kind, ex.GetType().Name);
            ShowFailure();
        }
    }

    private static void ShowFailure()
        => GlobalDependencies.Services.GetRequiredService<AppViewModel>().ShowTipCommand
            .Execute((ResourceToolkit.GetLocalizedString(StringNames.TextLinkOpenFailed), InfoType.Error));
}
