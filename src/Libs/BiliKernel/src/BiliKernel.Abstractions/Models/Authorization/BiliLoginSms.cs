// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Richasy.BiliKernel.Models.Authorization;

/// <summary>The result of sending a login or secondary-verification SMS.</summary>
public sealed class BiliLoginSmsResponse
{
    private BiliLoginSmsResponse(BiliLoginSmsContext? context, BiliLoginFailure? failure)
    {
        Context = context;
        Failure = failure;
    }

    /// <summary>Gets whether an SMS context is available.</summary>
    public bool IsSuccess => Context is not null;

    /// <summary>Gets the opaque captcha key to pass unchanged to the matching SMS submission method.</summary>
    [JsonIgnore]
    public BiliLoginSmsContext? Context { get; }

    /// <summary>Gets the error when sending did not succeed.</summary>
    public BiliLoginFailure? Failure { get; }

    /// <summary>Creates a successful send result.</summary>
    public static BiliLoginSmsResponse Succeeded(BiliLoginSmsContext context) => new(context, null);

    /// <summary>Creates a failure result.</summary>
    public static BiliLoginSmsResponse Failed(BiliLoginFailure failure) => new(null, failure);

    /// <inheritdoc/>
    public override string ToString() => $"BiliLoginSmsResponse (success={IsSuccess})";
}
