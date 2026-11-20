// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.WinUIKernel.Share.Toolkits;

/// <summary>
/// 系统字体信息.
/// </summary>
public sealed class SystemFont
{
    /// <summary>
    /// 本地名称.
    /// </summary>
    public string LocalName { get; set; }

    /// <summary>
    /// 英文名称.
    /// </summary>
    public string EngName { get; set; }

    /// <inheritdoc/>
    public override string ToString() => LocalName;
}
