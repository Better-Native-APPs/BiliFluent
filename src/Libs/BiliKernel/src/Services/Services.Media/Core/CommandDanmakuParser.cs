// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

using Bilibili.Community.Service.Dm.V1;
using Richasy.BiliKernel.Models.Danmaku;
using System.Globalization;
using System.Text.Json;

namespace Richasy.BiliKernel.Services.Media.Core;

internal static class CommandDanmakuParser
{
    public static CommandDanmakuInformation Parse(CommandDm command)
    {
        var information = new CommandDanmakuInformation
        {
            Id = string.IsNullOrEmpty(command.Idstr) ? command.Id.ToString(CultureInfo.InvariantCulture) : command.Idstr,
            Cid = command.Oid.ToString(CultureInfo.InvariantCulture),
            SenderId = command.Mid == 0 ? null : command.Mid.ToString(CultureInfo.InvariantCulture),
            Kind = command.Command switch
            {
                "#UP#" => CommandDanmakuKind.Up,
                "#LINK#" => CommandDanmakuKind.Link,
                "#ATTENTION#" => CommandDanmakuKind.Attention,
                "#VOTE#" => CommandDanmakuKind.Vote,
                "#GRADE#" => CommandDanmakuKind.Grade,
                "#RESERVE#" => CommandDanmakuKind.Reserve,
                _ => CommandDanmakuKind.Unknown,
            },
            Command = command.Command,
            Content = command.Content,
            ProgressMs = command.Progress,
            ExtraRaw = command.Extra,
        };

        if (string.IsNullOrWhiteSpace(command.Extra)
            || information.Kind == CommandDanmakuKind.Unknown
            || information.Kind == CommandDanmakuKind.Reserve)
        {
            return information;
        }

        try
        {
            using var document = JsonDocument.Parse(command.Extra);
            var extra = document.RootElement;
            if (extra.ValueKind != JsonValueKind.Object)
            {
                return information;
            }

            switch (information.Kind)
            {
                case CommandDanmakuKind.Up:
                    information.Up = new UpCommandDanmakuPayload
                    {
                        IconUrl = GetScalarText(extra, "icon"),
                    };
                    break;
                case CommandDanmakuKind.Link:
                    information.Link = new LinkCommandDanmakuPayload
                    {
                        Aid = GetScalarText(extra, "aid"),
                        Bvid = GetScalarText(extra, "bvid"),
                        Title = GetScalarText(extra, "title"),
                        IconUrl = GetScalarText(extra, "icon"),
                    };
                    break;
                case CommandDanmakuKind.Attention:
                    information.Attention = new AttentionCommandDanmakuPayload
                    {
                        DurationMs = GetInt32(extra, "duration"),
                        PosX = GetDouble(extra, "posX"),
                        PosY = GetDouble(extra, "posY"),
                        IconUrl = GetScalarText(extra, "icon"),
                        AttentionType = GetInt32(extra, "type"),
                    };
                    break;
                case CommandDanmakuKind.Vote:
                    information.Vote = new VoteCommandDanmakuPayload
                    {
                        VoteId = GetScalarText(extra, "vote_id"),
                        Question = GetScalarText(extra, "question"),
                        Options = GetVoteOptions(extra),
                        MyVote = GetInt32(extra, "my_vote"),
                        DurationMs = GetInt32(extra, "duration"),
                    };
                    break;
                case CommandDanmakuKind.Grade:
                    information.Grade = new GradeCommandDanmakuPayload
                    {
                        GradeId = GetScalarText(extra, "grade_id"),
                        Message = GetScalarText(extra, "msg"),
                        DurationMs = GetInt32(extra, "duration"),
                        Count = GetInt64(extra, "count"),
                        AverageScore = GetDouble(extra, "avg_score"),
                        MyScore = GetInt32(extra, "mid_score"),
                    };
                    break;
            }
        }
        catch (JsonException)
        {
            // Keep the recognized kind and original payload when the JSON is malformed.
        }

        return information;
    }

    private static IReadOnlyList<VoteCommandDanmakuOption> GetVoteOptions(JsonElement extra)
    {
        if (!extra.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<VoteCommandDanmakuOption>();
        }

        var result = new List<VoteCommandDanmakuOption>();
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            result.Add(new VoteCommandDanmakuOption
            {
                Index = GetInt32(option, "idx"),
                Text = GetScalarText(option, "desc"),
                Count = GetInt64(option, "cnt"),
            });
        }

        return result.AsReadOnly();
    }

    private static string? GetScalarText(JsonElement extra, string name)
    {
        if (!extra.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static int? GetInt32(JsonElement extra, string name)
        => int.TryParse(GetScalarText(extra, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static long? GetInt64(JsonElement extra, string name)
        => long.TryParse(GetScalarText(extra, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static double? GetDouble(JsonElement extra, string name)
        => double.TryParse(GetScalarText(extra, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && !double.IsNaN(value)
            && !double.IsInfinity(value) ? value : null;
}
