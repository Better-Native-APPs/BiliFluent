// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Classic;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.MpvKernel.Core.Enums;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 应用视图模型.
/// </summary>
public sealed partial class AppViewModel : ViewModelBase
{
    private readonly ILogger<AppViewModel> _logger;
    private readonly IBiliTokenResolver _tokenResolver;

    [ObservableProperty]
    private Window _activatedWindow;

    [ObservableProperty]
    private bool _isInitialLoading;

    [ObservableProperty]
    private bool _isUpdateShown;

    public List<PlayerViewModel> Players { get; } = [];

    internal event EventHandler? PlaybackActivityChanged;

    // Buffering/seeking/opening and unfinished close remain unthrottled to protect video/audio.
    // Web/External do not expose transport state; retain normal QoS until their owner releases.
    internal bool HasActivePlayback
        => Players.Any(player => player.Player?.PlaybackState is MpvPlayerState.Playing or MpvPlayerState.Buffering or MpvPlayerState.Seeking)
            || _liveApplication?.HasActivePlayback == true
            || ClassicPlayers.Any(window => window.Session is { } session
                && (session.State is ClassicPlaybackState.Playing or ClassicPlaybackState.Loading or ClassicPlaybackState.Closing
                    || (session.Backend is ClassicBackendKind.Web or ClassicBackendKind.External && !session.BackendReleaseConfirmed)));

    internal void NotifyPlaybackActivityChanged()
        => PlaybackActivityChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 已创建的窗口列表.
    /// </summary>
    public List<Window> Windows { get; } = new();

    /// <summary>
    /// FFmpeg 路径.
    /// </summary>
    public string FFmpegPath { get; private set; }

    /// <summary>
    /// 是否为 AMD 显卡.
    /// </summary>
    public bool? IsAmdGpu { get; private set; }

    /// <summary>
    /// 是否已关闭.
    /// </summary>
    public bool IsClosed { get; set; }
}
