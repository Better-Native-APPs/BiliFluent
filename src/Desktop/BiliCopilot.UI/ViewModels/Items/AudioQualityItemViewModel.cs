// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Richasy.BiliKernel.Models.Media;
using Richasy.WinUIKernel.Share.ViewModels;
using System.Globalization;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// One entry of the audio quality list: the standard quality, Dolby, or Hi-Res lossless.
/// </summary>
public sealed partial class AudioQualityItemViewModel : ViewModelBase<AudioQualityInformation>
{
    private readonly Func<AudioQualityItemViewModel, Task> _activeFunc;

    public AudioQualityItemViewModel(AudioQualityInformation quality, Func<AudioQualityItemViewModel, Task> activeFunc)
        : base(quality)
    {
        _activeFunc = activeFunc;
        Name = GetDisplayName(quality);
    }

    /// <summary>
    /// The stable quality ID, such as "std:30280", "dolby:30250" or "hires:30251".
    /// </summary>
    public string Id => Data.Id;

    public string Name { get; }

    /// <summary>
    /// Whether this is the entry for the standard quality. It stands for every standard bitrate of the media;
    /// the highest one that can play is the one that plays.
    /// </summary>
    public bool IsStandard => Data.Kind == AudioQualityKind.Standard;

    /// <summary>
    /// Whether the quality needs a premium membership. The list shows a quiet note for it.
    /// </summary>
    public bool NeedVip => Data.NeedVip;

    /// <summary>
    /// False for a quality the current account has no address for. It is listed, but cannot be chosen.
    /// </summary>
    public bool IsEnabled => Data.IsEnabled;

    /// <summary>
    /// The name a screen reader announces: the quality name, plus the membership mark when it applies.
    /// </summary>
    public string AccessibleName
        => NeedVip
            ? string.Format(CultureInfo.CurrentCulture, ResourceToolkit.GetLocalizedString(StringNames.AudioTrackVipTemplate), Name)
            : Name;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public override bool Equals(object? obj) => obj is AudioQualityItemViewModel model && Id == model.Id;

    public override int GetHashCode() => HashCode.Combine(Id);

    private static string GetDisplayName(AudioQualityInformation quality)
        => quality.Kind switch
        {
            // 30250 is the API's Dolby Atmos stream. Any other Dolby stream is only called Dolby.
            AudioQualityKind.Dolby => ResourceToolkit.GetLocalizedString(GetStreamId(quality) == "30250" ? StringNames.AudioTrackDolbyAtmos : StringNames.AudioTrackDolby),
            AudioQualityKind.HiRes => ResourceToolkit.GetLocalizedString(StringNames.AudioTrackHiRes),
            _ => ResourceToolkit.GetLocalizedString(StringNames.AudioTrackStandard),
        };

    // The part of the quality ID after the kind prefix, which is the stream ID the API uses.
    private static string GetStreamId(AudioQualityInformation quality)
    {
        var index = quality.Id.IndexOf(':', StringComparison.Ordinal);
        return index >= 0 ? quality.Id[(index + 1)..] : quality.Id;
    }

    [RelayCommand(CanExecute = nameof(IsEnabled))]
    private Task ActiveAsync()
        => _activeFunc.Invoke(this);
}
