// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Comment;

/// <summary>
/// The poll attached to a comment, as far as the comment itself describes it.
/// </summary>
public sealed class CommentVote
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CommentVote"/> class.
    /// </summary>
    /// <param name="id">The poll identifier.</param>
    /// <param name="title">The poll title, or null when the server did not send it.</param>
    /// <param name="participantCount">The number of participants, or null when the server did not send it.</param>
    public CommentVote(string id, string? title = default, long? participantCount = default)
    {
        Id = id;
        Title = title;
        ParticipantCount = participantCount;
    }

    /// <summary>
    /// Gets the poll identifier.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the poll title. Null when only the placeholder in the comment text was available.
    /// </summary>
    public string? Title { get; }

    /// <summary>
    /// Gets the number of participants. Null when only the placeholder in the comment text was available.
    /// </summary>
    public long? ParticipantCount { get; }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CommentVote vote && Id == vote.Id;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Id);
}
