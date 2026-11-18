// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Pages.Overlay;
using BiliCopilot.UI.ViewModels.Core;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Models.Article;
using Richasy.BiliKernel.Models.Media;
using Richasy.BiliKernel.Models.User;
using Richasy.WinUIKernel.Share.Base;
using Richasy.WinUIKernel.Share.Toolkits;
using Richasy.WinUIKernel.Share.ViewModels;
using System.Globalization;
using Windows.System;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>Opens notification actions through the existing content routes or the default browser.</summary>
public sealed partial class ChatNotificationLinkViewModel : ViewModelBase<ChatNotificationLink>
{
    private readonly NotifyMessageTarget _target;
    private readonly string? _liveId;
    private readonly string? _seasonId;

    public ChatNotificationLinkViewModel(ChatNotificationLink data)
        : base(data)
    {
        Text = string.IsNullOrWhiteSpace(data.Text) ? this.Get<IResourceToolkit>().GetLocalizedString("NotificationViewDetails") : data.Text;
        _target = NotifyMessageTarget.FromUri(data.Url);
        var value = data.Url.Trim();
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var web = uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        if ((web && string.Equals(uri.Host, "live.bilibili.com", StringComparison.OrdinalIgnoreCase))
            || (uri.Scheme == "bilibili" && uri.Host == "live"))
        {
            _liveId = NumberId(segments.FirstOrDefault());
        }
        else if (web && (uri.Host == "www.bilibili.com" || uri.Host == "bilibili.com")
            && segments is ["bangumi", "play", var item] && item.StartsWith("ss", StringComparison.OrdinalIgnoreCase))
        {
            _seasonId = NumberId(item[2..]);
        }
        else if (uri.Scheme == "bilibili" && uri.Host == "bangumi" && segments is ["season", var seasonId])
        {
            _seasonId = NumberId(seasonId);
        }
    }

    public string Text { get; }
    public bool CanOpen => _target.WebUri is not null || _liveId is not null || _seasonId is not null;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        if (!CanOpen) return;
        try
        {
            if (_target.Kind == NotifyMessageTargetKind.Video && _target.Id is string videoId)
            {
                var video = new VideoInformation(new MediaIdentifier(videoId, default, default), default);
                new VideoItemViewModel(video, VideoCardStyle.Search).PlayCommand.Execute(default);
            }
            else if (_target.Kind == NotifyMessageTargetKind.Article && _target.Id is string articleId)
            {
                this.Get<NavigationViewModel>().NavigateToOver(typeof(ArticleReaderPage), new ArticleIdentifier(articleId, Text, default, default));
            }
            else if (_liveId is not null)
            {
                await this.Get<AppViewModel>().OpenLiveAsync(new LiveInformation(new MediaIdentifier(_liveId, default, default)));
            }
            else if (_seasonId is not null)
            {
                var season = new SeasonInformation(new MediaIdentifier(_seasonId, default, default));
                this.Get<AppViewModel>().OpenPlayerCommand.Execute(new MediaSnapshot(season, default));
            }
            else if (_target.WebUri is Uri webUri && !await Launcher.LaunchUriAsync(webUri).AsTask())
            {
                ShowFailure();
            }
        }
        catch (Exception ex)
        {
            // Notification URLs and bodies can contain private data; log only the failure category.
            this.Get<ILogger<ChatNotificationLinkViewModel>>().LogError("打开消息链接失败 ({ErrorType})", ex.GetType().Name);
            ShowFailure();
        }
    }

    private void ShowFailure()
        => this.Get<AppViewModel>().ShowTipCommand.Execute((this.Get<IResourceToolkit>().GetLocalizedString("FailedToOpenMessageLink"), InfoType.Error));

    private static string? NumberId(string? value) => value is { Length: > 0 }
        && value.All(c => c is >= '0' and <= '9')
        && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? value : null;
}
