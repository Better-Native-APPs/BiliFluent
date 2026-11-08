// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;

namespace BiliCopilot.UI.Controls.Danmaku;

internal sealed record MpvAssDanmakuItem(double Start, int Mode, double FontSize, string Text, uint Color);

internal sealed record MpvAssDanmakuStyle(
    string FontFamily,
    double FontScale,
    bool Bold,
    double Opacity,
    double Outline,
    double Area,
    double Speed,
    bool Rolling,
    bool Top,
    bool Bottom,
    bool Limit,
    double PanelWidth,
    double PanelHeight,
    double RasterizationScale);

/// <summary>
/// Creates positioned ASS events using independent lane admission and linear collision tests.
/// All sizes are converted from panel DIPs to the video's script coordinate system.
/// </summary>
internal static class MpvAssDanmakuDocument
{
    private const int ScriptHeight = 1080;

    internal static string Build(
        IEnumerable<MpvAssDanmakuItem> items,
        MpvAssDanmakuStyle style,
        double aspect,
        CancellationToken token,
        out int skippedAdvanced,
        out int eventCount)
    {
        aspect = double.IsFinite(aspect) && aspect > 0 ? aspect : 16d / 9;
        var width = Math.Max(1, (int)Math.Round(ScriptHeight * aspect));
        var videoHeight = Math.Min(style.PanelHeight, style.PanelWidth / aspect);
        var scale = ScriptHeight / Math.Max(1, videoHeight);
        var outline = style.Outline * scale;
        // Normal Bilibili size 25 has already been adjusted to 22 by the common parser.
        var normalFontSize = GetDipFontSize(22, style) * scale;
        var laneHeight = Math.Max(1, (normalFontSize * 1.2) + (2 * outline));
        var areaHeight = ScriptHeight * Math.Clamp(style.Area, 0.05, 1);
        var laneCount = Math.Max(1, (int)Math.Floor(areaHeight / laneHeight));
        var scrolling = new RollingEvent?[laneCount];
        var top = new double[laneCount];
        var bottom = new double[laneCount];
        var active = new PriorityQueue<double, double>();
        var duration = 9 / Math.Max(0.1, style.Speed);
        var alpha = (int)Math.Round((1 - Math.Clamp(style.Opacity, 0, 1)) * 255);
        var font = style.FontFamily.Replace(',', ' ').Replace('\r', ' ').Replace('\n', ' ');
        var builder = new StringBuilder();
        builder.AppendLine("[Script Info]")
            .AppendLine("ScriptType: v4.00+")
            .AppendLine(FormattableString.Invariant($"PlayResX: {width}"))
            .AppendLine("PlayResY: 1080")
            .AppendLine("WrapStyle: 2")
            .AppendLine("ScaledBorderAndShadow: yes")
            .AppendLine("[V4+ Styles]")
            .AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding")
            .AppendLine(FormattableString.Invariant($"Style: Danmaku,{font},{Number(normalFontSize)},&H{alpha:X2}FFFFFF,&H{alpha:X2}FFFFFF,&H{alpha:X2}000000,&HFF000000,{(style.Bold ? -1 : 0)},0,0,0,100,100,0,0,1,{Number(outline)},0,7,0,0,0,1"))
            .AppendLine("[Events]")
            .AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        skippedAdvanced = 0;
        eventCount = 0;
        foreach (var item in items.OrderBy(p => p.Start))
        {
            token.ThrowIfCancellationRequested();
            if (item.Mode == 7)
            {
                skippedAdvanced++;
                continue;
            }

            var rolling = item.Mode is 1 or 6;
            if ((!rolling || !style.Rolling) && (item.Mode != 5 || !style.Top) && (item.Mode != 4 || !style.Bottom)) continue;
            var text = Escape(item.Text);
            if (text.Length == 0) continue;
            var start = Math.Max(0, item.Start);
            var fontSize = GetDipFontSize(item.FontSize, style) * scale;
            var span = Math.Min(laneCount, Math.Max(1, (int)Math.Ceiling(((fontSize * 1.2) + (2 * outline)) / laneHeight)));
            var end = start + (rolling ? duration : 4);
            string position;
            if (rolling)
            {
                while (active.TryPeek(out var expiry, out _) && expiry <= start) active.Dequeue();
                if (style.Limit && active.Count >= 20) continue;
                var textWidth = EstimateWidth(text, fontSize, style.Bold) + (2 * outline);
                var current = new RollingEvent(start, end, textWidth, (width + textWidth) / duration, item.Mode);
                var lane = FindRollingLane(scrolling, span, current, width, outline + 2);
                if (lane < 0) continue;
                for (var row = lane; row < lane + span; row++) scrolling[row] = current;
                active.Enqueue(end, end);
                var y = (lane * laneHeight) + outline;
                position = item.Mode == 6
                    ? $"\\an7\\move({Number(-textWidth)},{Number(y)},{width.ToString(CultureInfo.InvariantCulture)},{Number(y)})"
                    : $"\\an7\\move({width.ToString(CultureInfo.InvariantCulture)},{Number(y)},{Number(-textWidth)},{Number(y)})";
            }
            else
            {
                var lanes = item.Mode == 5 ? top : bottom;
                var lane = FindFixedLane(lanes, span, start);
                if (lane < 0) continue;
                for (var row = lane; row < lane + span; row++) lanes[row] = end;
                var y = item.Mode == 5 ? (lane * laneHeight) + outline : ScriptHeight - (lane * laneHeight) - outline;
                position = $"\\an{(item.Mode == 5 ? 8 : 2)}\\pos({Number(width / 2d)},{Number(y)})";
            }

            var color = item.Color & 0xFFFFFF;
            var red = (color >> 16) & 255;
            var green = (color >> 8) & 255;
            var blue = color & 255;
            var border = ((red * 299) + (green * 587) + (blue * 114)) < 60000 ? "FFFFFF" : "000000";
            var primary = color == 0xFFFFFF ? string.Empty : FormattableString.Invariant($"\\c&H{blue:X2}{green:X2}{red:X2}&");
            builder.Append("Dialogue: 0,").Append(Time(start)).Append(',').Append(Time(end))
                .Append(",Danmaku,,0,0,0,,{").Append(position)
                .Append("\\fs").Append(Number(fontSize)).Append(primary)
                .Append("\\3c&H").Append(border).Append("&}").AppendLine(text);
            eventCount++;
        }

        return builder.ToString();
    }

    private static int FindRollingLane(RollingEvent?[] lanes, int span, RollingEvent current, double width, double gap)
    {
        for (var lane = 0; lane <= lanes.Length - span; lane++)
        {
            var fits = true;
            for (var row = lane; row < lane + span; row++)
            {
                var previous = lanes[row];
                if (previous is null || previous.End <= current.Start) continue;
                // Opposite directions cannot safely share a lane until the earlier event exits.
                // In a common direction the gap is linear, so testing both endpoints is sufficient.
                var until = Math.Min(previous.End, current.End);
                if (previous.Mode != current.Mode || Separation(current.Start) < gap || Separation(until) < gap)
                {
                    fits = false;
                    break;
                }

                double Separation(double time)
                    => (width - (current.Speed * (time - current.Start)))
                        - (width + previous.Width - (previous.Speed * (time - previous.Start)));
            }

            if (fits) return lane;
        }

        return -1;
    }

    private static int FindFixedLane(double[] lanes, int span, double start)
    {
        for (var lane = 0; lane <= lanes.Length - span; lane++)
        {
            var fits = true;
            for (var row = lane; row < lane + span; row++)
            {
                if (lanes[row] > start) { fits = false; break; }
            }

            if (fits) return lane;
        }

        return -1;
    }

    private static double EstimateWidth(string text, double fontSize, bool bold)
    {
        var em = 0d;
        foreach (var rune in text.EnumerateRunes())
        {
            em += rune.Value <= 0x7F ? 0.55
                : rune.Value is >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7AF or >= 0xF900 and <= 0xFAFF
                    or >= 0xFE10 and <= 0xFE6F or >= 0xFF01 and <= 0xFF60 or >= 0x1F000 ? 1 : 0.7;
        }

        return em * fontSize * (bold ? 1.05 : 1);
    }

    private static double GetDipFontSize(double baseSize, MpvAssDanmakuStyle style)
    {
        // Match the Win2D renderer's 800-pixel narrow-window adjustment, integer font
        // rounding and final inverse-rasterization drawing transform without creating a canvas.
        var raster = style.RasterizationScale;
        var canvasWidth = style.PanelWidth * raster;
        var size = baseSize * style.FontScale;
        if (canvasWidth < 800)
        {
            size *= canvasWidth / 800;
            if (size >= 30) size *= 0.75;
        }

        size = Math.Max(2, (int)size);
        if (canvasWidth > 800) size *= raster;
        return size / raster;
    }

    private static string Escape(string text)
        => text.Replace('\\', '＼').Replace('{', '｛').Replace('}', '｝').Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Time(double seconds)
    {
        var centiseconds = (long)Math.Round(seconds * 100);
        return FormattableString.Invariant($"{centiseconds / 360000}:{centiseconds / 6000 % 60:00}:{centiseconds / 100 % 60:00}.{centiseconds % 100:00}");
    }

    private sealed record RollingEvent(double Start, double End, double Width, double Speed, int Mode);
}
