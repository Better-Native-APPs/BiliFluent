// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Media;

/// <summary>
/// A selectable audio language: the original audio, or a dubbing language advertised by the API.
/// </summary>
public sealed class AudioLanguageInformation(
    string id,
    string? name = default,
    bool isAI = false,
    bool needVip = false,
    bool hasAudio = false,
    int? productionType = default)
{
    /// <summary>
    /// The identifier of the original audio.
    /// </summary>
    public const string OriginalId = "original";

    /// <summary>
    /// Gets the language identifier: <see cref="OriginalId"/>, or the language code the API accepts.
    /// </summary>
    public string Id { get; } = id;

    /// <summary>
    /// Gets the display name supplied by the API, if any.
    /// </summary>
    public string? Name { get; } = name;

    /// <summary>
    /// Gets a value indicating whether this is the original audio.
    /// </summary>
    public bool IsOriginal => Id == OriginalId;

    /// <summary>
    /// Gets a value indicating whether the API marks this language itself as AI dubbing.
    /// </summary>
    public bool IsAI { get; } = isAI;

    /// <summary>
    /// Gets the production type the API reports for this language, as it is. Null when it reports none.
    /// </summary>
    public int? ProductionType { get; } = productionType;

    /// <summary>
    /// Gets a value indicating whether the API requires VIP access.
    /// </summary>
    public bool NeedVip { get; } = needVip;

    /// <summary>
    /// Gets a value indicating whether the loaded response serves this language.
    /// </summary>
    public bool HasAudio { get; } = hasAudio;

    /// <summary>
    /// Gets a value indicating whether the language can be chosen. A dubbing language gets its address only
    /// after it is requested, so only a VIP language that was requested and not served is ruled out.
    /// </summary>
    public bool IsEnabled => IsOriginal || !NeedVip || HasAudio;

    /// <summary>
    /// Whether two language codes name one language. A regional suffix is ignored ("en" matches "en-US").
    /// </summary>
    public static bool MatchesCode(string? requested, string? returned)
    {
        if (string.IsNullOrWhiteSpace(requested) || string.IsNullOrWhiteSpace(returned))
        {
            return false;
        }

        var first = requested.Replace('_', '-');
        var second = returned.Replace('_', '-');
        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
            || first.StartsWith(second + "-", StringComparison.OrdinalIgnoreCase)
            || second.StartsWith(first + "-", StringComparison.OrdinalIgnoreCase);
    }
}
