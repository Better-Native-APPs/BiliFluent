// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace Richasy.BiliKernel.Authorizers.TV.Core;

internal static class WebLoginLog
{
    private static readonly Action<ILogger, string, string, Exception?> CaptchaParameters = LoggerMessage.Define<string, string>(
        LogLevel.Information, new EventId(1, "SignInCaptchaParameters"), "SignIn | {Step}: {Parameters}.");
    private static readonly Action<ILogger, string, string, string?, int?, Exception?> OperationResult = LoggerMessage.Define<string, string, string?, int?>(
        LogLevel.Information, new EventId(2, "SignInOperationResult"), "SignIn | {Step}: result={Result}, error={ErrorKind}, code={Code}.");

    internal static void Captcha(ILogger logger, bool securityCenter, WebCaptchaDiagnostics diagnostics)
    {
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        // These values are bounded provider names and flags, never captcha parameters or proofs.
        var parameters = $"recaptcha_type={diagnostics.ReportedRecaptchaType}, type={diagnostics.ReportedStandardType}, typeField={diagnostics.TypeField}, typeValue={diagnostics.TypeValue}, typeShape={diagnostics.TypeShape}, tokenPresent={diagnostics.TokenPresent}, gtPresent={diagnostics.GtPresent}, challengePresent={diagnostics.ChallengePresent}, parametersComplete={diagnostics.ParametersComplete}, branch={diagnostics.Branch}";
        CaptchaParameters(logger, securityCenter ? "VerificationCaptchaParameters" : "LoginCaptchaParameters", parameters, null);
    }

    internal static void Result(ILogger logger, string step, string outcome, string? errorKind, int? code)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            OperationResult(logger, step, outcome, errorKind, code, null);
        }
    }
}
