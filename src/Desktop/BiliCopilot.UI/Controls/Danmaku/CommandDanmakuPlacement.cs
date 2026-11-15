// Copyright (c) BiliFluent. All rights reserved.

namespace BiliCopilot.UI.Controls.Danmaku;

/// <summary>
/// Where command danmaku cards may be placed for one overlay size: the displayed picture,
/// the preferred rectangle cards stay inside, the wider fallback, and the card width.
/// </summary>
internal readonly record struct CommandDanmakuArea(Rect Picture, Rect Bounds, Rect Safe, double CardWidth);

/// <summary>
/// Pure geometry for command danmaku cards. Every assumption about the server coordinate system
/// and the anchor strategy lives here, so a later calibration only touches this file.
/// </summary>
internal static class CommandDanmakuPlacement
{
    /// <summary>Width of the canvas that extra.posX refers to.</summary>
    internal const double ReferenceWidth = 667;

    /// <summary>Height of the canvas that extra.posY refers to.</summary>
    internal const double ReferenceHeight = 375;

    /// <summary>extra.posX_2 / extra.posY_2 are percentages of the picture.</summary>
    internal const double PercentScale = 100;

    /// <summary>Cards keep their normal size; they only narrow on a small picture.</summary>
    internal const double CardMaxWidth = 300;

    internal const double CardMinWidth = 248;

    /// <summary>Share of the available width one card may take before it reaches its minimum.</summary>
    internal const double CardWidthFraction = 0.42;

    /// <summary>Below this usable width or height the picture is too small to hold a card.</summary>
    internal const double MinAreaWidth = 400;

    internal const double MinAreaHeight = 180;

    /// <summary>Distance kept from the picture edge, the header and the control bar.</summary>
    internal const double EdgeMargin = 12;

    /// <summary>Distance kept between two cards, or a card and another floating notice.</summary>
    internal const double CardGap = 8;

    /// <summary>
    /// A card without server coordinates docks above this share of the picture height,
    /// so it stays out of the band where subtitles are normally drawn.
    /// </summary>
    internal const double DockedBottomInset = 0.14;

    /// <summary>
    /// Converts one server coordinate to 0..1 with the origin at the top-left corner.
    /// The percentage form wins; the reference-canvas form is the fallback. Null when neither exists.
    /// </summary>
    internal static double? Normalize(double? percent, double? reference, double referenceSize)
    {
        if (percent is { } p && double.IsFinite(p)) return Math.Clamp(p / PercentScale, 0, 1);
        if (reference is { } r && double.IsFinite(r) && referenceSize > 0) return Math.Clamp(r / referenceSize, 0, 1);
        return null;
    }

    /// <summary>
    /// The rectangle the picture really occupies inside the player, without the black bars.
    /// An unknown aspect ratio, or a picture cropped to fill, uses the whole player.
    /// </summary>
    internal static Rect GetPictureRect(Size host, double? aspectRatio, bool fillsHost)
    {
        if (!IsUsable(host)) return default;
        if (fillsHost || aspectRatio is not { } aspect || !double.IsFinite(aspect) || aspect <= 0)
            return new Rect(0, 0, host.Width, host.Height);
        if (aspect > host.Width / host.Height)
        {
            var height = host.Width / aspect;
            return new Rect(0, (host.Height - height) / 2, host.Width, height);
        }

        var width = host.Height * aspect;
        return new Rect((host.Width - width) / 2, 0, width, host.Height);
    }

    /// <summary>
    /// Works out the placement area. <paramref name="topReserved"/> and <paramref name="bottomReserved"/>
    /// are the strips owned by the header and the control bar. Null means: too small, show nothing.
    /// </summary>
    internal static CommandDanmakuArea? GetArea(Size host, Rect picture, double topReserved, double bottomReserved)
    {
        if (!IsUsable(host)) return null;
        var safe = FromEdges(EdgeMargin, Math.Max(0, topReserved) + EdgeMargin,
            host.Width - EdgeMargin, host.Height - Math.Max(0, bottomReserved) - EdgeMargin);
        if (safe.Width < MinAreaWidth || safe.Height <= 0) return null;

        var inner = FromEdges(Math.Max(picture.Left + EdgeMargin, safe.Left), Math.Max(picture.Top + EdgeMargin, safe.Top),
            Math.Min(picture.Right - EdgeMargin, safe.Right), Math.Min(picture.Bottom - EdgeMargin, safe.Bottom));

        // A picture too narrow or too short for a card (a vertical video in a wide window) keeps its
        // coordinates as the anchor, but cards may then use the rest of the player.
        var bounds = inner.Width >= MinAreaWidth && inner.Height >= MinAreaHeight ? inner : safe;
        var width = Math.Round(Math.Clamp(bounds.Width * CardWidthFraction, CardMinWidth, CardMaxWidth));
        return new CommandDanmakuArea(picture, bounds, safe, Math.Min(width, bounds.Width));
    }

    /// <summary>
    /// The anchor strategy: the position ratio is also the anchor ratio. (0, 0) puts the card in the
    /// picture's top-left corner, (1, 1) in its bottom-right corner, and anything between moves evenly,
    /// so the card can never leave the picture.
    /// </summary>
    internal static Point GetCardOrigin(double x, double y, Rect picture, Size card)
        => new(picture.Left + (x * (picture.Width - card.Width)), picture.Top + (y * (picture.Height - card.Height)));

    /// <summary>Moves the card the shortest way back inside <paramref name="bounds"/>.</summary>
    internal static Point ClampOrigin(Point origin, Size card, Rect bounds)
        => new(
            Math.Clamp(origin.X, bounds.Left, Math.Max(bounds.Left, bounds.Right - card.Width)),
            Math.Clamp(origin.Y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - card.Height)));

    /// <summary>
    /// Final rectangle of one card. <paramref name="x"/> and <paramref name="y"/> are normalized server
    /// coordinates; a card without them docks to the bottom-left corner, above the subtitle band.
    /// A card that would cover <paramref name="occupied"/> (earlier cards, other notices) is pushed
    /// up or down, whichever is nearer. Null when it cannot fit anywhere.
    /// </summary>
    internal static Rect? Place(double? x, double? y, Size card, CommandDanmakuArea area, IReadOnlyList<Rect> occupied)
    {
        if (!(card.Width > 0) || !(card.Height > 0)) return null;
        for (var pass = 0; pass < 2; pass++)
        {
            var region = pass == 0 ? area.Bounds : area.Safe;
            if (pass == 1 && region == area.Bounds) break;
            if (card.Width > region.Width || card.Height > region.Height) continue;
            var origin = x is { } horizontal && y is { } vertical
                ? ClampOrigin(GetCardOrigin(horizontal, vertical, area.Picture, card), card, region)
                : GetDockedOrigin(card, area.Picture, region);
            if (Separate(new Rect(Math.Round(origin.X), Math.Round(origin.Y), card.Width, card.Height), region, occupied) is { } rect)
                return rect;
        }

        return null;
    }

    private static Point GetDockedOrigin(Size card, Rect picture, Rect region)
    {
        var bottom = Math.Min(region.Bottom, picture.Bottom - (picture.Height * DockedBottomInset));
        return new Point(region.Left, Math.Max(region.Top, bottom - card.Height));
    }

    private static Rect? Separate(Rect rect, Rect region, IReadOnlyList<Rect> occupied)
    {
        // Each step clears one obstacle; a few steps are enough for two cards and one notice.
        for (var step = 0; step <= occupied.Count + 1; step++)
        {
            var hit = FindOverlap(rect, occupied);
            if (hit is not { } other) return rect;
            var up = other.Top - CardGap - rect.Height;
            var down = other.Bottom + CardGap;
            var canUp = up >= region.Top;
            var canDown = down + rect.Height <= region.Bottom;
            if (!canUp && !canDown) return null;
            var top = canUp && (!canDown || rect.Top - up <= down - rect.Top) ? up : down;
            rect = new Rect(rect.Left, Math.Round(top), rect.Width, rect.Height);
        }

        return FindOverlap(rect, occupied) is null ? rect : null;
    }

    private static Rect? FindOverlap(Rect rect, IReadOnlyList<Rect> occupied)
    {
        for (var index = 0; index < occupied.Count; index++)
        {
            var other = occupied[index];
            if (rect.Left < other.Right + CardGap && other.Left < rect.Right + CardGap
                && rect.Top < other.Bottom + CardGap && other.Top < rect.Bottom + CardGap) return other;
        }

        return null;
    }

    private static Rect FromEdges(double left, double top, double right, double bottom)
        => new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    private static bool IsUsable(Size size)
        => size.Width > 0 && size.Height > 0 && double.IsFinite(size.Width) && double.IsFinite(size.Height);
}
