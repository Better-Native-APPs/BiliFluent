// Copyright (c) BiliFluent. All rights reserved.

namespace BiliCopilot.UI.Controls.Core;

/// <summary>
/// The bubble shown over the progress bar while it is hovered or dragged: chapter title and time.
/// </summary>
public sealed partial class ChapterPreviewTip : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ChapterPreviewTip"/> class.
    /// </summary>
    public ChapterPreviewTip() => InitializeComponent();

    /// <summary>The time format every text of the transport bars uses.</summary>
    internal static string FormatTime(double seconds)
        => TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Max(0, seconds) : 0).ToString(@"hh\:mm\:ss");

    /// <summary>Sets what the bubble says. A position outside every chapter shows the time alone.</summary>
    internal void Update(string? title, double seconds)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            TitleBlock.Visibility = Visibility.Collapsed;
        }
        else
        {
            if (TitleBlock.Text != title) TitleBlock.Text = title;
            TitleBlock.Visibility = Visibility.Visible;
        }

        var time = FormatTime(seconds);
        if (TimeBlock.Text != time) TimeBlock.Text = time;
    }
}
