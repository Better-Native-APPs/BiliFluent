// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Richasy.BiliKernel.Models.Authorization;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// A country or region in the SMS sign-in list.
/// </summary>
public sealed class StartupCountryItem
{
    // The width the flag is shown at; the image is decoded for it at the scale of the display.
    private const int FlagWidth = 20;

    private readonly Uri? _flagUri;
    private ImageSource? _flag;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartupCountryItem"/> class.
    /// </summary>
    public StartupCountryItem(BiliLoginCountry country)
    {
        Country = country;
        Name = country.Name;
        DialingDigits = country.DialingCode.ToString(CultureInfo.InvariantCulture);
        DialingCode = "+" + DialingDigits;
        _flagUri = StartupCountryFlags.Find(country);
    }

    /// <summary>
    /// The entry as issued by the login service; it is passed back unchanged.
    /// </summary>
    public BiliLoginCountry Country { get; }

    /// <summary>
    /// Display name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// International dialing code with a leading plus sign.
    /// </summary>
    public string DialingCode { get; }

    /// <summary>
    /// The digits of the dialing code, which is what typing on the list looks for.
    /// </summary>
    public string DialingDigits { get; }

    /// <summary>
    /// Whether this entry has a flag. One that has none is shown with a globe.
    /// </summary>
    public bool HasFlag => _flagUri is not null;

    /// <summary>
    /// Whether this entry is shown with a globe.
    /// </summary>
    public bool IsFlagMissing => _flagUri is null;

    /// <summary>
    /// The flag, loaded when it is first shown.
    /// </summary>
    public ImageSource? Flag
        => _flag ??= _flagUri is null
            ? null
            : new BitmapImage(_flagUri) { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = FlagWidth };

    /// <summary>
    /// What a screen reader says for this entry, and for the box while the entry is selected.
    /// </summary>
    public override string ToString() => $"{Name} {DialingCode}";
}
