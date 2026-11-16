// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Authorization;

/// <summary>A country/region accepted by Web Passport SMS login.</summary>
public sealed class BiliLoginCountry
{
    /// <summary>Initializes a new instance of the <see cref="BiliLoginCountry"/> class.</summary>
    public BiliLoginCountry(int id, string name, int dialingCode)
    {
        Id = id;
        Name = name;
        DialingCode = dialingCode;
    }

    /// <summary>Gets the country-list item id sent as SMS cid.</summary>
    public int Id { get; }

    /// <summary>Gets the display name reported by the server.</summary>
    public string Name { get; }

    /// <summary>Gets country_id, the international dialing code displayed beside the phone input.</summary>
    public int DialingCode { get; }
}

/// <summary>The common and other country lists, or a safe business/protocol failure.</summary>
public sealed class BiliLoginCountriesResponse
{
    private BiliLoginCountriesResponse(IReadOnlyList<BiliLoginCountry> common, IReadOnlyList<BiliLoginCountry> others, BiliLoginFailure? failure)
    {
        Common = common;
        Others = others;
        Failure = failure;
    }

    /// <summary>Gets whether both country lists were parsed successfully.</summary>
    public bool IsSuccess => Failure is null;

    /// <summary>Gets the common regions, empty on failure.</summary>
    public IReadOnlyList<BiliLoginCountry> Common { get; }

    /// <summary>Gets the remaining regions, empty on failure.</summary>
    public IReadOnlyList<BiliLoginCountry> Others { get; }

    /// <summary>Gets the error, if any.</summary>
    public BiliLoginFailure? Failure { get; }

    /// <summary>Creates a successful list result.</summary>
    public static BiliLoginCountriesResponse Succeeded(IEnumerable<BiliLoginCountry> common, IEnumerable<BiliLoginCountry> others)
        => new(common.ToList().AsReadOnly(), others.ToList().AsReadOnly(), null);

    /// <summary>Creates a failure result.</summary>
    public static BiliLoginCountriesResponse Failed(BiliLoginFailure failure)
        => new(Array.Empty<BiliLoginCountry>(), Array.Empty<BiliLoginCountry>(), failure);
}
