// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;

namespace BiliCopilot.UI.Controls.Components;

/// <summary>
/// Watched mark laid over the video cover of a dynamic card: the state and a bar along the bottom edge.
/// Collapsed when there is no mark, so an unwatched card looks exactly as before.
/// </summary>
public sealed partial class WatchMarkOverlay : UserControl
{
    /// <summary><see cref="Mark"/> dependency property.</summary>
    /// <remarks>
    /// Typed as <see cref="object"/> so that both x:Bind and the template bindings of the card styles can set it without a type conversion.
    /// </remarks>
    public static readonly DependencyProperty MarkProperty =
        DependencyProperty.Register(nameof(Mark), typeof(object), typeof(WatchMarkOverlay), new PropertyMetadata(default, OnMarkChanged));

    /// <summary><see cref="BadgeMargin"/> dependency property.</summary>
    public static readonly DependencyProperty BadgeMarginProperty =
        DependencyProperty.Register(nameof(BadgeMargin), typeof(Thickness), typeof(WatchMarkOverlay), new PropertyMetadata(new Thickness(8), OnBadgeMarginChanged));

    /// <summary><see cref="BarHeight"/> dependency property.</summary>
    public static readonly DependencyProperty BarHeightProperty =
        DependencyProperty.Register(nameof(BarHeight), typeof(double), typeof(WatchMarkOverlay), new PropertyMetadata(5d, OnBarHeightChanged));

    /// <summary><see cref="LabelAlignment"/> dependency property.</summary>
    public static readonly DependencyProperty LabelAlignmentProperty =
        DependencyProperty.Register(nameof(LabelAlignment), typeof(HorizontalAlignment), typeof(WatchMarkOverlay), new PropertyMetadata(HorizontalAlignment.Left, OnLabelAlignmentChanged));

    // The row of the label: as tall as the duration badge of the host card.
    private const double RowHeight = 24d;

    // How far the fade rises above the row before it is gone.
    private const double FadeRise = 8d;

    // The icon and the gap to the text.
    private const double IconRoom = 21d;

    // Kept free on the same row for the duration badge of the host card, up to an hour-long one, and the gap to it.
    private const double DurationRoom = 64d;

    /// <summary>Initializes a new instance of the <see cref="WatchMarkOverlay"/> class.</summary>
    public WatchMarkOverlay() => InitializeComponent();

    /// <summary>The <see cref="WatchMark"/> to show. Anything else hides the overlay.</summary>
    public object? Mark
    {
        get => GetValue(MarkProperty);
        set => SetValue(MarkProperty, value);
    }

    /// <summary>Distance of the label from the cover edges. Match it to the duration badge of the host card, so both sit on one line.</summary>
    public Thickness BadgeMargin
    {
        get => (Thickness)GetValue(BadgeMarginProperty);
        set => SetValue(BadgeMarginProperty, value);
    }

    /// <summary>
    /// Thickness of the bar. Keep it within the gap between the cover edge and the duration badge of the host card,
    /// so the bar never runs into that badge.
    /// </summary>
    public double BarHeight
    {
        get => (double)GetValue(BarHeightProperty);
        set => SetValue(BarHeightProperty, value);
    }

    /// <summary>
    /// The side of the cover the label sits on. Use the side the duration badge of the host card leaves free.
    /// </summary>
    public HorizontalAlignment LabelAlignment
    {
        get => (HorizontalAlignment)GetValue(LabelAlignmentProperty);
        set => SetValue(LabelAlignmentProperty, value);
    }

    private static void OnMarkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((WatchMarkOverlay)d).Apply(e.NewValue as WatchMark);

    private static void OnBadgeMarginChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // The label does not exist until the first mark arrives; Apply picks the margin up then.
        var overlay = (WatchMarkOverlay)d;
        if (overlay.Root is not null)
        {
            overlay.ApplyMetrics();
        }
    }

    private static void OnBarHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var overlay = (WatchMarkOverlay)d;
        if (overlay.Root is not null)
        {
            overlay.ApplyMetrics();
        }
    }

    private static void OnLabelAlignmentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var overlay = (WatchMarkOverlay)d;
        if (overlay.Root is not null)
        {
            overlay.ApplyMetrics();
        }
    }

    private void ApplyMetrics()
    {
        StatusPanel.Margin = BadgeMargin;
        StatusPanel.HorizontalAlignment = LabelAlignment;
        Fade.Height = BadgeMargin.Bottom + RowHeight + FadeRise;
        BarTrack.Height = BarHeight;
        Bar.MinHeight = BarHeight;
        FitLabel();
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) => FitLabel();

    /// <summary>
    /// Shows the text only when it fits beside the duration badge of the host card. On a cover too narrow for both, the icon stands alone
    /// instead of a trimmed text. The cover gives the overlay its size, so showing or hiding the text never changes that size.
    /// </summary>
    private void FitLabel()
    {
        StatusText.Visibility = Visibility.Visible;
        var width = Root.ActualWidth;
        if (width <= 0)
        {
            // Not laid out yet. The size change that follows settles it.
            return;
        }

        StatusText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var needed = BadgeMargin.Left + BadgeMargin.Right + IconRoom + StatusText.DesiredSize.Width + DurationRoom;
        if (needed > width)
        {
            StatusText.Visibility = Visibility.Collapsed;
        }
    }

    private void Apply(WatchMark? mark)
    {
        if (mark is null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        if (Root is null)
        {
            _ = FindName(nameof(Root));
            Root!.SizeChanged += OnRootSizeChanged;
        }

        // One icon per state. The text is set before the metrics, which decide whether it fits.
        FinishedIcon.Visibility = mark.IsFinished ? Visibility.Visible : Visibility.Collapsed;
        PartialIcon.Visibility = mark.IsFinished ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = mark.Text;
        ApplyMetrics();
        Bar.Value = mark.Ratio;
        BarTrack.Visibility = mark.Ratio > 0 ? Visibility.Visible : Visibility.Collapsed;
        Visibility = Visibility.Visible;
    }
}
