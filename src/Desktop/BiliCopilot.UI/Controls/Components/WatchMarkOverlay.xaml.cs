// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;

namespace BiliCopilot.UI.Controls.Components;

/// <summary>
/// Watched mark laid over the video cover of a dynamic card: a corner label and a bar along the bottom edge.
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

    /// <summary>Initializes a new instance of the <see cref="WatchMarkOverlay"/> class.</summary>
    public WatchMarkOverlay() => InitializeComponent();

    /// <summary>The <see cref="WatchMark"/> to show. Anything else hides the overlay.</summary>
    public object? Mark
    {
        get => GetValue(MarkProperty);
        set => SetValue(MarkProperty, value);
    }

    /// <summary>Distance of the label from the cover edges. Match it to the duration badge of the host card.</summary>
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

    private static void OnMarkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((WatchMarkOverlay)d).Apply(e.NewValue as WatchMark);

    private static void OnBadgeMarginChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // The labels do not exist until the first mark arrives; Apply picks the margin up then.
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

    private void ApplyMetrics()
    {
        FinishedBadge.Margin = BadgeMargin;
        PartialBadge.Margin = BadgeMargin;
        BarTrack.Height = BarHeight;
        Bar.MinHeight = BarHeight;
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
        }

        ApplyMetrics();

        // One label per state, each fully described in XAML. Only the one in use is shown.
        FinishedBadge.Visibility = mark.IsFinished ? Visibility.Visible : Visibility.Collapsed;
        PartialBadge.Visibility = mark.IsFinished ? Visibility.Collapsed : Visibility.Visible;
        (mark.IsFinished ? FinishedLabel : PartialLabel).Text = mark.Text;
        Bar.Value = mark.Ratio;
        BarTrack.Visibility = mark.Ratio > 0 ? Visibility.Visible : Visibility.Collapsed;
        Visibility = Visibility.Visible;
    }
}
