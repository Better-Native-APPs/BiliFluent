// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

using Richasy.BiliKernel.Models.Comment;
using System.Globalization;
using System.Text.Json;

namespace Richasy.BiliKernel.Services.Comment.Core;

/// <summary>
/// Reads the poll JSON of /x/vote/vote_info and /x/vote/do_vote. The server leaves out fields that are zero or empty.
/// </summary>
internal static class CommentVoteParser
{
    // DateTimeOffset.FromUnixTimeSeconds accepts up to 9999-12-31.
    private const long MaxUnixSeconds = 253402300799;

    /// <summary>
    /// Reads a poll from the "data" object of a response. Returns null when it carries no usable poll,
    /// which is how the server answers for a deleted one.
    /// </summary>
    public static CommentVoteDetail? ParseDetail(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("vote_info", out var info) || info.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetInt64(info, "vote_id");
        var title = GetString(info, "title");
        var options = ParseOptions(info);
        if (id <= 0 || string.IsNullOrWhiteSpace(title) || options.Count == 0)
        {
            return null;
        }

        var endTime = GetInt64(info, "end_time");
        var publisher = GetInt64(info, "vote_publisher");

        // When signed in the user's own choices come next to vote_info; after voting they come inside it.
        var myVotes = ParseIndexes(data, "my_votes");
        if (myVotes.Count == 0)
        {
            myVotes = ParseIndexes(info, "my_votes");
        }

        return new CommentVoteDetail(id.ToString(CultureInfo.InvariantCulture), title!.Trim(), options)
        {
            Description = GetString(info, "desc") is { } description && !string.IsNullOrWhiteSpace(description) ? description.Trim() : null,
            EndTime = endTime is > 0 and <= MaxUnixSeconds ? DateTimeOffset.FromUnixTimeSeconds(endTime) : null,
            MaxChoiceCount = (int)Math.Max(1, Math.Min(options.Count, GetInt64(info, "choice_cnt"))),
            ParticipantCount = Math.Max(0, GetInt64(info, "join_num")),
            MyVotes = myVotes,
            PublisherId = publisher > 0 ? publisher.ToString(CultureInfo.InvariantCulture) : null,
            PublisherName = GetString(info, "name"),
            IsSupporterOnly = GetInt64(info, "only_fans_level") >= 10,
        };
    }

    /// <summary>
    /// Reads the options of a poll. Used on its own for the answer to a vote, which may only carry the new counts.
    /// </summary>
    public static IReadOnlyList<CommentVoteOption> ParseOptions(JsonElement info)
    {
        if (info.ValueKind != JsonValueKind.Object
            || !info.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CommentVoteOption>();
        }

        var result = new List<CommentVoteOption>();
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var index = GetInt64(option, "opt_idx");
            if (index is <= 0 or > int.MaxValue)
            {
                continue;
            }

            result.Add(new CommentVoteOption(
                (int)index,
                GetString(option, "opt_desc")?.Trim() ?? string.Empty,
                Math.Max(0, GetInt64(option, "cnt")),
                ParseImage(GetString(option, "img_url"))));
        }

        return result.AsReadOnly();
    }

    /// <summary>
    /// Reads a list of option indexes, such as the user's own choices.
    /// </summary>
    public static IReadOnlyList<int> ParseIndexes(JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object
            || !owner.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<int>();
        }

        var result = new List<int>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var index) && index > 0 && !result.Contains(index))
            {
                result.Add(index);
            }
        }

        return result.AsReadOnly();
    }

    /// <summary>
    /// Reads a whole number that may arrive as a number or as text. Missing means 0.
    /// </summary>
    public static long GetInt64(JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => 0,
        };
    }

    /// <summary>
    /// Reads a text value. Missing or not text means null.
    /// </summary>
    public static string? GetString(JsonElement owner, string name)
        => owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Uri? ParseImage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        // Pictures arrive as http:// or protocol-relative addresses; the image host serves all of them over https.
        var text = url!.Trim();
        if (text.StartsWith("//", StringComparison.Ordinal))
        {
            text = "https:" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        }

        return uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }
}
