// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Models.Constants;

public enum DanmakuRendererType
{
    DirectX,

    Win2D,

    /// <summary>
    /// Render inside mpv with a secondary ASS subtitle track.
    /// </summary>
    Mpv,
}
