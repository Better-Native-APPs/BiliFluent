// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.MpvKernel.Player.Models;

/// <summary>
/// MPV 媒体源.
/// </summary>
public sealed class MpvMediaSource(string url, string? id = default, string? title = default, MpvPlayOptions? options = default)
{
    /// <summary>
    /// 播放地址.
    /// </summary>
    public string Url { get; } = url;

    /// <summary>
    /// 媒体源ID.
    /// </summary>
    public string? Id { get; } = id;

    /// <summary>
    /// 媒体标题.
    /// </summary>
    public string? Title { get; } = title;

    /// <summary>
    /// 播放选项.
    /// </summary>
    public MpvPlayOptions Options { get; } = options ?? new MpvPlayOptions();

    /// <summary>
    /// The addresses of the streams, for a source the player may read through its loopback relay
    /// instead of opening <see cref="Url"/> itself. Null always plays <see cref="Url"/> directly.
    /// </summary>
    public MpvRelaySource? Relay { get; init; }
}
