// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Richasy.BiliKernel.Models.Authorization;

/// <summary>In-memory Geetest parameters. Pass only to the dedicated captcha window.</summary>
[DebuggerDisplay("BiliLoginCaptchaParameters [redacted]")]
public sealed class BiliLoginCaptchaParameters
{
    /// <summary>Initializes a new instance of the <see cref="BiliLoginCaptchaParameters"/> class.</summary>
    public BiliLoginCaptchaParameters(string type, string token, string gt, string challenge)
    {
        Type = type;
        Token = token;
        Gt = gt;
        Challenge = challenge;
    }

    /// <summary>Gets the captcha provider/type reported by the server.</summary>
    public string Type { get; }

    /// <summary>Gets the Passport token, or security-center recaptcha token. Never persist or log.</summary>
    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Token { get; }

    /// <summary>Gets the Geetest site identifier.</summary>
    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Gt { get; }

    /// <summary>Gets the challenge for the captcha SDK. Never persist or log.</summary>
    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Challenge { get; }

    /// <inheritdoc/>
    public override string ToString() => "BiliLoginCaptchaParameters [redacted]";
}

/// <summary>The user's Geetest V3 proof. This container is not a record and never expands in ToString.</summary>
[DebuggerDisplay("BiliLoginCaptchaResult [redacted]")]
public sealed class BiliLoginCaptchaResult
{
    /// <summary>Initializes a new instance of the <see cref="BiliLoginCaptchaResult"/> class.</summary>
    /// <remarks>Use Token from the parameters and the challenge/validate/seccode returned by Geetest.</remarks>
    public BiliLoginCaptchaResult(string token, string challenge, string validate, string seccode)
    {
        Token = token;
        Challenge = challenge;
        Validate = validate;
        Seccode = seccode;
    }

    /// <summary>Gets the original captcha token. Never persist or log.</summary>
    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Token { get; }

    /// <summary>Gets the solved challenge. Never persist or log.</summary>
    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Challenge { get; }

    /// <summary>Gets the Geetest validation proof. Never persist or log.</summary>
    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Validate { get; }

    /// <summary>Gets the Geetest seccode. Never persist or log.</summary>
    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Seccode { get; }

    /// <inheritdoc/>
    public override string ToString() => "BiliLoginCaptchaResult [redacted]";
}

/// <summary>A supported captcha or a safe failure, including unsupported provider responses.</summary>
public sealed class BiliLoginCaptchaResponse
{
    private BiliLoginCaptchaResponse(BiliLoginCaptchaParameters? parameters, BiliLoginFailure? failure)
    {
        Parameters = parameters;
        Failure = failure;
    }

    /// <summary>Gets whether <see cref="Parameters"/> is available.</summary>
    public bool IsSuccess => Parameters is not null;

    /// <summary>Gets the in-memory parameters on success.</summary>
    [JsonIgnore]
    public BiliLoginCaptchaParameters? Parameters { get; }

    /// <summary>Gets the error on failure.</summary>
    public BiliLoginFailure? Failure { get; }

    /// <summary>Creates a supported captcha result.</summary>
    public static BiliLoginCaptchaResponse Succeeded(BiliLoginCaptchaParameters parameters) => new(parameters, null);

    /// <summary>Creates a failure result.</summary>
    public static BiliLoginCaptchaResponse Failed(BiliLoginFailure failure) => new(null, failure);

    /// <inheritdoc/>
    public override string ToString() => $"BiliLoginCaptchaResponse (success={IsSuccess})";
}
