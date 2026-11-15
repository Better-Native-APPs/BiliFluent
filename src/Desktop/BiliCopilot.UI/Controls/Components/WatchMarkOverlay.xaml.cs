// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;

namespace BiliCopilot.UI.Controls.Components;

/// <summary>
/// Watched mark laid over the video cover of a dynamic card: a corner label and a thin bar along the bottom edge.
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

    private static void OnMarkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((WatchMarkOverlay)d).Apply(e.NewValue as WatchMark);

    private static void OnBadgeMarginChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // The label does not exist until the first mark arrives; Apply picks the margin up then.
        if (((WatchMarkOverlay)d).Badge is Border badge)
        {
            badge.Margin = (Thickness)e.NewValue;
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
        }

        Badge.Margin = BadgeMargin;
        Label.Text = mark.Text;
        FinishedIcon.Visibility = mark.IsFinished ? Visibility.Visible : Visibility.Collapsed;
        Bar.Value = mark.Ratio;
        BarTrack.Visibility = mark.Ratio > 0 ? Visibility.Visible : Visibility.Collapsed;
        Visibility = Visibility.Visible;
    }
}
