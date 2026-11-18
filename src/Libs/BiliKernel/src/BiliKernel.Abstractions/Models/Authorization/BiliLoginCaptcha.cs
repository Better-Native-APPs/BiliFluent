// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Richasy.BiliKernel.Models.Authorization;

/// <summary>In-memory captcha parameters. Pass only to the dedicated captcha window.</summary>
[DebuggerDisplay("BiliLoginCaptchaParameters [redacted]")]
public sealed class BiliLoginCaptchaParameters
{
    /// <summary>Initializes a new instance of the <see cref="BiliLoginCaptchaParameters"/> class.</summary>
    public BiliLoginCaptchaParameters(string type, string token, string gt, string challenge)
        : this(type, token, gt, challenge, string.Empty)
    {
    }

    /// <summary>Initializes captcha parameters with an in-memory image data URI.</summary>
    public BiliLoginCaptchaParameters(string type, string token, string gt, string challenge, string imageData)
    {
        Type = type;
        Token = token;
        Gt = gt;
        Challenge = challenge;
        ImageData = imageData;
    }

    /// <summary>Gets the captcha provider/type reported by the server.</summary>
    public string Type { get; }

    /// <summary>Gets the Passport token, or security-center recaptcha token. Never persist or log.</summary>
    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Token { get; }

    /// <summary>Gets the Geetest V3 site identifier or V4 captcha identifier.</summary>
    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Gt { get; }

    /// <summary>Gets the challenge for the captcha SDK. Never persist or log.</summary>
    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Challenge { get; }

    /// <summary>Gets an in-memory image data URI, without exposing the Passport token to the page.</summary>
    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string ImageData { get; }

    /// <inheritdoc/>
    public override string ToString() => "BiliLoginCaptchaParameters [redacted]";
}

/// <summary>The user's captcha proof. This container never expands its values in ToString.</summary>
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

    /// <summary>Creates the four-field proof returned by the official Geetest V4 SDK.</summary>
    public static BiliLoginCaptchaResult FromGeetest4(string token, string lotNumber, string captchaOutput, string passToken, string genTime)
        => new(token, string.Empty, string.Empty, string.Empty)
        {
            Type = "geetest4",
            LotNumber = lotNumber,
            CaptchaOutput = captchaOutput,
            PassToken = passToken,
            GenTime = genTime,
        };

    /// <summary>Creates the user's five-character image-captcha answer.</summary>
    public static BiliLoginCaptchaResult FromImage(string token, string imageCode)
        => new(token, string.Empty, string.Empty, string.Empty) { Type = "img", ImageCode = imageCode };

    /// <summary>Gets the proof provider, matching the issued parameters.</summary>
    public string Type { get; private set; } = "geetest";

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

    /// <summary>Gets the Geetest V4 lot number. Never persist or log.</summary>
    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string LotNumber { get; private set; } = string.Empty;

    /// <summary>Gets the Geetest V4 captcha output. Never persist or log.</summary>
    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string CaptchaOutput { get; private set; } = string.Empty;

    /// <summary>Gets the Geetest V4 pass token. Never persist or log.</summary>
    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string PassToken { get; private set; } = string.Empty;

    /// <summary>Gets the Geetest V4 generation time. Never persist or log.</summary>
    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string GenTime { get; private set; } = string.Empty;

    /// <summary>Gets the image-captcha answer. Never persist or log.</summary>
    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string ImageCode { get; private set; } = string.Empty;

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
