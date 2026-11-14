// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using Richasy.BiliKernel.Models.Base;
using Richasy.BiliKernel.Models.Search;

namespace BiliCopilot.UI.Controls.Player;

/// <summary>
/// 视频描述组件.
/// </summary>
public sealed partial class VideoDescriptorControl : VideoPlayerControlBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="VideoDescriptorControl"/> class.
    /// </summary>
    public VideoDescriptorControl() => InitializeComponent();

    private void OnTagButtonClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BiliTag { Name: { Length: > 0 } name })
        {
            return;
        }

        // The command takes a suggestion item; a bare keyword makes it throw.
        var suggestion = new SearchSuggestItemViewModel(new SearchSuggestItem(name, name));
        this.Get<SearchBoxViewModel>().SearchCommand.Execute(suggestion);
    }
}
