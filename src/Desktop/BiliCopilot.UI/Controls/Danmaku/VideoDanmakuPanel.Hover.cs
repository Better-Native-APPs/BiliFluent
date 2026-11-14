// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Richasy.Danmaku.Legacy;

namespace BiliCopilot.UI.Controls.Danmaku;

/// <summary>
/// Hover actions for one danmaku drawn by the Win2D renderer.
/// </summary>
/// <remarks>
/// The panel sits below the player's interactive surface and never receives pointer input itself.
/// It only observes the pointer events of the hosting overlay, so click, double click, drag,
/// context menu and wheel keep going to the surface untouched.
/// </remarks>
public sealed partial class VideoDanmakuPanel
{
    private const int HoverTickMs = 50; // Hit tests run on this timer, never per pointer event.
    private const int HoverDwellMs = 280;
    private const int HoverLeaveGraceMs = 320;
    private const int HoverPointerFreshMs = 400;
    private const double HoverBarGap = 4;
    private const double HoverBarReserve = 52; // Room the bar needs above a danmaku.

    private PlayerOverlay? _hoverOverlay;
    private PointerEventHandler? _hoverMovedHandler;
    private PointerEventHandler? _hoverPressedHandler;
    private PointerEventHandler? _hoverExitedHandler;
    private DispatcherQueueTimer? _hoverTimer;
    private Point _hoverPoint;
    private bool _hoverPointValid;
    private long _hoverMovedAt;
    private string? _hoverCandidateId;
    private long _hoverCandidateSince;
    private string? _hoverId;
    private string _hoverText = string.Empty;
    private Rect _hoverZone;
    private long _hoverLeftAt;
    private bool _hoverPointerInBar;
    private int _hoverSession;
    private int _hoverRedrawVersion;
    private long _hoverGeneration;
    private int _hoverProgress;
    private int? _hoverLikes;
    private bool _hoverLiked;
    private bool _hoverLikeBusy;

    private void AttachHover()
    {
        for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is PlayerOverlay overlay)
            {
                _hoverOverlay = overlay;
                break;
            }
        }

        if (_hoverOverlay is null) return;
        _hoverMovedHandler ??= OnHoverPointerMoved;
        _hoverPressedHandler ??= OnHoverPointerPressed;
        _hoverExitedHandler ??= OnHoverPointerExited;
        // Observe only: handled events are included and nothing is marked handled here.
        _hoverOverlay.AddHandler(PointerMovedEvent, _hoverMovedHandler, true);
        _hoverOverlay.AddHandler(PointerPressedEvent, _hoverPressedHandler, true);
        _hoverOverlay.AddHandler(PointerExitedEvent, _hoverExitedHandler, true);
        SizeChanged += OnHoverPanelSizeChanged;
    }

    private void DetachHover()
    {
        DismissHover();
        StopHoverTracking();
        if (_hoverOverlay is null) return;
        _hoverOverlay.RemoveHandler(PointerMovedEvent, _hoverMovedHandler!);
        _hoverOverlay.RemoveHandler(PointerPressedEvent, _hoverPressedHandler!);
        _hoverOverlay.RemoveHandler(PointerExitedEvent, _hoverExitedHandler!);
        SizeChanged -= OnHoverPanelSizeChanged;
        _hoverOverlay = null;
    }

    private void OnHoverPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_danmakuMaster is null || _canvas is null || ViewModel is not { IsHoverActionsEnabled: true, IsShowDanmaku: true }
            || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || e.IsGenerated)
        {
            return;
        }

        var point = e.GetCurrentPoint(_canvas);
        var buttons = point.Properties;
        if (buttons.IsLeftButtonPressed || buttons.IsRightButtonPressed || buttons.IsMiddleButtonPressed
            || !IsHoverSurface(e.OriginalSource))
        {
            // Dragging, or the pointer is over a control: an open bar closes after its grace period.
            _hoverPointValid = false;
            _hoverCandidateId = null;
            return;
        }

        if (_hoverPointValid && point.Position.Equals(_hoverPoint)) return;
        _hoverPoint = point.Position;
        _hoverPointValid = true;
        _hoverMovedAt = Environment.TickCount64;
        StartHoverTracking();
    }

    private void OnHoverPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // A press belongs to the player (pause, full screen, drag, context menu), so step aside.
        // The bar only returns after the pointer moves again, e.g. to pick a danmaku once paused.
        DismissHover();
        _hoverMovedAt = 0;
    }

    private void OnHoverPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hoverPointValid = false;
        _hoverCandidateId = null;
    }

    private void OnHoverPanelSizeChanged(object sender, SizeChangedEventArgs e)
        => DismissHover();

    private void OnHoverBarPointerEntered(object sender, PointerRoutedEventArgs e)
        => _hoverPointerInBar = true;

    private void OnHoverBarPointerExited(object sender, PointerRoutedEventArgs e)
        => _hoverPointerInBar = false;

    private void OnHoverFlyoutClosed(object? sender, object e)
    {
        // Also reached when the system closes the flyout, for example on a press outside.
        if (!HoverFlyout.IsOpen) DismissHover();
    }

    private void StartHoverTracking()
    {
        if (_hoverTimer is null)
        {
            _hoverTimer = DispatcherQueue.CreateTimer();
            _hoverTimer.Interval = TimeSpan.FromMilliseconds(HoverTickMs);
            _hoverTimer.IsRepeating = true;
            _hoverTimer.Tick += OnHoverTimerTick;
        }

        if (!_hoverTimer.IsRunning) _hoverTimer.Start();
    }

    private void StopHoverTracking()
    {
        _hoverTimer?.Stop();
        _hoverCandidateId = null;
    }

    private void OnHoverTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!CanHover())
        {
            DismissHover();
            StopHoverTracking();
            return;
        }

        var now = Environment.TickCount64;
        if (_hoverId is not null)
        {
            // The bar follows the player's own idle rule: once the controls and the cursor
            // are hidden it goes too, unless the pointer is resting on the bar itself.
            if (IsHoverStale() || (!_hoverPointerInBar && _playerViewModel?.IsControlsVisible != true))
            {
                DismissHover();
            }
            else if (_hoverPointerInBar || IsPointerInHoverZone())
            {
                _hoverLeftAt = 0;
            }
            else if (_hoverLeftAt == 0)
            {
                _hoverLeftAt = now;
            }
            else if (now - _hoverLeftAt >= HoverLeaveGraceMs)
            {
                DismissHover();
            }

            return;
        }

        // Only a pointer that moved recently can pick a danmaku. Danmaku rolling under a
        // resting pointer are ignored, so reading a video never pops the bar up by itself.
        var isFresh = now - _hoverMovedAt <= HoverPointerFreshMs;
        if (!_hoverPointValid || (!isFresh && _hoverCandidateId is null))
        {
            StopHoverTracking();
            return;
        }

        // Items without an id (the user's own fresh danmaku) cannot be frozen or liked.
        if (!_danmakuMaster!.TryHitTest(_hoverPoint.X, _hoverPoint.Y, out var hit) || string.IsNullOrEmpty(hit.SourceId))
        {
            _hoverCandidateId = null;
            return;
        }

        if (hit.SourceId != _hoverCandidateId)
        {
            _hoverCandidateId = hit.SourceId;
            _hoverCandidateSince = now;
        }
        else if (now - _hoverCandidateSince >= HoverDwellMs)
        {
            ShowHover(hit);
        }
    }

    private bool CanHover()
        => _danmakuMaster is not null && _canvas is not null && _rendererType == DanmakuRendererType.Win2D
            && ViewModel is { IsHoverActionsEnabled: true } && CanShowDanmaku()
            && _playerViewModel is
            {
                IsWindowDeactivated: false, IsConnecting: false, IsPopupVisible: false,
                IsExtraPanelVisible: false, IsSubtitleSettingsVisible: false,
            };

    // The renderer drops every item, including the frozen one, when the list is replaced,
    // the media changes or the position jumps.
    private bool IsHoverStale()
    {
        var progress = _lastProgress;
        var jumped = progress < _hoverProgress || progress - _hoverProgress > 5;
        _hoverProgress = progress;
        return jumped || _hoverRedrawVersion != _redrawVersion
            || _hoverGeneration != (_playerViewModel?.MediaGeneration ?? 0);
    }

    private bool IsPointerInHoverZone()
    {
        if (_canvas is null) return false;
        // Without a cursor reading, use the last pointer position seen on the overlay.
        var isInside = _hoverPointValid && _hoverZone.Contains(_hoverPoint);
        try
        {
            // The cursor is polled because a pointer over the bar, or outside the window,
            // no longer raises move events on the overlay.
            if (XamlRoot?.CoordinateConverter is { } converter && PInvoke.GetCursorPos(out var cursor))
            {
                var local = converter.ConvertScreenToLocal(new Windows.Graphics.PointInt32(cursor.X, cursor.Y));
                isInside = _canvas.TransformToVisual(null).TransformBounds(_hoverZone).Contains(local);
            }
        }
        catch (Exception)
        {
            // The island can go away between ticks; the next tick dismisses the bar.
            isInside = false;
        }

        return isInside;
    }

    private void ShowHover(DanmakuHitInfo hit)
    {
        _hoverCandidateId = null;
        if (_danmakuMaster is not { } master || _canvas is not { } canvas || _playerViewModel is not { } player) return;

        master.FreezeItem(hit.SourceId);
        // Read the bounds again: they are now the place where the item was frozen.
        if (!master.TryHitTest(_hoverPoint.X, _hoverPoint.Y, out var frozen) || frozen.SourceId != hit.SourceId)
        {
            master.ReleaseFrozenItem();
            return;
        }

        var bounds = frozen.Bounds;
        var toPanel = canvas.TransformToVisual(this);
        var x = Math.Clamp(_hoverPoint.X, bounds.Left, bounds.Right);
        var isBelow = toPanel.TransformPoint(new Point(x, bounds.Top)).Y < HoverBarReserve;
        // The zone is the danmaku plus the strip the pointer crosses to reach the bar.
        _hoverZone = new Rect(
            bounds.Left - 8,
            bounds.Top - (isBelow ? 6 : 16),
            bounds.Width + 16,
            bounds.Height + 22);

        _hoverId = hit.SourceId;
        _hoverText = hit.Text ?? string.Empty;
        _hoverLeftAt = 0;
        _hoverPointerInBar = false;
        _hoverRedrawVersion = _redrawVersion;
        _hoverGeneration = player.MediaGeneration;
        _hoverProgress = _lastProgress;
        _hoverLikes = null;
        _hoverLiked = false;
        _hoverLikeBusy = false;
        var session = ++_hoverSession;

        var canLike = player.CanLikeDanmaku(out var isSignedIn);
        HoverLikeHost.Visibility = canLike || !isSignedIn ? Visibility.Visible : Visibility.Collapsed;
        HoverLikeButton.IsEnabled = canLike;
        ToolTipService.SetToolTip(HoverLikeHost, isSignedIn ? null : ResourceToolkit.GetLocalizedString(StringNames.DanmakuLikeNeedSignIn));
        UpdateHoverLikeVisual();

        // The bar opens right above the pointer (below for the top rows), centred on it,
        // so a long danmaku does not send the pointer on a trip to its middle.
        var anchor = toPanel.TransformPoint(new Point(x, isBelow ? bounds.Bottom + HoverBarGap : bounds.Top - HoverBarGap));
        try
        {
            // A press on the player while the bar is open must still pause or open the menu.
            HoverFlyout.OverlayInputPassThroughElement = _hoverOverlay;
            HoverFlyout.ShowAt(this, new FlyoutShowOptions
            {
                Position = anchor,
                Placement = isBelow ? FlyoutPlacementMode.Bottom : FlyoutPlacementMode.Top,
                // Transient: no focus change, and a press outside still reaches the player.
                ShowMode = FlyoutShowMode.Transient,
            });
        }
        catch (Exception)
        {
            DismissHover();
            return;
        }

        if (canLike) _ = LoadHoverLikeStateAsync(session, hit.SourceId);
    }

    private void DismissHover()
    {
        _hoverCandidateId = null;
        if (_hoverId is null) return;
        _hoverId = null;
        ++_hoverSession;
        _hoverPointerInBar = false;
        _hoverLeftAt = 0;
        _danmakuMaster?.ReleaseFrozenItem();
        if (HoverFlyout.IsOpen) HoverFlyout.Hide();
    }

    private void OnHoverCopyClick(object sender, RoutedEventArgs e)
    {
        if (_hoverId is not { } id) return;
        // Merged duplicates are drawn with a count suffix; copy what was actually sent.
        var content = ViewModel?.GetCachedDanmakus().FirstOrDefault(item => item is not null && item.Id == id)?.Content;
        _playerViewModel?.CopyDanmakuText(string.IsNullOrEmpty(content) ? _hoverText : content);
        DismissHover();
    }

    private async void OnHoverLikeClick(object sender, RoutedEventArgs e)
    {
        if (_hoverLikeBusy || _hoverId is not { } id || _playerViewModel is not { } player) return;
        var session = _hoverSession;
        var previousLiked = _hoverLiked;
        var previousLikes = _hoverLikes;
        var isLike = !previousLiked;

        // Show the result at once; it is rolled back if the request fails.
        _hoverLikeBusy = true;
        _hoverLiked = isLike;
        if (previousLikes is { } count) _hoverLikes = Math.Max(0, count + (isLike ? 1 : -1));
        UpdateHoverLikeVisual();

        var succeeded = await player.SetDanmakuLikeAsync(id, isLike);
        if (session != _hoverSession) return;
        _hoverLikeBusy = false;
        if (!succeeded)
        {
            _hoverLiked = previousLiked;
            _hoverLikes = previousLikes;
            UpdateHoverLikeVisual();
        }
        else if (_hoverLikes is null)
        {
            _ = LoadHoverLikeStateAsync(session, id);
        }
    }

    private async Task LoadHoverLikeStateAsync(int session, string id)
    {
        if (_playerViewModel is not { } player) return;
        var state = await player.GetDanmakuLikeStateAsync(id);
        if (state is not { } value || session != _hoverSession || _hoverLikeBusy) return;
        _hoverLikes = value.Likes;
        _hoverLiked = value.IsLiked;
        UpdateHoverLikeVisual();
    }

    private void UpdateHoverLikeVisual()
    {
        HoverLikeIcon.Visibility = _hoverLiked ? Visibility.Collapsed : Visibility.Visible;
        HoverLikedIcon.Visibility = _hoverLiked ? Visibility.Visible : Visibility.Collapsed;
        var hasCount = _hoverLikes is > 0;
        HoverLikeCount.Text = hasCount ? AppToolkit.FormatCount(_hoverLikes!.Value) : string.Empty;
        HoverLikeCount.Visibility = hasCount ? Visibility.Visible : Visibility.Collapsed;
        var name = ResourceToolkit.GetLocalizedString(_hoverLiked ? StringNames.DanmakuUnlike : StringNames.Like);
        AutomationProperties.SetName(HoverLikeButton, name);
        ToolTipService.SetToolTip(HoverLikeButton, name);
    }

    // Same rule as the player's interactive surface: the event must come from the bare
    // video area, not from a button, slider or any other control laid over it.
    private static bool IsHoverSurface(object source)
    {
        for (var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is PlayerInteractivePanel) return true;
            if (current is Control or Microsoft.UI.Xaml.Documents.Hyperlink) return false;
        }

        return false;
    }
}
