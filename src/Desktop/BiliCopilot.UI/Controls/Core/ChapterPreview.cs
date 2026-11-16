// Copyright (c) BiliFluent. All rights reserved.

using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace BiliCopilot.UI.Controls.Core;

/// <summary>
/// Shows the chapter and the time over a progress slider while the pointer hovers or drags it.
/// It only watches the slider: the pointer handlers also see handled events and change nothing,
/// the bubble takes neither the pointer nor the focus, and the slider's value is read, never written.
/// </summary>
internal sealed class ChapterPreview
{
    private const double EdgeMargin = 4;
    private const double TrackSpacing = 2;

    private readonly Slider _slider;
    private readonly ChapterTrackLayer _track;
    private readonly Popup _popup;
    private readonly ChapterPreviewTip _tip;
    private readonly Func<bool> _canShow;
    private ChapterTrackLayer? _shownTrack;
    private bool _pointerOver;
    private bool _pressed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChapterPreview"/> class.
    /// </summary>
    /// <param name="slider">The slider the user hovers and drags.</param>
    /// <param name="track">The chapter layer of that slider.</param>
    /// <param name="popup">The popup that holds the bubble; it sits in the same tree as the slider.</param>
    /// <param name="tip">The bubble.</param>
    /// <param name="canShow">Whether the bar takes input right now.</param>
    public ChapterPreview(Slider slider, ChapterTrackLayer track, Popup popup, ChapterPreviewTip tip, Func<bool> canShow)
    {
        _slider = slider;
        _track = track;
        _popup = popup;
        _tip = tip;
        _canShow = canShow;
        slider.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(OnPointerMoved), true);
        slider.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerMoved), true);
        slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPointerReleased), true);
        slider.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(OnPointerExited), true);
        slider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnPointerLost), true);
        slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnPointerLost), true);
        slider.ValueChanged += OnValueChanged;
    }

    /// <summary>
    /// Shows the bubble over a position of the given track. The pointer paths call it only while the bar takes input;
    /// a host calls it directly for a preview it drives itself, such as the one of a seek gesture.
    /// </summary>
    public void ShowAt(ChapterTrackLayer track, double seconds)
    {
        if (!track.HasChapters || !track.TryGetAnchor(seconds, _popup, out var anchor))
        {
            Hide();
            return;
        }

        var chapter = track.ChapterAt(seconds);
        _tip.Update(chapter?.Title, seconds);
        if (!ReferenceEquals(_shownTrack, track)) _shownTrack?.Highlight(null);
        _shownTrack = track;
        track.Highlight(chapter);

        _tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _tip.DesiredSize;
        var x = anchor.X - (size.Width / 2);
        if (_popup.XamlRoot is { } root)
        {
            try
            {
                // Keep the whole bubble inside the window.
                var origin = _popup.TransformToVisual(null).TransformPoint(default);
                var minimum = EdgeMargin - origin.X;
                var maximum = root.Size.Width - size.Width - EdgeMargin - origin.X;
                x = Math.Clamp(x, minimum, Math.Max(minimum, maximum));
            }
            catch (Exception)
            {
                // Not in a live tree at this moment; the unclamped place is still right above the pointer.
            }
        }

        _popup.HorizontalOffset = Math.Round(x);
        _popup.VerticalOffset = Math.Round(anchor.Y - size.Height - TrackSpacing);
        if (!_popup.IsOpen) _popup.IsOpen = true;
    }

    /// <summary>Hides the bubble and the segment mark, keeping the pointer state.</summary>
    public void Hide()
    {
        if (_popup.IsOpen) _popup.IsOpen = false;
        _shownTrack?.Highlight(null);
        _shownTrack = null;
    }

    /// <summary>Hides the bubble and forgets the pointer: the bar was hidden, disabled or changed its media.</summary>
    public void Reset()
    {
        _pointerOver = false;
        _pressed = false;
        Hide();
    }

    private bool IsInside(PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(_slider).Position;
        return point.X >= 0 && point.Y >= 0 && point.X <= _slider.ActualWidth && point.Y <= _slider.ActualHeight;
    }

    private void ShowAtPointer(PointerRoutedEventArgs args)
    {
        if (_canShow() && _track.TryGetPosition(args, out var seconds)) ShowAt(_track, seconds);
        else Hide();
    }

    private void ShowAtValue(double seconds)
    {
        if (_canShow() && double.IsFinite(seconds)) ShowAt(_track, seconds);
        else Hide();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        _pointerOver = IsInside(args);

        // A drag previews where the slider will seek to; a hover previews the place under the pointer.
        if (_pressed) ShowAtValue(_slider.Value);
        else if (_pointerOver) ShowAtPointer(args);
        else Hide();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        _pressed = true;
        _pointerOver = IsInside(args);
        ShowAtPointer(args);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        _pressed = false;

        // A finger or a pen that lifts is gone; a mouse may still rest over the bar.
        _pointerOver = args.Pointer.PointerDeviceType == PointerDeviceType.Mouse && IsInside(args);
        if (_pointerOver) ShowAtPointer(args);
        else Hide();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs args)
    {
        _pointerOver = false;
        if (!_pressed) Hide();
    }

    private void OnPointerLost(object sender, PointerRoutedEventArgs args)
    {
        _pressed = false;
        if (!_pointerOver) Hide();
    }

    private void OnValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        // Only a drag moves the bubble with the value. Playback moving the thumb does not show it.
        if (_pressed) ShowAtValue(args.NewValue);
    }
}
