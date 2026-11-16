// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using Richasy.BiliKernel.Models.Authorization;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// A country or region in the SMS sign-in list.
/// </summary>
public sealed class StartupCountryItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StartupCountryItem"/> class.
    /// </summary>
    public StartupCountryItem(BiliLoginCountry country)
    {
        Country = country;
        Name = country.Name;
        DialingCode = "+" + country.DialingCode.ToString(CultureInfo.InvariantCulture);
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

    /// <inheritdoc/>
    public override string ToString() => $"{Name} {DialingCode}";
}
