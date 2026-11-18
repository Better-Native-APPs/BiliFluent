// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.ComponentModel;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Items;

public sealed partial class ChapterItemViewModel(int index, string name, double position, double end = 0, string? imageUrl = null, int type = 0) : ViewModelBase
{
    public int Index { get; set; } = index;

    [ObservableProperty]
    public partial string? Title { get; set; } = string.IsNullOrEmpty(name) ? string.Format(ResourceToolkit.GetLocalizedString(StringNames.ChapterIndexTemplate), index + 1) : name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Start))]
    public partial double Position { get; set; } = position;

    public double Start => Position;

    [ObservableProperty]
    public partial double End { get; set; } = end;

    [ObservableProperty]
    public partial string? ImageUrl { get; set; } = imageUrl;

    public int Type { get; } = type;

    [ObservableProperty]
    public partial bool IsPlayed { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }
}
