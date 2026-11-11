// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Richasy.BiliKernel.Services.User.Core;

internal sealed class UserLiveRoomResponse
{
    [JsonPropertyName("roomStatus")]
    public int RoomStatus { get; set; }

    [JsonPropertyName("liveStatus")]
    public int LiveStatus { get; set; }

    [JsonPropertyName("roomid")]
    public long RoomId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}
