// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.Graphics.Canvas.Text;
using Richasy.WinUIKernel.Share.Toolkits;
using System.Globalization;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// Loads a privately owned font snapshot without the shared toolkit's mutable cache.
/// </summary>
internal static class DanmakuFontCatalog
{
    internal static Task<IReadOnlyList<SystemFont>> LoadAsync()
        => Task.Run<IReadOnlyList<SystemFont>>(ReadFonts);

    internal static SystemFont? Find(IReadOnlyList<SystemFont> fonts, string family)
        => fonts.FirstOrDefault(p => string.Equals(p.LocalName, family, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.EngName, family, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<SystemFont> ReadFonts()
    {
        // Each worker owns its list. Other players can load or sort their fonts concurrently.
        var fonts = new List<SystemFont>();
        var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var englishNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var language = CultureInfo.CurrentCulture.TwoLetterISOLanguageName;
        using var fontSet = CanvasFontSet.GetSystemFontSet();
        foreach (var face in fontSet.Fonts)
        {
            using (face)
            {
                if (face.IsSymbolFont) continue;
                var names = face.FamilyNames;
                var englishName = names.TryGetValue("en-us", out var name) ? name : string.Empty;
                var localName = names.FirstOrDefault(p => p.Key.StartsWith(language, StringComparison.OrdinalIgnoreCase)).Value
                    ?? names.Values.FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(localName) || localNames.Contains(localName)
                    || (!string.IsNullOrEmpty(englishName) && englishNames.Contains(englishName))) continue;

                localNames.Add(localName);
                if (!string.IsNullOrEmpty(englishName)) englishNames.Add(englishName);
                fonts.Add(new SystemFont { LocalName = localName, EngName = englishName });
            }
        }

        return Array.AsReadOnly(fonts.OrderBy(p => p.LocalName, StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
