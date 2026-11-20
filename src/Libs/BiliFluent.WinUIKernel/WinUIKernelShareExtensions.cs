// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Richasy.WinUIKernel.Share;

/// <summary>
/// AI kernel extension.
/// </summary>
public static class WinUIKernelShareExtensions
{
    /// <summary>
    /// 图片缓存时间。
    /// </summary>
    public static TimeSpan ImageCacheTime { get; set; } = TimeSpan.FromDays(7);

    internal static ILogger Logger { get; set; }

    /// <summary>
    /// Initialize the Share kernel.
    /// </summary>
    /// <param name="logger">日志记录.</param>
    public static void InitializeShareKernel(ILogger? logger = null)
    {
        Logger = logger ?? NullLogger.Instance;
    }
}
