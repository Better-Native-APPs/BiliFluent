// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Authorizers.TV.Core;

internal sealed class WebCaptchaDiagnostics
{
    internal string TypeField { get; set; } = "type";

    internal string TypeValue { get; set; } = "missing";

    internal string TypeShape { get; set; } = "Undefined";

    internal string ReportedRecaptchaType { get; set; } = "missing";

    internal string ReportedStandardType { get; set; } = "missing";

    internal bool TokenPresent { get; set; }

    internal bool GtPresent { get; set; }

    internal bool ChallengePresent { get; set; }

    internal bool ParametersComplete { get; set; }

    internal string Branch { get; set; } = "WebLogin";
}
