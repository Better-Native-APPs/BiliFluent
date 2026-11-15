// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Media;

/// <summary>
/// How far the signed-in account has watched a video, as reported by the server alongside a card.
/// </summary>
/// <remarks>
/// This type is the single place that interprets the raw progress fields.
/// Their unit and their "finished" value are not documented, so every assumption is listed on
/// <see cref="FromPartProgress(bool, long, long, long)"/> and can be corrected there once a real sample is available.
/// </remarks>
public sealed class VideoWatchProgress
{
    /// <summary>
    /// Watching at least this share of the part counts as finished.
    /// </summary>
    private const double FinishedRatio = 0.97;

    /// <summary>
    /// Stopping within this many seconds of the end counts as finished.
    /// </summary>
    private const long FinishedRemainingSeconds = 5;

    /// <summary>
    /// The remaining-seconds rule only applies past this share, so that very short videos are not finished by default.
    /// </summary>
    private const double FinishedRemainingMinRatio = 0.8;

    /// <summary>
    /// A part reported as this many times longer than the whole video cannot be in seconds.
    /// </summary>
    private const long MillisecondSuspectFactor = 20;

    private VideoWatchProgress(bool isFinished, long position, long duration, double ratio)
    {
        IsFinished = isFinished;
        Position = position;
        Duration = duration;
        Ratio = ratio;
    }

    /// <summary>
    /// Whether the video has been watched to the end.
    /// </summary>
    public bool IsFinished { get; }

    /// <summary>
    /// Last watched position in seconds. Zero when the server only says the video is finished.
    /// </summary>
    public long Position { get; }

    /// <summary>
    /// Duration in seconds of the part the position belongs to. Zero when unknown.
    /// </summary>
    public long Duration { get; }

    /// <summary>
    /// Watched share between 0 and 1. It is 1 when finished and 0 when the duration is unknown.
    /// </summary>
    public double Ratio { get; }

    /// <summary>
    /// Interprets the progress fields of a dynamic archive card (<c>show_progress</c>, <c>part_progress</c>, <c>part_duration</c>).
    /// </summary>
    /// <param name="showProgress">The server's flag that there is progress to show.</param>
    /// <param name="partProgress">Last watched position of the part.</param>
    /// <param name="partDuration">Duration of that part.</param>
    /// <param name="totalDuration">Duration of the whole video in seconds, used when the part duration is missing.</param>
    /// <returns>The watch progress, or <see langword="null"/> when the video carries no watched mark.</returns>
    /// <remarks>
    /// Assumptions (not confirmed against a real response):
    /// <list type="number">
    /// <item>No flag or a zero position means the video has not been watched.</item>
    /// <item>A negative position means finished, as <c>-1</c> does in the history and view interfaces.</item>
    /// <item>Position and part duration are in seconds like the card's own duration. They are read as milliseconds only when they are implausibly large for seconds.</item>
    /// <item>The position belongs to the part whose duration is given, so the share is position / part duration.</item>
    /// <item>Reaching the end, 97%, or the last 5 seconds (past 80%) counts as finished.</item>
    /// </list>
    /// </remarks>
    public static VideoWatchProgress? FromPartProgress(bool showProgress, long partProgress, long partDuration, long totalDuration)
    {
        if (!showProgress || partProgress == 0)
        {
            return null;
        }

        if (partProgress < 0)
        {
            return new VideoWatchProgress(true, 0, Math.Max(partDuration, 0), 1d);
        }

        if (totalDuration > 0 && Math.Max(partDuration, partProgress) > totalDuration * MillisecondSuspectFactor)
        {
            partDuration /= 1000;
            partProgress /= 1000;
            if (partProgress == 0)
            {
                return null;
            }
        }

        var duration = partDuration > 0 ? partDuration : Math.Max(totalDuration, 0);
        if (duration == 0)
        {
            return new VideoWatchProgress(false, partProgress, 0, 0d);
        }

        var ratio = Math.Min(1d, partProgress / (double)duration);
        var remaining = duration - partProgress;
        var isFinished = remaining <= 0
            || ratio >= FinishedRatio
            || (remaining <= FinishedRemainingSeconds && ratio >= FinishedRemainingMinRatio);

        return isFinished
            ? new VideoWatchProgress(true, duration, duration, 1d)
            : new VideoWatchProgress(false, partProgress, duration, ratio);
    }
}
