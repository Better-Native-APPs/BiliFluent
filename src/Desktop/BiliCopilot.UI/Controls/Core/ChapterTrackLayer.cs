// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace BiliCopilot.UI.Controls.Core;

/// <summary>
/// Cuts the track of a progress slider into chapter segments.
/// The layer sits inside the slider's own track grid, above the track and the played fill and below the thumb,
/// so the played, buffered and marker colours stay as they are and only thin gaps are drawn over them.
/// It draws and measures only: it never takes input and never writes to the slider.
/// </summary>
internal sealed class ChapterTrackLayer
{
    private const double GapWidth = 2;
    private const double FallbackThumbWidth = 18;
    private const double FallbackTrackHeight = 4;

    private readonly Slider _slider;
    private readonly Style _gapStyle;
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly Rectangle _highlight;
    private readonly List<ChapterItemViewModel> _chapters = [];
    private Grid? _track;
    private FrameworkElement? _thumb;
    private ChapterItemViewModel? _highlighted;

    public ChapterTrackLayer(Slider slider, Style gapStyle, Style highlightStyle)
    {
        _slider = slider;
        _gapStyle = gapStyle;
        _highlight = new Rectangle
        {
            Style = highlightStyle,
            RadiusX = 1,
            RadiusY = 1,
            Opacity = 0,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) },
        };
        _canvas.Children.Add(_highlight);
        AutomationProperties.SetAccessibilityView(_canvas, AccessibilityView.Raw);
        Grid.SetRow(_canvas, 1);
        Grid.SetColumnSpan(_canvas, 3);
        _canvas.SizeChanged += (_, _) => Render();
        _slider.Loaded += (_, _) => Render();
        _slider.SizeChanged += (_, _) => Render();
        _slider.RegisterPropertyChangedCallback(RangeBase.MaximumProperty, (_, _) => Render());
    }

    /// <summary>Whether the track is cut into segments right now.</summary>
    public bool HasChapters => _chapters.Count >= 2;

    /// <summary>Sets the chapters to draw, or clears the segments for <see langword="null"/>.</summary>
    public void SetChapters(IEnumerable<ChapterItemViewModel>? chapters)
    {
        _chapters.Clear();
        if (chapters is not null) _chapters.AddRange(chapters);
        if (_highlighted is not null && !_chapters.Contains(_highlighted)) _highlighted = null;
        Render();
    }

    /// <summary>The chapter that holds the position, with the same rule as the current chapter: start &lt;= position &lt; end.</summary>
    public ChapterItemViewModel? ChapterAt(double seconds)
    {
        if (!HasChapters || !double.IsFinite(seconds)) return null;
        foreach (var chapter in _chapters)
        {
            if (chapter.Start <= seconds && seconds < chapter.End) return chapter;
        }

        // The very end of the track still belongs to the last chapter.
        var last = _chapters[^1];
        return seconds >= last.Start && seconds <= last.End ? last : null;
    }

    /// <summary>Marks one segment, or none for <see langword="null"/>.</summary>
    public void Highlight(ChapterItemViewModel? chapter)
    {
        if (ReferenceEquals(_highlighted, chapter)) return;
        _highlighted = chapter;
        PlaceHighlight();
    }

    /// <summary>The position under the pointer, on the same scale the slider uses for its thumb.</summary>
    public bool TryGetPosition(PointerRoutedEventArgs args, out double seconds)
    {
        seconds = 0;
        if (!EnsureAttached() || !TryGetScale(out var thumb, out var usable, out var maximum)) return false;
        var x = args.GetCurrentPoint(_track!).Position.X;
        seconds = Math.Clamp((x - (thumb / 2)) / usable, 0, 1) * maximum;
        return true;
    }

    /// <summary>The point above the track at the position, in the coordinates of another element.</summary>
    public bool TryGetAnchor(double seconds, UIElement relativeTo, out Point anchor)
    {
        anchor = default;
        if (!_slider.IsLoaded || !EnsureAttached() || !TryGetOffset(seconds, out var x)) return false;
        try
        {
            anchor = _track!.TransformToVisual(relativeTo).TransformPoint(new Point(x, 0));
            return true;
        }
        catch (Exception)
        {
            // The two elements are not in one live tree at this moment.
            return false;
        }
    }

    private static T? FindChild<T>(DependencyObject parent, string name)
        where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T element && element.Name == name) return element;
            if (FindChild<T>(child, name) is { } nested) return nested;
        }

        return null;
    }

    private bool EnsureAttached()
    {
        if (_track is not null && ReferenceEquals(_canvas.Parent, _track)) return true;
        _track = null;
        _thumb = null;
        (_canvas.Parent as Panel)?.Children.Remove(_canvas);

        // Both the stock slider template and the player slider style name these two parts.
        if (FindChild<Grid>(_slider, "HorizontalTemplate") is not { } track
            || FindChild<FrameworkElement>(track, "HorizontalThumb") is not { } thumb)
        {
            return false;
        }

        // Before the thumb: above the track and the played fill, below the thumb.
        var index = track.Children.IndexOf(thumb);
        track.Children.Insert(index < 0 ? track.Children.Count : index, _canvas);
        _track = track;
        _thumb = thumb;
        return true;
    }

    private bool TryGetScale(out double thumb, out double usable, out double maximum)
    {
        thumb = _thumb is { ActualWidth: > 0 } element ? element.ActualWidth : FallbackThumbWidth;
        usable = (_track?.ActualWidth ?? 0) - thumb;
        maximum = _slider.Maximum;
        return usable > 0 && maximum > 0 && double.IsFinite(maximum);
    }

    private bool TryGetOffset(double seconds, out double x)
    {
        x = 0;
        if (!double.IsFinite(seconds) || !TryGetScale(out var thumb, out var usable, out var maximum)) return false;
        x = (thumb / 2) + (Math.Clamp(seconds / maximum, 0, 1) * usable);
        return true;
    }

    private void Render()
    {
        if (!EnsureAttached()) return;
        var children = _canvas.Children;
        for (var i = children.Count - 1; i >= 1; i--) children.RemoveAt(i);

        if (HasChapters && TryGetScale(out _, out _, out var maximum))
        {
            var height = _canvas.ActualHeight > 0 ? _canvas.ActualHeight : FallbackTrackHeight;
            var boundaries = new List<double>(_chapters.Count * 2);
            foreach (var chapter in _chapters)
            {
                boundaries.Add(chapter.Start);
                boundaries.Add(chapter.End);
            }

            boundaries.Sort();
            var previous = double.NegativeInfinity;
            foreach (var boundary in boundaries)
            {
                // The two ends of the track are not gaps.
                if (!(boundary > 0) || !(boundary < maximum) || !TryGetOffset(boundary, out var x)) continue;
                var left = Math.Round(x - (GapWidth / 2));
                if (left - previous < GapWidth * 2) continue;
                previous = left;
                var gap = new Rectangle { Width = GapWidth, Height = height, Style = _gapStyle };
                Canvas.SetLeft(gap, left);
                children.Add(gap);
            }
        }

        PlaceHighlight();
    }

    private void PlaceHighlight()
    {
        if (_highlighted is not { } chapter || _track is null || !HasChapters
            || !TryGetScale(out _, out _, out var maximum)
            || !TryGetOffset(chapter.Start, out var start) || !TryGetOffset(chapter.End, out var end))
        {
            _highlight.Opacity = 0;
            return;
        }

        // The first and the last segment run to the ends of the track; the others stop at their gaps.
        var left = chapter.Start > 0 ? Math.Round(start + (GapWidth / 2)) : 0;
        var right = chapter.End < maximum ? Math.Round(end - (GapWidth / 2)) : _track.ActualWidth;
        if (right - left < 1)
        {
            _highlight.Opacity = 0;
            return;
        }

        _highlight.Width = right - left;
        _highlight.Height = _canvas.ActualHeight > 0 ? _canvas.ActualHeight : FallbackTrackHeight;
        Canvas.SetLeft(_highlight, left);
        _highlight.Opacity = 1;
    }
}
