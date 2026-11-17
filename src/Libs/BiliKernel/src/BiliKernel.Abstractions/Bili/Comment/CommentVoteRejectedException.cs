// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Bili.Comment;

/// <summary>
/// The server received a vote and turned it down, for example because the poll has closed.
/// </summary>
public sealed class CommentVoteRejectedException : KernelException
{
    private const int SupporterOnlyCode = 5200011;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommentVoteRejectedException"/> class.
    /// </summary>
    public CommentVoteRejectedException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CommentVoteRejectedException"/> class with an error message.
    /// </summary>
    /// <param name="message">The message describing the failure.</param>
    public CommentVoteRejectedException(string? message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CommentVoteRejectedException"/> class with an error message and its cause.
    /// </summary>
    /// <param name="message">The message describing the failure.</param>
    /// <param name="innerException">The exception that caused the failure.</param>
    public CommentVoteRejectedException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CommentVoteRejectedException"/> class from a server response.
    /// </summary>
    /// <param name="code">The response code.</param>
    /// <param name="reason">The reason in the server's own words, if it gave one.</param>
    /// <param name="innerException">The exception that caused the failure.</param>
    public CommentVoteRejectedException(int code, string? reason, Exception? innerException)
        : base(string.IsNullOrEmpty(reason) ? $"The vote was rejected with code {code}." : reason, innerException)
    {
        Code = code;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason!.Trim();
    }

    /// <summary>
    /// Gets the response code.
    /// </summary>
    public int Code { get; }

    /// <summary>
    /// Gets the reason in the server's own words. Null when it gave none.
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// Gets a value indicating whether the vote was turned down because the poll is for paying supporters only.
    /// </summary>
    public bool IsSupporterOnly => Code == SupporterOnlyCode;
}
