// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Comment;

/// <summary>
/// One option of a poll.
/// </summary>
public sealed class CommentVoteOption
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CommentVoteOption"/> class.
    /// </summary>
    /// <param name="index">The option index as the server numbers it, starting at 1.</param>
    /// <param name="text">The option text.</param>
    /// <param name="count">The number of votes the option has received.</param>
    /// <param name="image">The picture of the option, for picture polls.</param>
    public CommentVoteOption(int index, string text, long count = 0, Uri? image = default)
    {
        Index = index;
        Text = text;
        Count = count;
        Image = image;
    }

    /// <summary>
    /// Gets the option index as the server numbers it, starting at 1. This is the value a vote is sent with.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Gets the option text.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Gets the number of votes the option has received.
    /// </summary>
    public long Count { get; }

    /// <summary>
    /// Gets the picture of the option. Null for text polls.
    /// </summary>
    public Uri? Image { get; }
}
