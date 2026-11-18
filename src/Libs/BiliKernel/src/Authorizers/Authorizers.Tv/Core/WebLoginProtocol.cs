// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

using Richasy.BiliKernel.Bili;
using Richasy.BiliKernel.Models.Authorization;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Richasy.BiliKernel.Authorizers.TV.Core;

internal static class WebLoginProtocol
{
    internal const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/116.0.0.0 Safari/537.36 Edg/116.0.1938.69";
    internal static readonly Uri PasswordReferer = new("https://passport.bilibili.com/login");
    internal static readonly Uri SmsReferer = PasswordReferer;
    internal static readonly Uri BrowserIdentityReferer = new("https://www.bilibili.com/");

    // Match the standalone Passport page, rather than the header widget or App protocol.
    internal const string CaptchaSource = "main-fe";
    internal const string LoginSource = "main_web";
    internal const string LoginGoUrl = "";
    internal const string VerificationWithRequestId = "loginTelCheck";
    internal const string VerificationWithoutRequestId = "secLogin";
    internal const string VerificationSmsType = "sms";
    private const string ExchangeGoUrlBase = "https://passport.bilibili.com/pc/passport/risk/secTip";

    internal static int? GetInt(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var item))
        {
            return null;
        }

        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number))
        {
            return number;
        }

        return item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number : null;
    }

    internal static string GetString(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString() ?? string.Empty : string.Empty;

    internal static JsonElement GetData(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data : default;

    internal static BiliLoginFailure? ReadFailure(JsonElement root)
    {
        var code = GetInt(root, "code");
        return code is null ? new(BiliLoginErrorKind.InvalidResponse)
            : code == 0 ? null : new(MapError(code.Value), code);
    }

    internal static BiliLoginErrorKind MapError(int code)
        => code switch
        {
            -629 => BiliLoginErrorKind.InvalidCredentials,
            -105 or 2400 or 2406 => BiliLoginErrorKind.CaptchaFailed,
            -652 or -509 or 86203 => BiliLoginErrorKind.RateLimited,
            -662 => BiliLoginErrorKind.TimedOut,
            -400 or -653 or -2001 or 86000 => BiliLoginErrorKind.InvalidParameters,
            1002 => BiliLoginErrorKind.InvalidPhoneNumber,
            1003 => BiliLoginErrorKind.SmsAlreadySent,
            1025 => BiliLoginErrorKind.PhoneNumberBlocked,
            1006 => BiliLoginErrorKind.InvalidSmsCode,
            1007 => BiliLoginErrorKind.SmsCodeExpired,
            -2100 => BiliLoginErrorKind.UnsupportedVerification,
            _ => BiliLoginErrorKind.Unknown,
        };

    internal static BiliLoginFailure HttpFailure(int status, int? code)
    {
        var kind = status switch
        {
            429 => BiliLoginErrorKind.RateLimited,
            408 or 504 => BiliLoginErrorKind.TimedOut,
            400 => BiliLoginErrorKind.InvalidParameters,
            _ => code is not null && code != 0 ? MapError(code.Value) : BiliLoginErrorKind.Unknown,
        };
        return new(kind, code, status);
    }

    internal static BiliLoginCaptchaResponse ParseCaptcha(JsonElement root, bool securityCenter)
        => ParseCaptcha(root, securityCenter, out _);

    internal static BiliLoginCaptchaResponse ParseCaptcha(JsonElement root, bool securityCenter, out WebCaptchaDiagnostics diagnostics)
    {
        diagnostics = new WebCaptchaDiagnostics();
        var failure = ReadFailure(root);
        if (failure is not null)
        {
            return BiliLoginCaptchaResponse.Failed(failure);
        }

        var data = GetData(root);
        if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("recaptcha_type", out var reportedRecaptchaType))
            {
                diagnostics.ReportedRecaptchaType = ReadTypeName(reportedRecaptchaType);
            }

            if (data.TryGetProperty("type", out var reportedStandardType))
            {
                diagnostics.ReportedStandardType = ReadTypeName(reportedStandardType);
            }
        }
        // The official risk page only remaps the flat fields when recaptcha_type is truthy.
        // Otherwise the endpoint already has the standard type/token/geetest envelope.
        var flat = securityCenter && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("recaptcha_type", out var flatType) && IsTruthy(flatType);
        var typeField = flat ? "recaptcha_type" : "type";
        var type = GetString(data, typeField).Trim().ToLowerInvariant();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty(typeField, out var typeValue))
        {
            diagnostics.TypeValue = ReadTypeName(typeValue);
            diagnostics.TypeShape = typeValue.ValueKind.ToString();
        }
        diagnostics.TypeField = typeField;
        var token = GetString(data, flat ? "recaptcha_token" : "token");
        var geetest = flat ? data
            : data.ValueKind == JsonValueKind.Object && data.TryGetProperty("geetest", out var gee) ? gee : default;
        var gt = GetString(geetest, flat ? "gee_gt" : "gt");
        var challenge = GetString(geetest, flat ? "gee_challenge" : "challenge");
        diagnostics.TokenPresent = !string.IsNullOrWhiteSpace(token);
        diagnostics.GtPresent = !string.IsNullOrWhiteSpace(gt);
        diagnostics.ChallengePresent = !string.IsNullOrWhiteSpace(challenge);
        diagnostics.Branch = type switch
        {
            "geetest" => "GeetestV3",
            "geetest4" => "GeetestV4",
            "img" => "Image",
            _ => "WebLogin",
        };
        if (diagnostics.Branch == "WebLogin")
        {
            return BiliLoginCaptchaResponse.Failed(new(BiliLoginErrorKind.UnsupportedCaptcha, 0));
        }

        diagnostics.ParametersComplete = diagnostics.TokenPresent && (type switch
        {
            "geetest" => diagnostics.GtPresent && diagnostics.ChallengePresent,
            "geetest4" => diagnostics.GtPresent,
            _ => true,
        });
        return diagnostics.ParametersComplete ? BiliLoginCaptchaResponse.Succeeded(new(type, token, gt, challenge))
            : BiliLoginCaptchaResponse.Failed(new(BiliLoginErrorKind.InvalidResponse, 0));
    }

    private static bool IsTruthy(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => !string.IsNullOrEmpty(value.GetString()),
            JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.Number => !value.TryGetDouble(out var number) || number != 0,
            _ => true,
        };

    private static string ReadTypeName(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            var scalar = value.GetRawText();
            return scalar.Length <= 32 ? scalar : "invalid";
        }

        var name = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
        var normalized = name.Trim().ToLowerInvariant();
        if (normalized is "geetest" or "geetest4" or "img")
        {
            return normalized;
        }

        return name.Length == 0 ? "empty" : name.Length <= 32 && name.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')
            ? name : "invalid";
    }

    internal static BiliLoginCountriesResponse ParseCountries(JsonElement root)
    {
        var failure = ReadFailure(root);
        if (failure is not null)
        {
            return BiliLoginCountriesResponse.Failed(failure);
        }

        var data = GetData(root);
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("list", out _))
        {
            var defaultCountry = data.TryGetProperty("default", out var item) ? ReadCountry(item, "country_code") : null;
            if (defaultCountry is null || !TryReadCountries(data, "list", "country_code", out var countries))
            {
                return BiliLoginCountriesResponse.Failed(new(BiliLoginErrorKind.InvalidResponse, 0));
            }

            return BiliLoginCountriesResponse.Succeeded([defaultCountry], countries.Where(country => country.Id != defaultCountry.Id).ToList());
        }

        if (!TryReadCountries(data, "common", "country_id", out var common) || !TryReadCountries(data, "others", "country_id", out var others))
        {
            return BiliLoginCountriesResponse.Failed(new(BiliLoginErrorKind.InvalidResponse, 0));
        }

        return BiliLoginCountriesResponse.Succeeded(common, others);
    }

    internal static BiliLoginResult ParseLogin(JsonElement root, WebLoginSession session, bool smsLogin)
    {
        var code = GetInt(root, "code");
        var data = GetData(root);
        var status = GetInt(data, "status");
        if (code is null)
        {
            return BiliLoginResult.Failed(new(BiliLoginErrorKind.InvalidResponse), status);
        }

        var url = GetString(data, "url");
        if (TryParseQuery(url, out var query, out var referer)
            && query.TryGetValue("tmp_token", out var temporaryToken) && !string.IsNullOrWhiteSpace(temporaryToken))
        {
            if (IsNonSmsVerification(query, data))
            {
                return BiliLoginResult.Failed(new(BiliLoginErrorKind.UnsupportedVerification, code), status);
            }

            query.TryGetValue("request_id", out var requestId);
            query.TryGetValue("source", out var source);
            query.TryGetValue("gourl", out var goUrl);
            var context = new WebVerificationContext(session, temporaryToken, requestId ?? string.Empty, referer, source ?? string.Empty, goUrl ?? string.Empty);
            session.Verification?.Invalidate();
            session.Verification = context;
            session.Sms?.Invalidate();
            session.Sms = null;
            return BiliLoginResult.NeedsVerification(context, code.Value, status);
        }

        if (code == -2100 || (code == 0 && (smsLogin ? status == 5 : status is 1 or 2)))
        {
            return BiliLoginResult.Failed(new(BiliLoginErrorKind.UnsupportedVerification, code), status);
        }

        return code != 0 ? BiliLoginResult.Failed(new(MapError(code.Value), code), status)
            : BiliLoginResult.Succeeded(code.Value, status);
    }

    internal static BiliLoginFailure? ReadCaptchaKey(JsonElement root, out string key)
    {
        key = string.Empty;
        var failure = ReadFailure(root);
        if (failure is not null)
        {
            return failure;
        }

        key = GetString(GetData(root), "captcha_key");
        return string.IsNullOrWhiteSpace(key) ? new(BiliLoginErrorKind.InvalidResponse, 0) : null;
    }

    internal static BiliLoginFailure? ReadExchangeCode(JsonElement root, out string exchangeCode)
    {
        exchangeCode = string.Empty;
        var failure = ReadFailure(root);
        if (failure is not null)
        {
            return failure;
        }

        var data = GetData(root);
        exchangeCode = GetString(data, "code");
        if (string.IsNullOrEmpty(exchangeCode) && GetInt(data, "code") is int number)
        {
            exchangeCode = number.ToString(CultureInfo.InvariantCulture);
        }

        return string.IsNullOrWhiteSpace(exchangeCode) ? new(BiliLoginErrorKind.InvalidResponse, 0) : null;
    }

    internal static Dictionary<string, string> CaptchaFields(BiliLoginCaptchaResult captcha)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = captcha.Token };
        AddCaptchaProof(fields, captcha, securityCenter: false);
        return fields;
    }

    private static void AddCaptchaProof(Dictionary<string, string> fields, BiliLoginCaptchaResult captcha, bool securityCenter)
    {
        var prefix = securityCenter ? "gee_" : string.Empty;
        switch (captcha.Type)
        {
            case "geetest":
                fields[prefix + "challenge"] = captcha.Challenge;
                fields[prefix + "validate"] = captcha.Validate;
                fields[prefix + "seccode"] = captcha.Seccode;
                break;
            case "geetest4":
                fields[prefix + "lot_number"] = captcha.LotNumber;
                fields[prefix + "captcha_output"] = captcha.CaptchaOutput;
                fields[prefix + "pass_token"] = captcha.PassToken;
                fields[prefix + "gen_time"] = captcha.GenTime;
                break;
            case "img":
                fields[securityCenter ? "img_code" : "captcha"] = captcha.ImageCode;
                break;
        }
    }

    internal static Dictionary<string, string> VerificationSmsFields(WebVerificationContext context, BiliLoginCaptchaResult captcha)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sms_type"] = string.IsNullOrEmpty(context.RequestId) ? VerificationWithoutRequestId : VerificationWithRequestId,
            ["tmp_code"] = context.TemporaryToken,
            ["recaptcha_token"] = captcha.Token,
        };
        AddCaptchaProof(fields, captcha, securityCenter: true);
        return fields;
    }

    internal static Dictionary<string, string> VerificationSubmitFields(WebVerificationContext context, WebSmsContext sms, string smsCode)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["code"] = smsCode,
            ["tmp_code"] = context.TemporaryToken,
            ["captcha_key"] = sms.CaptchaKey,
        };
        if (string.IsNullOrEmpty(context.RequestId))
        {
            fields["verify_type"] = VerificationSmsType;
        }
        else
        {
            fields["type"] = VerificationWithRequestId;
            fields["request_id"] = context.RequestId;
            if (!string.IsNullOrEmpty(context.Source))
            {
                fields["source"] = context.Source;
            }
        }

        return fields;
    }

    internal static string VerificationSubmitEndpoint(WebVerificationContext context)
        => string.IsNullOrEmpty(context.RequestId) ? BiliApis.Passport.VerificationSmsVerifyWithoutRequestId : BiliApis.Passport.VerificationSmsVerify;

    internal static string ExchangeGoUrl(WebVerificationContext context)
        => string.IsNullOrEmpty(context.RequestId)
            ? ExchangeGoUrlBase + "?gourl=" + Uri.EscapeDataString(context.GoUrl) + "&bind_tel=1"
            : context.GoUrl;

    internal static bool HasAuthenticationCookies(IDictionary<string, string> cookies)
        => cookies.TryGetValue("SESSDATA", out var sessionData) && !string.IsNullOrWhiteSpace(sessionData)
            && cookies.TryGetValue("bili_jct", out var csrf) && !string.IsNullOrWhiteSpace(csrf)
            && cookies.TryGetValue("DedeUserID", out var userId) && !string.IsNullOrWhiteSpace(userId);

    internal static void AddUrlCookies(JsonElement root, IDictionary<string, string> cookies)
    {
        if (!TryParseQuery(GetString(GetData(root), "url"), out var query, out _))
        {
            return;
        }

        foreach (var name in new[] { "SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5", "sid" })
        {
            if ((!cookies.TryGetValue(name, out var current) || string.IsNullOrWhiteSpace(current))
                && query.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                cookies[name] = value;
            }
        }
    }

    internal static bool TryParseQuery(string url, out Dictionary<string, string> values, out Uri referer)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);
        referer = PasswordReferer;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // Keep only the page's origin/path in Referer, never the temporary tokens in its query.
        referer = new Uri(uri.GetLeftPart(UriPartial.Path));
        var query = uri.Query;
        if (string.IsNullOrEmpty(query))
        {
            var queryIndex = uri.Fragment.IndexOf('?');
            query = queryIndex >= 0 ? uri.Fragment.Substring(queryIndex) : string.Empty;
        }

        foreach (var part in query.TrimStart('?').Split('&'))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = WebUtility.UrlDecode(part.Substring(0, separator));
            var value = WebUtility.UrlDecode(part.Substring(separator + 1));
            if (values.ContainsKey(name))
            {
                values.Clear();
                return false;
            }

            values.Add(name, value);
        }

        return true;
    }

    private static bool IsNonSmsVerification(Dictionary<string, string> query, JsonElement data)
    {
        var type = GetString(data, "verify_type");
        if (query.TryGetValue("verify_type", out var queryType))
        {
            type = queryType;
        }

        if (!string.IsNullOrEmpty(type) && !type.Equals(VerificationSmsType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return query.TryGetValue("type", out var method)
            && (method.Equals("email", StringComparison.OrdinalIgnoreCase) || method.Equals("loginEmailCheck", StringComparison.OrdinalIgnoreCase));
    }

    private static BiliLoginCountry? ReadCountry(JsonElement item, string dialingCodeName)
    {
        var id = GetInt(item, "id");
        var dialingCode = GetInt(item, dialingCodeName);
        var name = GetString(item, "cname");
        return id is null or <= 0 || dialingCode is null or <= 0 || string.IsNullOrWhiteSpace(name)
            ? null : new(id.Value, name, dialingCode.Value);
    }

    private static bool TryReadCountries(JsonElement data, string name, string dialingCodeName, out List<BiliLoginCountry> countries)
    {
        countries = new List<BiliLoginCountry>();
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in list.EnumerateArray())
        {
            var country = ReadCountry(item, dialingCodeName);
            if (country is null)
            {
                return false;
            }

            countries.Add(country);
        }

        return true;
    }
}
