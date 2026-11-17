// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Richasy.BiliKernel.Models.Authorization;

/// <summary>The outcome of a password, SMS, or secondary-verification login operation.</summary>
public enum BiliLoginStatus
{
    /// <summary>Cookies and a usable App token have been imported through the existing save flow.</summary>
    Success,

    /// <summary>The operation failed. Inspect <see cref="BiliLoginResult.Failure"/>.</summary>
    Failed,

    /// <summary>Continue with the opaque <see cref="BiliLoginResult.VerificationContext"/>.</summary>
    RequiresVerification,
}

/// <summary>A safe error category for localized UI messages. No server text is retained.</summary>
public enum BiliLoginErrorKind
{
    /// <summary>An unrecognized business error.</summary>
    Unknown,

    /// <summary>The account or password is incorrect.</summary>
    InvalidCredentials,

    /// <summary>The human-verification proof is invalid or has expired.</summary>
    CaptchaFailed,

    /// <summary>The server rejected the request frequency or SMS quota.</summary>
    RateLimited,

    /// <summary>The operation or submitted login token timed out.</summary>
    TimedOut,

    /// <summary>Required input is missing or rejected.</summary>
    InvalidParameters,

    /// <summary>The phone number has an invalid format.</summary>
    InvalidPhoneNumber,

    /// <summary>An SMS was already sent; do not immediately send another.</summary>
    SmsAlreadySent,

    /// <summary>The destination phone number is blocked.</summary>
    PhoneNumberBlocked,

    /// <summary>The SMS code is incorrect.</summary>
    InvalidSmsCode,

    /// <summary>The SMS code or SMS captcha key has expired.</summary>
    SmsCodeExpired,

    /// <summary>The session was disposed, completed, or belongs to another service instance.</summary>
    SessionExpired,

    /// <summary>The server requested an unsupported security-center variant; use web login.</summary>
    UnsupportedVerification,

    /// <summary>The captcha provider is unsupported by the in-app challenge window; use web login.</summary>
    UnsupportedCaptcha,

    /// <summary>A response lacks the required fields or authenticated cookies; use web login.</summary>
    InvalidResponse,

    /// <summary>The existing Cookie-to-App-token import could not complete; use web login.</summary>
    SessionImportFailed,
}

/// <summary>A business failure containing only safe classifications and numeric diagnostics.</summary>
public sealed class BiliLoginFailure
{
    private readonly bool _requiresWebLogin;

    /// <summary>Initializes a new instance of the <see cref="BiliLoginFailure"/> class.</summary>
    public BiliLoginFailure(BiliLoginErrorKind kind, int? code = null, int? httpStatusCode = null, bool requiresWebLogin = false)
    {
        Kind = kind;
        Code = code;
        HttpStatusCode = httpStatusCode;
        _requiresWebLogin = requiresWebLogin;
    }

    /// <summary>Gets the category for the UI.</summary>
    public BiliLoginErrorKind Kind { get; }

    /// <summary>Gets the unmodified API code, or null for a local/transport/protocol failure.</summary>
    public int? Code { get; }

    /// <summary>Gets the HTTP status when the failure came from a non-success HTTP response.</summary>
    public int? HttpStatusCode { get; }

    /// <summary>Gets whether this failure requires switching to the existing full web-login fallback.</summary>
    public bool RequiresWebLogin => _requiresWebLogin || Kind is BiliLoginErrorKind.UnsupportedVerification
        or BiliLoginErrorKind.UnsupportedCaptcha
        or BiliLoginErrorKind.InvalidResponse
        or BiliLoginErrorKind.SessionImportFailed;

    /// <inheritdoc/>
    public override string ToString() => $"BiliLoginFailure ({Kind}, code={Code}, http={HttpStatusCode})";
}

/// <summary>A completed login, a safe failure, or an opaque secondary-verification continuation.</summary>
public sealed class BiliLoginResult
{
    private BiliLoginResult(BiliLoginStatus status, int? code, int? serverStatus, BiliLoginFailure? failure, BiliLoginVerificationContext? verificationContext)
    {
        Status = status;
        Code = code;
        ServerStatus = serverStatus;
        Failure = failure;
        VerificationContext = verificationContext;
    }

    /// <summary>Gets the result discriminator.</summary>
    public BiliLoginStatus Status { get; }

    /// <summary>Gets the raw API code, including zero or -2100 for verification continuations.</summary>
    public int? Code { get; }

    /// <summary>Gets the raw data.status when present.</summary>
    public int? ServerStatus { get; }

    /// <summary>Gets the failure only when <see cref="Status"/> is <see cref="BiliLoginStatus.Failed"/>.</summary>
    public BiliLoginFailure? Failure { get; }

    /// <summary>Gets the opaque continuation only when secondary verification is required.</summary>
    [JsonIgnore]
    public BiliLoginVerificationContext? VerificationContext { get; }

    /// <summary>Creates a successful result after both Cookie and App-token import have completed.</summary>
    public static BiliLoginResult Succeeded(int code = 0, int? serverStatus = null)
        => new(BiliLoginStatus.Success, code, serverStatus, null, null);

    /// <summary>Creates a business failure.</summary>
    public static BiliLoginResult Failed(BiliLoginFailure failure, int? serverStatus = null)
        => new(BiliLoginStatus.Failed, failure.Code, serverStatus, failure, null);

    /// <summary>Creates an opaque secondary-verification continuation.</summary>
    public static BiliLoginResult NeedsVerification(BiliLoginVerificationContext context, int code, int? serverStatus = null)
        => new(BiliLoginStatus.RequiresVerification, code, serverStatus, null, context);

    /// <inheritdoc/>
    public override string ToString() => $"BiliLoginResult ({Status}, code={Code}, status={ServerStatus})";
}
