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
/// One entry of the audio language list: the original audio, or a dubbing language the API offers.
/// </summary>
public sealed partial class AudioLanguageItemViewModel : ViewModelBase<AudioLanguageInformation>
{
    private readonly Func<AudioLanguageItemViewModel, Task> _activeFunc;

    public AudioLanguageItemViewModel(AudioLanguageInformation language, Func<AudioLanguageItemViewModel, Task> activeFunc)
        : base(language)
    {
        _activeFunc = activeFunc;

        // The original audio has no language name of its own. A dubbing language shows the API's title as it is.
        Name = language.IsOriginal
            ? ResourceToolkit.GetLocalizedString(StringNames.AudioTrackOriginal)
            : string.IsNullOrWhiteSpace(language.Name) ? language.Id : language.Name;
    }

    /// <summary>
    /// "original", or the language code the API uses, such as "en" or "ja".
    /// </summary>
    public string Id => Data.Id;

    public string Name { get; }

    public bool IsOriginal => Data.IsOriginal;

    /// <summary>
    /// Whether this is AI dubbing. The list marks it with a tag.
    /// </summary>
    public bool IsAI => Data.IsAI;

    /// <summary>
    /// Whether the language needs a premium membership. The list shows a quiet note for it.
    /// </summary>
    public bool NeedVip => Data.NeedVip;

    /// <summary>
    /// False for a language the current account cannot play. It is listed, but cannot be chosen.
    /// </summary>
    public bool IsEnabled => Data.IsEnabled;

    /// <summary>
    /// The name a screen reader announces: the language name, plus the AI and membership marks when they apply.
    /// </summary>
    public string AccessibleName
    {
        get
        {
            var name = IsAI
                ? string.Format(CultureInfo.CurrentCulture, ResourceToolkit.GetLocalizedString(StringNames.AudioTrackAITemplate), Name)
                : Name;
            return NeedVip
                ? string.Format(CultureInfo.CurrentCulture, ResourceToolkit.GetLocalizedString(StringNames.AudioTrackVipTemplate), name)
                : name;
        }
    }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public override bool Equals(object? obj) => obj is AudioLanguageItemViewModel model && Id == model.Id;

    public override int GetHashCode() => HashCode.Combine(Id);

    [RelayCommand(CanExecute = nameof(IsEnabled))]
    private Task ActiveAsync()
        => _activeFunc.Invoke(this);
}
