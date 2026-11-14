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
/// One entry of the audio track list: a standard quality, Dolby, Hi-Res lossless, or an AI dubbing language.
/// </summary>
public sealed partial class AudioTrackItemViewModel : ViewModelBase<AudioTrackInformation>
{
    private readonly Func<AudioTrackItemViewModel, Task> _activeFunc;

    public AudioTrackItemViewModel(AudioTrackInformation track, Func<AudioTrackItemViewModel, Task> activeFunc)
        : base(track)
    {
        _activeFunc = activeFunc;
        Name = GetDisplayName(track);

        // A dubbing language gets its address only after it is requested, so it stays selectable without one.
        IsEnabled = track.IsAI || !string.IsNullOrEmpty(track.Segment?.BaseUrl);
    }

    /// <summary>
    /// The stable track ID, such as "std:30280", "dolby:30250", "hires:30251" or "ai:en".
    /// </summary>
    public string Id => Data.Id;

    public string Name { get; }

    /// <summary>
    /// Whether the track is an AI dubbing. The list marks it with a tag, to tell it apart from the original audio.
    /// </summary>
    public bool IsAI => Data.IsAI;

    /// <summary>
    /// Whether the track needs a premium membership. The list shows a quiet note for it.
    /// </summary>
    public bool NeedVip => Data.NeedVip;

    /// <summary>
    /// False for a track the current account has no address for. It is listed, but cannot be chosen.
    /// </summary>
    public bool IsEnabled { get; }

    /// <summary>
    /// The name a screen reader announces: the track name, plus the AI and membership marks when they apply.
    /// </summary>
    public string AccessibleName
    {
        get
        {
            var name = Name;
            if (IsAI)
            {
                name = string.Format(CultureInfo.CurrentCulture, ResourceToolkit.GetLocalizedString(StringNames.AudioTrackAITemplate), name);
            }

            return NeedVip
                ? string.Format(CultureInfo.CurrentCulture, ResourceToolkit.GetLocalizedString(StringNames.AudioTrackVipTemplate), name)
                : name;
        }
    }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public override bool Equals(object? obj) => obj is AudioTrackItemViewModel model && Id == model.Id;

    public override int GetHashCode() => HashCode.Combine(Id);

    private static string GetDisplayName(AudioTrackInformation track)
        => track.Kind switch
        {
            // 30250 is the API's Dolby Atmos stream. Any other Dolby stream is only called Dolby.
            AudioTrackKind.Dolby => ResourceToolkit.GetLocalizedString(GetStreamId(track) == "30250" ? StringNames.AudioTrackDolbyAtmos : StringNames.AudioTrackDolby),
            AudioTrackKind.HiRes => ResourceToolkit.GetLocalizedString(StringNames.AudioTrackHiRes),
            AudioTrackKind.AiDubbing => GetLanguageName(track),
            _ => GetBitrateName(track),
        };

    // The part of the track ID after the kind prefix, which is the stream ID the API uses.
    private static string GetStreamId(AudioTrackInformation track)
    {
        var index = track.Id.IndexOf(':', StringComparison.Ordinal);
        return index >= 0 ? track.Id[(index + 1)..] : track.Id;
    }

    private static string GetBitrateName(AudioTrackInformation track)
    {
        // The API's well-known standard streams. Their real bandwidth varies from video to video.
        switch (GetStreamId(track))
        {
            case "30216":
                return "64K";
            case "30232":
                return "132K";
            case "30280":
                return "192K";
        }

        var bandwidth = track.Segment?.Bandwidth ?? 0;
        return bandwidth > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(bandwidth / 1000d)}K")
            : ResourceToolkit.GetLocalizedString(StringNames.AudioTrackStandard);
    }

    private static string GetLanguageName(AudioTrackInformation track)
    {
        if (!string.IsNullOrWhiteSpace(track.Name))
        {
            return track.Name;
        }

        var code = track.LanguageCode;
        if (string.IsNullOrWhiteSpace(code))
        {
            return GetStreamId(track);
        }

        try
        {
            // A code the system does not know comes back as "Unknown language (xx)"; the code alone reads better.
            var name = CultureInfo.GetCultureInfo(code).DisplayName;
            return string.IsNullOrWhiteSpace(name) || name.Contains(code, StringComparison.OrdinalIgnoreCase) ? code : name;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    [RelayCommand(CanExecute = nameof(IsEnabled))]
    private Task ActiveAsync()
        => _activeFunc.Invoke(this);
}
