// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.
// Modified for BiliFluent: native calls go through HanumanInstitute.LibMpv, results use MpvResult.

using static Richasy.MpvKernel.Core.Enums.MpvClientProperties;

namespace Richasy.MpvKernel.Core;

public sealed partial class MpvClient
{
    /// <summary>
    /// 获取全屏状态.
    /// </summary>
    /// <returns>是否全屏.</returns>
    public async Task<MpvResult<bool>> GetFullScreenStateAsync()
    {
        var errorCode = MpvError.Success;
        bool node = default;
        await Task.Run(() => errorCode = _handle.GetProperty(FullScreen, out node));
        var stateResult = WrapAsResult(errorCode, "Mpv | get fullscreen failed");
        if (stateResult.IsFailed)
        {
            return stateResult;
        }

        return node;
    }

    /// <summary>
    /// 设置全屏模式.
    /// </summary>
    /// <returns><see cref="Task"/>.</returns>
    public async Task<MpvResult> SetFullScreenStateAsync(bool isFullScreen)
    {
        var errorCode = MpvError.Success;
        await Task.Run(() => errorCode = _handle.SetProperty(CompactOverlay, false));
        var stateResult = WrapAsResult(errorCode, "Mpv | set FullScreen/ontop failed");
        if (stateResult.IsFailed)
        {
            return stateResult;
        }

        await Task.Run(() => errorCode = _handle.SetProperty(FullScreen, isFullScreen));
        return WrapAsResult(errorCode, "Mpv | set FullScreen/fullscreen failed");
    }

    /// <summary>
    /// 获取小窗状态.
    /// </summary>
    /// <returns>是否小窗.</returns>
    public async Task<MpvResult<bool>> GetCompactOverlayStateAsync()
    {
        var errorCode = MpvError.Success;
        bool node = default;
        await Task.Run(() => errorCode = _handle.GetProperty(CompactOverlay, out node));
        var stateResult = WrapAsResult(errorCode, "Mpv | get ontop failed");
        if (stateResult.IsFailed)
        {
            return stateResult;
        }

        return node;
    }

    /// <summary>
    /// 设置小窗置顶模式.
    /// </summary>
    /// <returns><see cref="Task"/>.</returns>
    public async Task<MpvResult> SetCompactOverlayStateAsync(bool isCompactOverlay)
    {
        var errorCode = MpvError.Success;
        await Task.Run(() => errorCode = _handle.SetProperty("fullscreen", false));
        var stateResult = WrapAsResult(errorCode, "Mpv | Set CompactOverlay/fullscreen failed");
        if (stateResult.IsFailed)
        {
            return stateResult;
        }

        await Task.Run(() => errorCode = _handle.SetProperty("ontop", isCompactOverlay));
        return WrapAsResult(errorCode, "Mpv | Set CompactOverlay/ontop failed");
    }
}
