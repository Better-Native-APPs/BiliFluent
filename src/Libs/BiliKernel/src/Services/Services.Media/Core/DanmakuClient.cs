// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using Bilibili.Community.Service.Dm.V1;
using Richasy.BiliKernel.Authenticator;
using Richasy.BiliKernel.Bili;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Http;
using Richasy.BiliKernel.Models;
using Richasy.BiliKernel.Models.Danmaku;
using System.Globalization;
using System.Text.Json;

namespace Richasy.BiliKernel.Services.Media.Core;

internal sealed class DanmakuClient
{
    private readonly BiliHttpClient _httpClient;
    private readonly BiliAuthenticator _authenticator;
    private readonly IBiliTokenResolver _tokenResolver;

    public DanmakuClient(
        BiliHttpClient httpClient,
        BiliAuthenticator authenticator,
        IBiliTokenResolver tokenResolver)
    {
        _httpClient = httpClient;
        _authenticator = authenticator;
        _tokenResolver = tokenResolver;
    }

    public async Task<DanmakuMeta> GetDanmakuMetaAsync(string aid, string cid, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(aid) || string.IsNullOrEmpty(cid))
        {
            throw new ArgumentNullException(nameof(aid), "aid 或者 cid 不能为空");
        }
        var queryParameters = new Dictionary<string, string>
        {
            { "type", "1" },
            { "oid", cid },
            { "pid", aid },
        };

        DmWebViewReply? reply = default;
        var request = BiliHttpClient.CreateRequest(HttpMethod.Get, new Uri(BiliApis.Video.DanmakuMetaData));
        _authenticator.AuthorizeRestRequest(request, queryParameters, new BiliAuthorizeExecutionSettings
        {
            RequireCookie = true,
            ApiType = BiliApiType.Web,
        });
        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        reply = await BiliHttpClient.ParseAsync(response, DmWebViewReply.Parser).ConfigureAwait(false);

        return new DanmakuMeta
        {
            SegmentCount = Convert.ToInt32(reply.DmSge?.Total ?? 0),
            Total = Convert.ToInt32(reply.Count),
            CommandDanmakus = reply.CommandDms.Select(CommandDanmakuParser.Parse).ToList(),
        };
    }

    public async Task<IReadOnlyList<DanmakuInformation>> GetSegmentDanmakusWithWebAsync(string aid, string cid, int segmentIndex, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(aid) || string.IsNullOrEmpty(cid))
        {
            throw new ArgumentNullException(nameof(aid), "aid 或者 cid 不能为空");
        }

        DmSegMobileReply? reply = default;
        var retryCount = 0;

        do
        {
            try
            {
                var queryParameters = new Dictionary<string, string>
                {
                    { "type", "1" },
                    { "oid", cid },
                    { "pid", aid },
                    { "segment_index", segmentIndex.ToString() },
                };
                var request = BiliHttpClient.CreateRequest(HttpMethod.Get, new Uri(BiliApis.Video.WebSegmentDanmaku));
                _authenticator.AuthorizeRestRequest(request, queryParameters, new BiliAuthorizeExecutionSettings
                {
                    RequireCookie = true,
                    ApiType = BiliApiType.Web,
                });
                var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                reply = await BiliHttpClient.ParseAsync(response, DmSegMobileReply.Parser).ConfigureAwait(false);
            }
            catch (Exception)
            {
                retryCount++;
                if (retryCount > 2)
                {
                    throw;
                }
            }
        }
        while (reply == null);

        return reply.Elems.Select(p => new DanmakuInformation(
            string.IsNullOrEmpty(p.IdStr) ? p.Id.ToString() : p.IdStr,
            p.Content,
            p.Mode,
            p.Progress / 1000.0,
            (uint)p.Color,
            p.Fontsize)).ToList();
    }

    public async Task<IReadOnlyList<DanmakuInformation>> GetSegmentDanmakusWithGrpcAsync(string aid, string cid, int segmentIndex, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(aid) || string.IsNullOrEmpty(cid))
        {
            throw new ArgumentNullException(nameof(aid), "aid 或者 cid 不能为空");
        }

        var req = new DmSegMobileReq
        {
            Pid = Convert.ToInt64(aid),
            Oid = Convert.ToInt64(cid),
            SegmentIndex = segmentIndex,
            Type = 1,
        };

        DmSegMobileReply? reply = default;
        var retryCount = 0;

        do
        {
            try
            {
                var request = BiliHttpClient.CreateRequest(new Uri(BiliApis.Video.GrpcSegmentDanmaku), req);
                _authenticator.AuthorizeGrpcRequest(request);
                var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                reply = await BiliHttpClient.ParseAsync(response, DmSegMobileReply.Parser).ConfigureAwait(false);
            }
            catch (Exception)
            {
                retryCount++;
                if (retryCount > 2)
                {
                    throw;
                }
            }
        }
        while (reply == null);

        return reply.Elems.Select(p => new DanmakuInformation(
            string.IsNullOrEmpty(p.IdStr) ? p.Id.ToString() : p.IdStr,
            p.Content,
            p.Mode,
            p.Progress / 1000.0,
            (uint)p.Color,
            p.Fontsize)).ToList();
    }

    public async Task LikeDanmakuAsync(string danmakuId, string cid, bool isLike, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string>
        {
            { "dmid", danmakuId },
            { "oid", cid },
            { "op", isLike ? "1" : "2" },
            { "platform", "web_player" },
        };

        var request = BiliHttpClient.CreateRequest(HttpMethod.Post, new Uri(BiliApis.Video.LikeDanmaku));
        _authenticator.AuthorizeRestRequest(request, parameters, new BiliAuthorizeExecutionSettings
        {
            ApiType = BiliApiType.None,
            NeedCSRF = true,
            RequireCookie = true,
            ForceNoToken = true,
        });
        await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task VoteDanmakuAsync(string aid, string cid, int progressMs, string voteId, int voteType,
        string commandId, int optionId, bool hasSelfDefinition, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(aid))
        {
            throw new ArgumentException("The video aid is required.", nameof(aid));
        }

        if (string.IsNullOrEmpty(cid))
        {
            throw new ArgumentException("The video cid is required.", nameof(cid));
        }

        if (string.IsNullOrEmpty(voteId))
        {
            throw new ArgumentException("The vote identifier is required.", nameof(voteId));
        }

        if (string.IsNullOrEmpty(commandId))
        {
            throw new ArgumentException("The command danmaku identifier is required.", nameof(commandId));
        }

        if (progressMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(progressMs), "The playback progress must not be negative.");
        }

        if (optionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(optionId), "The option identifier must be positive.");
        }

        var parameters = new Dictionary<string, string>
        {
            { "aid", aid },
            { "cid", cid },
            { "progress", progressMs.ToString(CultureInfo.InvariantCulture) },
            { "vote_id", voteId },
            { "vote_type", voteType.ToString(CultureInfo.InvariantCulture) },
            { "cmd_id_str", commandId },
            { "option_id", optionId.ToString(CultureInfo.InvariantCulture) },
            { "has_self_def", hasSelfDefinition ? "1" : "0" },
            { "polaris_app_id", "100" },
            { "polaris_platform", "5" },
            { "statistics", "{\"appId\":100,\"platform\":5,\"abtest\":\"\",\"version\":\"\"}" },
        };

        using var request = BiliHttpClient.CreateRequest(HttpMethod.Post, new Uri(BiliApis.Video.VoteDanmaku));
        _authenticator.AuthorizeRestRequest(request, parameters, new BiliAuthorizeExecutionSettings
        {
            ApiType = BiliApiType.None,
            NeedCSRF = true,
            RequireCookie = true,
            ForceNoToken = true,
        });
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (KernelException ex) when (IsVoteSignInRequired(ex))
        {
            throw new UnauthorizedAccessException("Sign in is required to vote.", ex);
        }
    }

    public async Task<VoteCommandDanmakuPayload> GetDanmakuVoteInfoAsync(string voteId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(voteId))
        {
            throw new ArgumentException("The vote identifier is required.", nameof(voteId));
        }
        var parameters = new Dictionary<string, string> { { "vote_id", voteId } };
        using var request = BiliHttpClient.CreateRequest(HttpMethod.Get, new Uri(BiliApis.Video.DanmakuVoteInfo));
        _authenticator.AuthorizeRestRequest(request, parameters, new BiliAuthorizeExecutionSettings
        {
            ApiType = BiliApiType.None,
            RequireCookie = true,
            ForceNoToken = true,
        });
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("vote_info", out var info) || info.ValueKind != JsonValueKind.Object)
        {
            throw new KernelException("Failed to parse voting information.");
        }

        return CommandDanmakuParser.ParseVoteInfo(info);
    }

    private static bool IsVoteSignInRequired(KernelException exception)
    {
        if (exception.InnerException is not { } cause) return false;
        try
        {
            using var document = JsonDocument.Parse(cause.Message);
            var response = document.RootElement;
            return response.ValueKind == JsonValueKind.Object && response.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value) && value == -101;
        }
        catch (JsonException) { return false; }
    }

    public async Task GradeDanmakuAsync(string aid, string cid, int progressMs, string gradeId, int gradeScore, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(aid))
        {
            throw new ArgumentException("The video aid is required.", nameof(aid));
        }

        if (string.IsNullOrEmpty(cid))
        {
            throw new ArgumentException("The video cid is required.", nameof(cid));
        }

        if (string.IsNullOrEmpty(gradeId))
        {
            throw new ArgumentException("The grade identifier is required.", nameof(gradeId));
        }

        if (progressMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(progressMs), "The playback progress must not be negative.");
        }

        if (gradeScore < 2 || gradeScore > 10 || gradeScore % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gradeScore), "The grade score must be 2, 4, 6, 8, or 10.");
        }

        var parameters = new Dictionary<string, string>
        {
            { "aid", aid },
            { "cid", cid },
            { "progress", progressMs.ToString(CultureInfo.InvariantCulture) },
            { "grade_id", gradeId },
            { "grade_score", gradeScore.ToString(CultureInfo.InvariantCulture) },
        };

        var request = BiliHttpClient.CreateRequest(HttpMethod.Post, new Uri(BiliApis.Video.GradeDanmaku));
        _authenticator.AuthorizeRestRequest(request, parameters, new BiliAuthorizeExecutionSettings
        {
            ApiType = BiliApiType.None,
            NeedCSRF = true,
            RequireCookie = true,
            ForceNoToken = true,
        });
        await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, (int Likes, bool UserLiked)>> GetDanmakuLikeStatesAsync(string cid, IEnumerable<string> danmakuIds, CancellationToken cancellationToken = default)
    {
        var ids = string.Join(",", danmakuIds);
        if (string.IsNullOrEmpty(ids))
        {
            return new Dictionary<string, (int Likes, bool UserLiked)>();
        }

        var parameters = new Dictionary<string, string>
        {
            { "oid", cid },
            { "ids", ids },
        };

        var request = BiliHttpClient.CreateRequest(HttpMethod.Get, new Uri(BiliApis.Video.DanmakuLikeStats));
        _authenticator.AuthorizeRestRequest(request, parameters, new BiliAuthorizeExecutionSettings
        {
            ApiType = BiliApiType.Web,
            RequireCookie = true,
            ForceNoToken = true,
        });
        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseObj = await BiliHttpClient.ParseAsync(response, SourceGenerationContext.Default.BiliDataResponseDictionaryStringDanmakuLikeState).ConfigureAwait(false)
            ?? throw new KernelException("解析弹幕点赞状态失败");
        return responseObj.Data?.ToDictionary(p => p.Key, p => (Likes: p.Value.Likes, UserLiked: p.Value.UserLike == 1))
            ?? new Dictionary<string, (int Likes, bool UserLiked)>();
    }

    public async Task ReportDanmakuAsync(string danmakuId, string cid, int reason, string? content = null, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string>
        {
            { "cid", cid },
            { "dmid", danmakuId },
            { "reason", reason.ToString() },
        };

        if (content != null)
        {
            parameters.Add("content", content);
        }

        var request = BiliHttpClient.CreateRequest(HttpMethod.Post, new Uri(BiliApis.Video.ReportDanmaku));
        _authenticator.AuthorizeRestRequest(request, parameters, new BiliAuthorizeExecutionSettings
        {
            ApiType = BiliApiType.Web,
            NeedCSRF = true,
            RequireCookie = true,
            ForceNoToken = true,
        });
        await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendVideoDanmakuAsync(string content, string aid, string cid, int progress, string color, bool isStandardSize = true, DanmakuLocation location = DanmakuLocation.Scroll, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string>
        {
            { "aid", aid },
            { "type", "1" },
            { "oid", cid },
            { "msg", content },
            { "progress", (progress * 1000).ToString() },
            { "color", color },
            { "fontsize", isStandardSize ? "25" : "18" },
            { "mode", ((int)location).ToString() },
            { "rnd", DateTimeOffset.Now.ToLocalTime().ToUnixTimeMilliseconds().ToString() },
        };

        var request = BiliHttpClient.CreateRequest(HttpMethod.Post, new Uri(BiliApis.Video.SendDanmaku));
        _authenticator.AuthorizeRestRequest(request, parameters);
        await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendLiveDanmakuAsync(string message, string roomId, string color, bool isStandardSize = true, DanmakuLocation location = DanmakuLocation.Scroll, CancellationToken cancellationToken = default)
    {
        var mid = _tokenResolver.GetToken().UserId.ToString();
        var parameters = new Dictionary<string, string>
        {
            { "cid", roomId },
            { "mid", mid },
            { "color", color },
            { "fontsize", isStandardSize ? "25" : "18" },
            { "msg", message },
            { "rnd", DateTimeOffset.Now.ToLocalTime().ToUnixTimeMilliseconds().ToString() },
            { "mode", ((int)location).ToString() },
            { "pool", "0" },
            { "type", "json" },
            { "playtime", "0.0" },
        };

        var request = BiliHttpClient.CreateRequest(HttpMethod.Post, new Uri(BiliApis.Live.SendMessage));
        _authenticator.AuthorizeRestRequest(request, parameters);
        await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
