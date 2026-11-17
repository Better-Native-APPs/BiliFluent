// Copyright (c) BiliFluent. All rights reserved.

using Richasy.BiliKernel.Models.Authorization;

namespace BiliCopilot.UI.Forms;

public sealed partial class CaptchaWindow
{
    internal static bool HasValidParameters(BiliLoginCaptchaParameters parameters)
        => parameters.Type switch
        {
            "geetest" => IsToken(parameters.Gt, 128) && IsToken(parameters.Challenge, 128),
            "geetest4" => IsToken(parameters.Gt, 128),
            "img" => parameters.ImageData.Length is > 0 and <= 1400000
                && (parameters.ImageData.StartsWith("data:image/png;base64,", StringComparison.Ordinal)
                    || parameters.ImageData.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal)
                    || parameters.ImageData.StartsWith("data:image/gif;base64,", StringComparison.Ordinal)
                    || parameters.ImageData.StartsWith("data:image/webp;base64,", StringComparison.Ordinal)),
            _ => false,
        };

    private static bool IsToken(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength)
        {
            return false;
        }

        foreach (var ch in value)
        {
            // Printable ASCII without spaces; SDK identifiers and proofs use these characters.
            if (ch is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }
}
