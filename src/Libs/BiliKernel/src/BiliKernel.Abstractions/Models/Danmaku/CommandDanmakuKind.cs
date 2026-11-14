// Copyright (c) BiliFluent. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.BiliKernel.Models.Danmaku;

/// <summary>
/// Identifies a command danmaku without discarding unsupported commands.
/// </summary>
public enum CommandDanmakuKind
{
    /// <summary>An unsupported command.</summary>
    Unknown,

    /// <summary>An uploader avatar danmaku.</summary>
    Up,

    /// <summary>A related video card.</summary>
    Link,

    /// <summary>A follow or triple-action prompt.</summary>
    Attention,

    /// <summary>A voting card.</summary>
    Vote,

    /// <summary>A rating card.</summary>
    Grade,

    /// <summary>A reservation card with an unparsed payload.</summary>
    Reserve,
}
