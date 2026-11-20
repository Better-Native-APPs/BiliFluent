// Copyright (c) BiliFluent. All rights reserved.

using Microsoft.UI.Input;
using Microsoft.UI.Windowing;

namespace BiliCopilot.UI.Controls.Core;

/// <summary>
/// The built-in <see cref="TitleBar"/> of the standalone player window, with the caption-button insets kept current.
/// The built-in control copies <see cref="AppWindowTitleBar.LeftInset"/> and <see cref="AppWindowTitleBar.RightInset"/>
/// into its two padding columns once, when the template is applied, and as raw numbers. The system reports them in
/// physical pixels and changes them with the presenter and the monitor scale, so this class rewrites the columns in
/// view pixels on every measure pass. Everything else is the built-in control and its default template.
/// </summary>
public sealed partial class PlayerTitleBar : TitleBar
{
    // Part names of the built-in template.
    private const string LeftPaddingColumnName = "LeftPaddingColumn";
    private const string RightPaddingColumnName = "RightPaddingColumn";
    private const double WidthTolerance = 0.01;

    private ColumnDefinition? _leftPaddingColumn;
    private ColumnDefinition? _rightPaddingColumn;
    private AppWindow? _appWindow;
    private XamlRoot? _observedRoot;
    private double _observedScale;
    private bool _regionsLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayerTitleBar"/> class.
    /// </summary>
    public PlayerTitleBar()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Re-reads the caption insets and asks the built-in control to recompute its passthrough regions. It is for
    /// changes that move the buttons without resizing the title bar: the right-hand group growing or shrinking, a
    /// presenter change, a monitor scale change. The built-in control recomputes by itself only when it is resized.
    /// </summary>
    internal void RefreshRegions()
    {
        // A collapsed header has no bounds to register; it is measured and resized again when it comes back.
        if (!IsLoaded || XamlRoot is null || ActualWidth <= 0) return;
        ApplyCaptionInsets();
        RecomputeDragRegions();
    }

    /// <summary>
    /// Debug builds only. Logs once what the window really has registered as passthrough regions, and whether the
    /// title lies inside one of them: it does not when the built-in control looks for the interactive elements inside
    /// its content, and it does when the control still treats the whole content as interactive.
    /// </summary>
    /// <param name="titleProbe">The element that shows the title. Its text is never logged.</param>
    [System.Diagnostics.Conditional("DEBUG")]
    internal void LogPassthroughRegionsOnce(FrameworkElement? titleProbe)
    {
        if (_regionsLogged || titleProbe is null || !IsLoaded || XamlRoot is not { } root) return;
        if (ActualWidth <= 0 || titleProbe.ActualWidth <= 0) return;
        _regionsLogged = true;
        try
        {
            var scale = root.RasterizationScale;
            var source = InputNonClientPointerSource.GetForWindowId(root.ContentIslandEnvironment.AppWindowId);
            var rects = source.GetRegionRects(NonClientRegionKind.Passthrough);

            // The built-in control registers root-relative view pixels multiplied by the scale; probe the same way.
            var center = titleProbe.TransformToVisual(null).TransformPoint(new Point(titleProbe.ActualWidth / 2, titleProbe.ActualHeight / 2));
            var x = center.X * scale;
            var y = center.Y * scale;
            var inside = rects.Any(rect => x >= rect.X && x < rect.X + rect.Width && y >= rect.Y && y < rect.Y + rect.Height);
            Serilog.Log.Information(
                "PlayerWindow | titlebar regions passthrough={Count} title-passthrough={Inside:l} scale={Scale} right-inset={Inset} right-padding={Padding}",
                rects.Length,
                inside ? "true" : "false",
                scale,
                _appWindow?.TitleBar?.RightInset,
                _rightPaddingColumn?.Width.Value);
        }
        catch (Exception ex)
        {
            Serilog.Log.Information("PlayerWindow | titlebar regions read failed error={Error:l}", ex.GetType().Name);
        }
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _leftPaddingColumn = GetTemplateChild(LeftPaddingColumnName) as ColumnDefinition;
        _rightPaddingColumn = GetTemplateChild(RightPaddingColumnName) as ColumnDefinition;
        if (_leftPaddingColumn is null || _rightPaddingColumn is null)
        {
            // The template no longer has these parts: leave the padding to the built-in control.
            _leftPaddingColumn = null;
            _rightPaddingColumn = null;
            LogMissingPaddingColumns();
        }
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        // Written before the template is measured: the layout pass that resizes the title bar already uses the current
        // insets, so the built-in size-changed handler computes the passthrough regions from the final positions.
        ApplyCaptionInsets();
        return base.MeasureOverride(availableSize);
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void LogMissingPaddingColumns()
        => Serilog.Log.Information("PlayerWindow | titlebar padding columns missing, caption insets left to the built-in control");

    private static void SetWidth(ColumnDefinition column, double pixels)
    {
        // An equal value is never rewritten: once the built-in control writes view pixels too, the two writers must not
        // keep invalidating each other's layout.
        var current = column.Width;
        if (current.IsAbsolute && Math.Abs(current.Value - pixels) < WidthTolerance) return;
        column.Width = new GridLength(pixels);
    }

    private void ApplyCaptionInsets()
    {
        if (_leftPaddingColumn is not { } leftColumn || _rightPaddingColumn is not { } rightColumn) return;
        if (XamlRoot is not { } root) return;
        try
        {
            var scale = root.RasterizationScale;
            if (scale <= 0) return;
            _appWindow ??= root.ContentIslandEnvironment is { } environment
                ? AppWindow.GetFromWindowId(environment.AppWindowId)
                : null;
            if (_appWindow?.TitleBar is not { } caption) return;

            // Same sides as the built-in control: the two insets swap in a right-to-left layout.
            var isLeftToRight = FlowDirection == FlowDirection.LeftToRight;
            SetWidth(leftColumn, (isLeftToRight ? caption.LeftInset : caption.RightInset) / scale);
            SetWidth(rightColumn, (isLeftToRight ? caption.RightInset : caption.LeftInset) / scale);
        }
        catch (Exception)
        {
            // The window is going away. This runs inside layout, so the columns keep their last value; the cached
            // window is dropped and looked up again should the title bar be measured once more.
            _appWindow = null;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StopObservingRoot();
        if (XamlRoot is { } root)
        {
            _observedRoot = root;
            _observedScale = root.RasterizationScale;
            root.Changed += OnXamlRootChanged;
        }

        // The insets may have changed while the header was out of the tree.
        InvalidateMeasure();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopObservingRoot();
        _appWindow = null;
    }

    private void StopObservingRoot()
    {
        if (_observedRoot is { } root) root.Changed -= OnXamlRootChanged;
        _observedRoot = null;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        // Moving to a monitor with another scale changes the insets and the physical bounds of every button, yet the
        // title bar can keep its size in view pixels, in which case the built-in control would not recompute.
        var scale = sender.RasterizationScale;
        if (scale == _observedScale) return;
        _observedScale = scale;
        RefreshRegions();
    }
}
