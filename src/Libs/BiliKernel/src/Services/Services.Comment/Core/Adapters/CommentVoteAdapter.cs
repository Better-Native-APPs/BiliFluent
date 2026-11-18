// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.RegularExpressions;
using Richasy.BiliKernel.Models.Comment;

namespace Richasy.BiliKernel.Services.Comment.Core.Adapters;

internal static class CommentVoteAdapter
{
    // The message carries the vote as an inline {vote:ID} placeholder; the vote itself sits in Content.Vote.
    private static readonly Regex _placeholderRegex = new(@"\{vote:([0-9]+)\}", RegexOptions.CultureInvariant);

    /// <summary>
    /// Removes {vote:ID} placeholders so the body never shows them as literal text.
    /// </summary>
    public static string RemoveVotePlaceholders(string message)
    {
        if (string.IsNullOrEmpty(message) || message.IndexOf("{vote:", StringComparison.Ordinal) < 0)
        {
            return message;
        }

        var result = _placeholderRegex.Replace(message, string.Empty);

        // Only tidy the edges when a placeholder was really taken out, other text stays untouched.
        return result.Length == message.Length ? message : result.Trim();
    }

    /// <summary>
    /// Reads the vote attached to a comment. Falls back to the ID inside the placeholder
    /// when the server did not send the vote details.
    /// </summary>
    public static CommentVote? ToCommentVote(this Bilibili.Main.Community.Reply.V1.Content content)
    {
        var vote = content.Vote;
        if (vote is { Id: > 0 })
        {
            var title = string.IsNullOrWhiteSpace(vote.Title) ? null : vote.Title.Trim();
            return new CommentVote(vote.Id.ToString(CultureInfo.InvariantCulture), title, vote.Count);
        }

        var message = content.Message;
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var match = _placeholderRegex.Match(message);
        return match.Success ? new CommentVote(match.Groups[1].Value) : null;
    }
}
