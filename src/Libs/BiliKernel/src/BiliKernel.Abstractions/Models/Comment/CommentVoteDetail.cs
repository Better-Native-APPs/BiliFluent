// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Comment;

/// <summary>
/// A poll in full: what is asked, the options, the results and what the signed-in user chose.
/// </summary>
public sealed class CommentVoteDetail
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CommentVoteDetail"/> class.
    /// </summary>
    /// <param name="id">The poll identifier.</param>
    /// <param name="title">The poll title.</param>
    /// <param name="options">The options, in the order the server lists them.</param>
    public CommentVoteDetail(string id, string title, IReadOnlyList<CommentVoteOption> options)
    {
        Id = id;
        Title = title;
        Options = options;
    }

    /// <summary>
    /// Gets the poll identifier.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the poll title.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the options, in the order the server lists them.
    /// </summary>
    public IReadOnlyList<CommentVoteOption> Options { get; }

    /// <summary>
    /// Gets or sets the introduction written by the publisher, if any.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the time voting closes. Null when the server did not send one.
    /// </summary>
    public DateTimeOffset? EndTime { get; set; }

    /// <summary>
    /// Gets or sets how many options one user may choose. 1 for a single-choice poll.
    /// </summary>
    public int MaxChoiceCount { get; set; } = 1;

    /// <summary>
    /// Gets or sets the number of users who have voted.
    /// </summary>
    public long ParticipantCount { get; set; }

    /// <summary>
    /// Gets or sets the indexes of the options the signed-in user chose. Empty when not signed in or not voted yet.
    /// </summary>
    public IReadOnlyList<int> MyVotes { get; set; } = Array.Empty<int>();

    /// <summary>
    /// Gets or sets the identifier of the user who published the poll.
    /// </summary>
    public string? PublisherId { get; set; }

    /// <summary>
    /// Gets or sets the name of the user who published the poll.
    /// </summary>
    public string? PublisherName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether only paying supporters of the publisher may vote.
    /// </summary>
    public bool IsSupporterOnly { get; set; }
}
