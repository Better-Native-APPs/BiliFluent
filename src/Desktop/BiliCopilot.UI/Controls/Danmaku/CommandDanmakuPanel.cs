// Copyright (c) BiliFluent. All rights reserved.

namespace BiliCopilot.UI.Controls.Danmaku;

/// <summary>
/// Lays out a few <see cref="InteractiveDanmakuCard"/> children over the video, the most important
/// (lowest <see cref="InteractiveDanmakuCard.Order"/>) first. Positions are resolved inside the layout
/// pass itself, so a resize, a card growing or a new reserved strip never positions anything from an
/// event handler.
/// </summary>
public sealed partial class CommandDanmakuPanel : Panel
{
    private readonly List<Rect> _occupied = [];
    private readonly List<InteractiveDanmakuCard> _ordered = [];
    private double? _aspectRatio;
    private bool _fillsHost;
    private double _topReserved;
    private double _bottomReserved;
    private Size _noticeSize;
    private Thickness _noticeMargin;
    private Size _lastArrangeSize;

    /// <summary>Gets or sets a value indicating whether a card may glide to a new position.</summary>
    internal bool AnimationsEnabled { get; set; }

    /// <summary>
    /// Describes the surroundings of the cards. Cheap to call again with the same values.
    /// </summary>
    /// <param name="aspectRatio">Width / height of the picture, or null when unknown.</param>
    /// <param name="fillsHost">Whether the picture is cropped to fill the whole player.</param>
    /// <param name="topReserved">Height owned by the header at the top.</param>
    /// <param name="bottomReserved">Height owned by the control bar at the bottom.</param>
    /// <param name="noticeSize">Size of the notice docked to the bottom-right corner; empty when there is none.</param>
    /// <param name="noticeMargin">Distance of that notice from the right and bottom edges.</param>
    internal void SetEnvironment(double? aspectRatio, bool fillsHost, double topReserved, double bottomReserved,
        Size noticeSize, Thickness noticeMargin)
    {
        if (_aspectRatio == aspectRatio && _fillsHost == fillsHost && _topReserved == topReserved
            && _bottomReserved == bottomReserved && _noticeSize == noticeSize && _noticeMargin == noticeMargin) return;
        _aspectRatio = aspectRatio;
        _fillsHost = fillsHost;
        _topReserved = topReserved;
        _bottomReserved = bottomReserved;
        _noticeSize = noticeSize;
        _noticeMargin = noticeMargin;
        InvalidateMeasure();
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var host = new Size(
            double.IsFinite(availableSize.Width) ? availableSize.Width : 0,
            double.IsFinite(availableSize.Height) ? availableSize.Height : 0);
        var area = CommandDanmakuPlacement.GetArea(host,
            CommandDanmakuPlacement.GetPictureRect(host, _aspectRatio, _fillsHost), _topReserved, _bottomReserved);
        _occupied.Clear();
        if (_noticeSize.Width > 0 && _noticeSize.Height > 0)
        {
            // The other notice is docked to the bottom-right corner; cards keep clear of it.
            _occupied.Add(new Rect(
                Math.Max(0, host.Width - _noticeMargin.Right - _noticeSize.Width),
                Math.Max(0, host.Height - _noticeMargin.Bottom - _noticeSize.Height),
                _noticeSize.Width, _noticeSize.Height));
        }

        _ordered.Clear();
        foreach (var child in Children)
        {
            if (child is not InteractiveDanmakuCard card) continue;
            var index = _ordered.Count;
            while (index > 0 && _ordered[index - 1].Order > card.Order) index--;
            _ordered.Insert(index, card);
        }

        foreach (var card in _ordered)
        {
            card.Slot = area is { } usable ? Resolve(card, usable) : null;
            if (card.Slot is { } slot) _occupied.Add(slot);

            // A card with no room is collapsed, not clipped or squeezed; it returns when room does.
            var visibility = card.Slot.HasValue ? Visibility.Visible : Visibility.Collapsed;
            if (card.Visibility != visibility) card.Visibility = visibility;
        }

        // The layer takes whatever the overlay gives it and never asks for space of its own.
        return default;
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        // Follow a window resize directly; only glide when the surroundings change.
        var glide = AnimationsEnabled && finalSize == _lastArrangeSize;
        _lastArrangeSize = finalSize;
        foreach (var child in Children)
        {
            if (child is not InteractiveDanmakuCard card) continue;
            if (card.Slot is not { } slot)
            {
                card.ArrangedSlot = null;
                continue;
            }

            if (glide && card.ArrangedSlot is { } previous) card.GlideFrom(previous.Left - slot.Left, previous.Top - slot.Top);
            card.Arrange(slot);
            card.ArrangedSlot = slot;
        }

        return finalSize;
    }

    private Rect? Resolve(InteractiveDanmakuCard card, CommandDanmakuArea area)
    {
        var available = new Size(area.CardWidth, double.PositiveInfinity);
        if (card.Visibility == Visibility.Visible)
        {
            card.Measure(available);
            card.MeasuredHeight = card.DesiredSize.Height;
        }
        else if (card.MeasuredHeight <= 0)
        {
            return MeasureShown(card, area, available);
        }

        var slot = CommandDanmakuPlacement.Place(card.NormalizedX, card.NormalizedY,
            new Size(area.CardWidth, card.MeasuredHeight), area, _occupied);

        // The remembered height of a collapsed card can be stale: measure it for real before it returns.
        return slot.HasValue && card.Visibility != Visibility.Visible ? MeasureShown(card, area, available) : slot;
    }

    private Rect? MeasureShown(InteractiveDanmakuCard card, CommandDanmakuArea area, Size available)
    {
        card.Visibility = Visibility.Visible;
        card.Measure(available);
        card.MeasuredHeight = card.DesiredSize.Height;
        return CommandDanmakuPlacement.Place(card.NormalizedX, card.NormalizedY,
            new Size(area.CardWidth, card.MeasuredHeight), area, _occupied);
    }
}
