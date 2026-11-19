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
    // Created with the first short link that is opened; showing text never asks the site anything.
    private static readonly Lazy<ShortLinkRequests> ShortLinks
        = new(() => new ShortLinkRequests(new ShortLinkResolver(ShortLinkResolver.CreateHandler())));

    /// <summary>
    /// Opens a destination. <paramref name="name"/> is what the link was shown as, used as the first
    /// title of the page while the real one loads. A short link is first asked about, which takes a
    /// moment: <paramref name="origin"/> tells the link that was pressed from any other, and
    /// <paramref name="isStillShown"/> says whether the text it sits in is still on screen when the
    /// answer arrives. The answer is dropped when it is not, or when another link was opened meanwhile.
    /// </summary>
    public static async Task OpenAsync(TextLinkDestination destination, string? name = null, object? origin = null, Func<bool>? isStillShown = null)
    {
        var services = GlobalDependencies.Services;
        try
        {
            if (destination is { IsShortLink: true, WebUri: { } shortLink })
            {
                if (await FollowShortLinkAsync(shortLink, origin ?? destination, isStillShown, services) is not { } place)
                {
                    return;
                }

                destination = place;
            }
            else if (ShortLinks.IsValueCreated)
            {
                // Opening anything else voids a short link that is still being asked about.
                ShortLinks.Value.Void();
            }

            // Protocol activations use this same lease check to discard a superseded UI handoff.
            if (isStillShown?.Invoke() == false) return;
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
                    await services.GetRequiredService<AppViewModel>().OpenPlayerCommand.ExecuteAsync(new MediaSnapshot(video)
                    {
                        StartPosition = destination.StartPosition,
                        PreferPart = destination.PartId is { } cid ? new VideoPart(new MediaIdentifier(cid, default, default), 0, 0) : null,
                        PreferPartNumber = destination.PartNumber,
                    });
                    break;
                case TextLinkDestinationKind.Episode:
                    var episode = new EpisodeInformation(new MediaIdentifier(id, default, default));
                    await services.GetRequiredService<AppViewModel>().OpenPlayerCommand.ExecuteAsync(new MediaSnapshot(default, episode));
                    break;
                case TextLinkDestinationKind.Season:
                    var season = new SeasonInformation(new MediaIdentifier(id, default, default));
                    await services.GetRequiredService<AppViewModel>().OpenPlayerCommand.ExecuteAsync(new MediaSnapshot(season, default));
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

    // Where a short link leads, or null when its answer is not to be acted on. When the site does not
    // name a place inside the app, for whatever reason, the link goes to the browser as it was written.
    private static async Task<TextLinkDestination?> FollowShortLinkAsync(Uri shortLink, object key, Func<bool>? isStillShown, IServiceProvider services)
    {
        if (await ShortLinks.Value.AskAsync(key, shortLink) is not { } result)
        {
            // The same link pressed again while it was being asked about: the first press opens it.
            return null;
        }

        var isShown = isStillShown?.Invoke() != false;
        var isUsed = result.Outcome != ShortLinkOutcome.Superseded && isShown;

        // Addresses come from user content: log only how it went.
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(TextLinkNavigator));
        if (logger.IsEnabled(LogLevel.Information))
        {
            var error = result.ErrorType ?? "none";
            var use = isUsed ? "used" : isShown ? "dropped" : "dropped, its text left the screen";
            logger.LogInformation(
                "Short link asked ({Outcome}, {Hops} hops, {Milliseconds} ms, error {ErrorType}, {Use})",
                result.Outcome,
                result.Hops,
                result.Milliseconds,
                error,
                use);
        }

        if (!isUsed)
        {
            return null;
        }

        return result.Destination ?? new TextLinkDestination(TextLinkDestinationKind.Browser, null, shortLink);
    }

    private static void ShowFailure()
        => GlobalDependencies.Services.GetRequiredService<AppViewModel>().ShowTipCommand
            .Execute((ResourceToolkit.GetLocalizedString(StringNames.TextLinkOpenFailed), InfoType.Error));
}
