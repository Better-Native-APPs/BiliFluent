// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// What a dynamic card shows on its cover for a video the account has already watched.
/// </summary>
public sealed class WatchMark
{
    /// <summary>
    /// Keeps a few watched seconds of a long video visible on the cover bar.
    /// </summary>
    private const double MinVisibleRatio = 0.04;

    private WatchMark(bool isFinished, double ratio, string text)
    {
        IsFinished = isFinished;
        Ratio = ratio;
        Text = text;
    }

    /// <summary>
    /// Whether the video has been watched to the end.
    /// </summary>
    public bool IsFinished { get; }

    /// <summary>
    /// Bar fill between 0 and 1. Zero means the share is unknown and the bar is not shown.
    /// </summary>
    public double Ratio { get; }

    /// <summary>
    /// Label text, such as "已看完" or "看到 02:15".
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Creates the mark for a video, or <see langword="null"/> when it has not been watched.
    /// </summary>
    public static WatchMark? Create(VideoInformation? video)
        => Create(video?.GetExtensionIfNotNull<VideoWatchProgress>(VideoExtensionDataId.WatchProgress));

    /// <summary>
    /// Creates the mark for an episode, or <see langword="null"/> when it has not been watched.
    /// </summary>
    public static WatchMark? Create(EpisodeInformation? episode)
        => Create(episode?.GetExtensionIfNotNull<VideoWatchProgress>(EpisodeExtensionDataId.WatchProgress));

    private static WatchMark? Create(VideoWatchProgress? progress)
    {
        if (progress is null)
        {
            return null;
        }

        if (progress.IsFinished)
        {
            return new WatchMark(true, 1d, ResourceToolkit.GetLocalizedString(StringNames.WatchFinished));
        }

        var position = AppToolkit.FormatDuration(TimeSpan.FromSeconds(progress.Position));
        var text = string.Format(ResourceToolkit.GetLocalizedString(StringNames.WatchedToTemplate), position);
        var ratio = progress.Ratio > 0 ? Math.Max(progress.Ratio, MinVisibleRatio) : 0d;
        return new WatchMark(false, ratio, text);
    }
}
