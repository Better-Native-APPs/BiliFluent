// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

using Richasy.BiliKernel.Authenticator;
using Richasy.BiliKernel.Bili;
using Richasy.BiliKernel.Bili.Comment;
using Richasy.BiliKernel.Http;
using Richasy.BiliKernel.Models;
using Richasy.BiliKernel.Models.Comment;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Richasy.BiliKernel.Services.Comment.Core;

/// <summary>
/// Polls attached to comments. They are the same polls as the ones in dynamics, so this follows the
/// requests of the official poll page: GET /x/vote/vote_info and POST /x/vote/do_vote.
/// </summary>
internal sealed class CommentVoteClient
{
    // What the server answers for a poll that exists but may not be shown.
    private const int NotDisplayableCode = 260010;

    // The general "not signed in" code, and the one the poll service uses for the same thing.
    private const int NotSignedInCode = -101;
    private const int VoteNotSignedInCode = 100;

    // The official page refetches this long after a vote whose answer carries no results.
    private static readonly TimeSpan _refreshDelay = TimeSpan.FromMilliseconds(600);

    private readonly BiliHttpClient _httpClient;
    private readonly BiliAuthenticator _authenticator;

    public CommentVoteClient(
        BiliHttpClient httpClient,
        BiliAuthenticator authenticator)
    {
        _httpClient = httpClient;
        _authenticator = authenticator;
    }

    public async Task<CommentVoteDetail?> GetVoteDetailAsync(string voteId, CancellationToken cancellationToken)
    {
        var id = ParseId(voteId, nameof(voteId));
        var parameters = new Dictionary<string, string> { { "vote_id", id.ToString(CultureInfo.InvariantCulture) } };
        using var request = BiliHttpClient.CreateRequest(HttpMethod.Get, new Uri(BiliApis.Community.VoteInfo));

        // Works without signing in; the cookie only adds the user's own choices to the answer.
        _authenticator.AuthorizeRestRequest(request, parameters, new BiliAuthorizeExecutionSettings
        {
            ApiType = BiliApiType.None,
            RequireCookie = true,
            ForceNoToken = true,
        });

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(content);
            return document.RootElement.TryGetProperty("data", out var data) ? CommentVoteParser.ParseDetail(data) : null;
        }
        catch (KernelException ex) when (TryGetServerError(ex, out var code, out _) && code == NotDisplayableCode)
        {
            return null;
        }
        catch (JsonException ex)
        {
            throw new KernelException("Failed to parse the poll.", ex);
        }
    }

    public async Task<CommentVoteDetail> SubmitVoteAsync(
        CommentVoteDetail vote,
        IReadOnlyList<int> optionIndexes,
        string voterId,
        bool anonymous,
        CancellationToken cancellationToken)
    {
        var id = ParseId((vote ?? throw new ArgumentNullException(nameof(vote))).Id, nameof(vote));
        var votes = (optionIndexes ?? throw new ArgumentNullException(nameof(optionIndexes))).Distinct().ToList();
        if (votes.Count == 0 || votes.Count > vote.MaxChoiceCount || votes.Any(index => vote.Options.All(option => option.Index != index)))
        {
            throw new ArgumentException("The chosen options do not fit the poll.", nameof(optionIndexes));
        }

        if (!long.TryParse(voterId, NumberStyles.None, CultureInfo.InvariantCulture, out var voter) || voter <= 0)
        {
            throw new UnauthorizedAccessException("Sign in is required to vote.");
        }

        // The official page posts JSON and carries the CSRF token (the bili_jct cookie) in the query string.
        var csrf = _authenticator.AuthorizeRequestParameters(new Dictionary<string, string>(), new BiliAuthorizeExecutionSettings
        {
            ApiType = BiliApiType.None,
            NeedCSRF = true,
            ForceNoToken = true,
        }).TryGetValue("csrf", out var token) ? token : string.Empty;
        if (string.IsNullOrEmpty(csrf))
        {
            throw new UnauthorizedAccessException("Sign in is required to vote.");
        }

        var body = string.Format(
            CultureInfo.InvariantCulture,
            "{{\"vote_id\":{0},\"votes\":[{1}],\"voter_uid\":{2},\"status\":{3},\"op_bit\":0,\"dynamic_id\":0}}",
            id,
            string.Join(",", votes.Select(index => index.ToString(CultureInfo.InvariantCulture))),
            voter,
            anonymous ? 1 : 0);

        using var request = BiliHttpClient.CreateRequest(HttpMethod.Post, new Uri(BiliApis.Community.DoVote + "?csrf=" + Uri.EscapeDataString(csrf)));

        // Only the cookie is wanted from the authenticator; the form body it prepares is replaced by the JSON one.
        _authenticator.AuthorizeRestRequest(request, new Dictionary<string, string>(), new BiliAuthorizeExecutionSettings
        {
            ApiType = BiliApiType.None,
            RequireCookie = true,
            ForceNoToken = true,
        });
        request.Content?.Dispose();
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("Origin", "https://t.bilibili.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://t.bilibili.com/vote/h5/index/");

        IReadOnlyList<CommentVoteOption> options;
        IReadOnlyList<int> myVotes;
        long participants;
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(content);
            var info = document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("vote_info", out var voteInfo) ? voteInfo : default;
            options = CommentVoteParser.ParseOptions(info);
            myVotes = CommentVoteParser.ParseIndexes(info, "my_votes");
            participants = CommentVoteParser.GetInt64(info, "join_num");
        }
        catch (KernelException ex) when (TryGetServerError(ex, out var code, out var reason))
        {
            if (code is NotSignedInCode or VoteNotSignedInCode)
            {
                throw new UnauthorizedAccessException("Sign in is required to vote.", ex);
            }

            throw new CommentVoteRejectedException(code, reason, ex);
        }
        catch (JsonException)
        {
            // The vote went through (the response passed the code check); only the results could not be read.
            options = Array.Empty<CommentVoteOption>();
            myVotes = Array.Empty<int>();
            participants = 0;
        }

        if (options.Count > 0)
        {
            // The answer carries the new counts, and not necessarily anything else.
            return Merge(vote, options, participants > 0 ? participants : vote.ParticipantCount + 1, myVotes.Count > 0 ? myVotes : votes);
        }

        try
        {
            await Task.Delay(_refreshDelay, cancellationToken).ConfigureAwait(false);
            if (await GetVoteDetailAsync(vote.Id, cancellationToken).ConfigureAwait(false) is { } refreshed)
            {
                if (refreshed.MyVotes.Count == 0)
                {
                    refreshed.MyVotes = votes;
                }

                return refreshed;
            }
        }
        catch (Exception ex) when (ex is KernelException or OperationCanceledException)
        {
            // The vote itself succeeded; fall through and count it locally instead of reporting a failure.
        }

        return Merge(
            vote,
            vote.Options.Select(option => votes.Contains(option.Index)
                ? new CommentVoteOption(option.Index, option.Text, option.Count + 1, option.Image)
                : option).ToList(),
            vote.ParticipantCount + 1,
            votes);
    }

    // Keeps what describes the poll and takes the results from the newer source.
    private static CommentVoteDetail Merge(CommentVoteDetail vote, IReadOnlyList<CommentVoteOption> results, long participantCount, IReadOnlyList<int> myVotes)
    {
        var options = vote.Options
            .Select(option => results.FirstOrDefault(result => result.Index == option.Index) is { } result
                ? new CommentVoteOption(option.Index, option.Text, result.Count, option.Image)
                : option)
            .ToList();
        return new CommentVoteDetail(vote.Id, vote.Title, options)
        {
            Description = vote.Description,
            EndTime = vote.EndTime,
            MaxChoiceCount = vote.MaxChoiceCount,
            ParticipantCount = participantCount,
            MyVotes = myVotes,
            PublisherId = vote.PublisherId,
            PublisherName = vote.PublisherName,
            IsSupporterOnly = vote.IsSupporterOnly,
        };
    }

    private static long ParseId(string voteId, string parameterName)
        => long.TryParse(voteId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : throw new ArgumentException("The poll identifier must be a positive number.", parameterName);

    // BiliHttpClient reports a non-zero response code as a KernelException whose inner message is the raw response.
    private static bool TryGetServerError(KernelException exception, out int code, out string? reason)
    {
        code = 0;
        reason = null;
        if (exception.InnerException is not { } cause)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(cause.Message);
            var response = document.RootElement;
            if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("code", out var value)
                || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out code))
            {
                return false;
            }

            reason = CommentVoteParser.GetString(response, "message") ?? CommentVoteParser.GetString(response, "msg");
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
