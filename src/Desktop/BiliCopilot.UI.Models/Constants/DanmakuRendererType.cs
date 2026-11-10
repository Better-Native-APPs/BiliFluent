// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Models.Constants;

public enum DanmakuRendererType
{
    /// <summary>
    /// The removed DirectX renderer. Only kept so stored settings still parse; it is treated as <see cref="Mpv"/>.
    /// </summary>
    DirectX,

    Win2D,

    /// <summary>
    /// Render inside mpv with a secondary ASS subtitle track.
    /// </summary>
    Mpv,
}
