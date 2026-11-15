// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Components;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Richasy.BiliKernel.Models.Media;
using Richasy.WinUIKernel.Share.ViewModels;
using System.Globalization;

namespace BiliCopilot.UI.ViewModels.Items;

public sealed partial class SourceItemViewModel : ViewModelBase<PlayerFormatInformation>
{
    private readonly Func<SourceItemViewModel, Task> _activeFunc;

    public SourceItemViewModel(PlayerFormatInformation info, Func<SourceItemViewModel, Task> activeFunc)
        : base(info)
    {
        Name = info.Description;
        Id = info.Quality.ToString();
        _activeFunc = activeFunc;
        var userIsVip = this.Get<AccountViewModel>().MyProfile.IsVip ?? false;
        IsEnabled = !info.NeedVip || (info.NeedVip && userIsVip);
    }

    private SourceItemViewModel(Func<SourceItemViewModel, Task> activeFunc)
        : base(new PlayerFormatInformation(0, string.Empty, false, false))
    {
        Name = ResourceToolkit.GetLocalizedString(StringNames.QualityAuto);
        Id = "auto";
        _activeFunc = activeFunc;
        IsAuto = true;
        IsEnabled = true;
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    public string Id { get; set; }

    /// <summary>
    /// Whether this is the "automatic" entry at the top of the list. It stands for no quality of its own
    /// (its quality number is 0); its name tells which quality plays.
    /// </summary>
    public bool IsAuto { get; }

    /// <summary>
    /// The name of the audio quality the automatic entry picked, when that is Dolby or Hi-Res.
    /// It is part of the entry's name then. Null for the standard quality.
    /// </summary>
    public string? AutoAudioName { get; set; }

    /// <summary>
    /// Creates the "automatic" entry of a quality list.
    /// </summary>
    public static SourceItemViewModel CreateAuto(Func<SourceItemViewModel, Task> activeFunc)
        => new(activeFunc);

    /// <summary>
    /// Names the automatic entry after what plays, such as "Auto (1080P)", or "Auto (1080P · Hi-Res lossless)"
    /// when <see cref="AutoAudioName"/> is set.
    /// </summary>
    public void ShowAutoQuality(PlayerFormatInformation? playing)
    {
        if (!IsAuto)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(playing?.Description))
        {
            Name = ResourceToolkit.GetLocalizedString(StringNames.QualityAuto);
        }
        else if (string.IsNullOrWhiteSpace(AutoAudioName))
        {
            Name = string.Format(CultureInfo.CurrentCulture, ResourceToolkit.GetLocalizedString(StringNames.QualityAutoTemplate), playing.Description);
        }
        else
        {
            Name = string.Format(CultureInfo.CurrentCulture, ResourceToolkit.GetLocalizedString(StringNames.QualityAutoWithAudioTemplate), playing.Description, AutoAudioName);
        }
    }

    public override bool Equals(object? obj) => obj is SourceItemViewModel model && Id == model.Id;
    public override int GetHashCode() => HashCode.Combine(Id);

    [RelayCommand]
    private Task ActiveAsync()
        => _activeFunc.Invoke(this);
}
