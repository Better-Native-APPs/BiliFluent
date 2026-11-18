// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.

namespace Richasy.MpvKernel.Core.Models;

/// <summary>
/// MPV 初始化选项.
/// </summary>
public sealed class MpvInitializeOptions
{
    /// <summary>
    /// 同 <c>--config</c> 选项.
    /// </summary>
    public bool? UseConfig { get; set; }

    /// <summary>
    /// 同 <c>--config-dir</c> 选项.
    /// </summary>
    public string? ConfigDirectory { get; set; }

    /// <summary>
    /// 同 <c>--input-conf</c> 选项.
    /// </summary>
    public string? InputConfigPath { get; set; }

    /// <summary>
    /// 同 <c>--load-scripts</c> 选项.
    /// </summary>
    public bool? LoadScripts { get; set; }

    /// <summary>
    /// 同 <c>--script</c> 选项.
    /// </summary>
    public string? ScriptPath { get; set; }

    /// <summary>
    /// 同 <c>--player-operation-mode</c> 选项.
    /// </summary>
    public MpvPlayerOperationMode? PlayerOperationMode { get; set; }

    /// <summary>
    /// Disables the libcurl network backend for diagnostic comparisons.
    /// Unsupported options are logged without failing initialization.
    /// </summary>
    public bool DisableCurl { get; set; }

    /// <summary>
    /// Uses <c>--video-sync=display-resample</c> for diagnostic comparisons, without interpolation.
    /// A rejected option is logged without failing initialization.
    /// </summary>
    public bool UseDisplayResample { get; set; }
}
