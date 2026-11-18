// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Media;

/// <summary>
/// How far the signed-in account has watched a video.
/// </summary>
/// <remarks>
/// This type is the single place that decides between "not watched", "partly watched" and "finished".
/// There are two sources: the watch history (<see cref="FromHistory(long, long)"/>), whose values are known,
/// and the progress fields of a dynamic card (<see cref="FromPartProgress(bool, long, long, long)"/>),
/// which the server has so far been seen to leave empty.
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
    /// Interprets a watch history record, or a playback position of the same shape.
    /// </summary>
    /// <param name="progress">Last watched second. 0 is not watched and -1 is finished.</param>
    /// <param name="duration">Duration in seconds of the part that was watched. 0 when the record does not carry it.</param>
    /// <returns>The watch progress, or <see langword="null"/> when the video has not been watched.</returns>
    /// <remarks>
    /// The history interface reports seconds, never reports a progress past the duration, and marks a finished video
    /// either with -1 or with a progress equal to the duration. A record without a duration only yields the position.
    /// A video left within the last few seconds also counts as finished here, which is more lenient than the server.
    /// </remarks>
    public static VideoWatchProgress? FromHistory(long progress, long duration)
    {
        if (progress == 0)
        {
            return null;
        }

        return progress < 0
            ? new VideoWatchProgress(true, 0, Math.Max(duration, 0), 1d)
            : Evaluate(progress, Math.Max(duration, 0));
    }

    /// <summary>
    /// Interprets the progress fields of a dynamic archive card (<c>show_progress</c>, <c>part_progress</c>, <c>part_duration</c>).
    /// </summary>
    /// <param name="showProgress">The server's flag that there is progress to show.</param>
    /// <param name="partProgress">Last watched position of the part.</param>
    /// <param name="partDuration">Duration of that part.</param>
    /// <param name="totalDuration">Duration of the whole video in seconds, used when the part duration is missing.</param>
    /// <returns>The watch progress, or <see langword="null"/> when the video carries no watched mark.</returns>
    /// <remarks>
    /// The server has not been seen to fill these fields, so the reading below is an assumption kept for the day it does:
    /// <list type="number">
    /// <item>No flag or a zero position means the video has not been watched.</item>
    /// <item>A negative position means finished, as -1 does in the history interface.</item>
    /// <item>Position and part duration are in seconds like the card's own duration. They are read as milliseconds only when they are implausibly large for seconds.</item>
    /// <item>The position belongs to the part whose duration is given, so the share is position / part duration.</item>
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

        return Evaluate(partProgress, partDuration > 0 ? partDuration : Math.Max(totalDuration, 0));
    }

    /// <summary>
    /// Decides between partly watched and finished for a positive position.
    /// Reaching the end, 97%, or the last 5 seconds (past 80%) counts as finished.
    /// </summary>
    private static VideoWatchProgress Evaluate(long position, long duration)
    {
        if (duration == 0)
        {
            return new VideoWatchProgress(false, position, 0, 0d);
        }

        var ratio = Math.Min(1d, position / (double)duration);
        var remaining = duration - position;
        var isFinished = remaining <= 0
            || ratio >= FinishedRatio
            || (remaining <= FinishedRemainingSeconds && ratio >= FinishedRemainingMinRatio);

        return isFinished
            ? new VideoWatchProgress(true, duration, duration, 1d)
            : new VideoWatchProgress(false, position, duration, ratio);
    }
}
