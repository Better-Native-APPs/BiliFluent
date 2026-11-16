// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using CommunityToolkit.Mvvm.Input;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private PlayerMediaCapabilities MediaCapabilities => new(_snapshot?.Type);

    /// <summary>Whether the media has an on-demand timeline, including previews and seeking.</summary>
    public bool CanSeek => MediaCapabilities.CanSeek;

    /// <summary>Whether persistent or temporary playback-rate changes are supported.</summary>
    public bool CanChangePlaybackRate => MediaCapabilities.CanChangePlaybackRate;

    /// <summary>Whether subtitle loading, selection and settings are supported.</summary>
    public bool CanUseSubtitles => MediaCapabilities.CanUseSubtitles;

    /// <summary>Whether the on-demand audio-track pipeline is supported; available choices still depend on the resolver.</summary>
    public bool CanSelectAudioTrack => MediaCapabilities.CanSelectAudioTrack;

    /// <summary>Whether DASH video/audio quality selection, including its automatic choice, is supported.</summary>
    public bool CanSelectOnDemandQuality => MediaCapabilities.CanSelectOnDemandQuality;

    /// <summary>Whether chapter loading and navigation are supported.</summary>
    public bool CanUseChapters => MediaCapabilities.CanUseChapters;

    /// <summary>Whether on-demand progress reporting and resume positions are supported.</summary>
    public bool CanUsePlaybackHistory => MediaCapabilities.CanUsePlaybackHistory;

    /// <summary>Whether previous/next media and end-of-media autoplay are supported.</summary>
    public bool CanNavigatePlaylist => MediaCapabilities.CanNavigatePlaylist;

    /// <summary>Whether Sponsor segments, automatic content actions and contribution are supported.</summary>
    public bool CanUseSponsorContent => MediaCapabilities.CanUseSponsorContent;

    /// <summary>Whether on-demand node sampling, failover, URL refresh and loopback relay are supported.</summary>
    public bool CanUseOnDemandNetwork => MediaCapabilities.CanUseOnDemandNetwork;

    /// <summary>Whether timeline-based interactive danmaku are supported.</summary>
    public bool CanUseInteractiveDanmaku => MediaCapabilities.CanUseInteractiveDanmaku;

    /// <summary>Whether on-demand danmaku segments and their interactions are supported.</summary>
    public bool CanLoadOnDemandDanmaku => MediaCapabilities.CanLoadOnDemandDanmaku;

    /// <summary>Whether sending through the on-demand aid/cid/position pipeline is supported.</summary>
    public bool CanSendOnDemandDanmaku => MediaCapabilities.CanSendOnDemandDanmaku;

    /// <summary>Whether a failed, duration-less on-demand load may be replayed automatically.</summary>
    public bool CanAutoReplay => MediaCapabilities.CanAutoReplay;

    /// <summary>Whether this media supports a send entry; live sends need the room-session pipeline.</summary>
    public bool CanSendDanmaku => MediaCapabilities.CanSendDanmaku;

    // Raw mpv bindings can seek or change rate behind transport commands. Only stats keys are common.
    private bool CanSendOpaquePlayerKey(string? key)
        => (CanSeek && CanChangePlaybackRate) || key is "ESC" or "i" or "I" or "1" or "2" or "3" or "4" or "5";

    private void NotifyMediaCapabilitiesChanged()
    {
        OnPropertyChanged(nameof(CanSeek));
        OnPropertyChanged(nameof(CanChangePlaybackRate));
        OnPropertyChanged(nameof(CanUseSubtitles));
        OnPropertyChanged(nameof(CanSelectAudioTrack));
        OnPropertyChanged(nameof(CanSelectOnDemandQuality));
        OnPropertyChanged(nameof(CanUseChapters));
        OnPropertyChanged(nameof(CanUsePlaybackHistory));
        OnPropertyChanged(nameof(CanNavigatePlaylist));
        OnPropertyChanged(nameof(CanUseSponsorContent));
        OnPropertyChanged(nameof(CanUseOnDemandNetwork));
        OnPropertyChanged(nameof(CanUseInteractiveDanmaku));
        OnPropertyChanged(nameof(CanLoadOnDemandDanmaku));
        OnPropertyChanged(nameof(CanSendOnDemandDanmaku));
        OnPropertyChanged(nameof(CanAutoReplay));
        OnPropertyChanged(nameof(CanSendDanmaku));
        OnPropertyChanged(nameof(IsQualityMenuAvailable));

        IRelayCommand[] commands =
        [
            IncreaseSpeedCommand, DecreaseSpeedCommand, SkipBackwardCommand, SkipForwardCommand,
            TripleSpeedCommand, RestoreSpeedCommand, ResetSubtitlePositionCommand, ChangeSubtitlePositionCommand,
            ReloadFontsCommand, ChangeSubtitleFontSizeCommand, ChangeSubtitleFontFamilyCommand,
            SwitchToPreviousChapterCommand, SwitchToNextChapterCommand, JumpToChapterCommand,
            SetSubtitleDelaySecondsCommand, ToggleSubtitleEnabledCommand, PlayNextCommand, PlayPreviousCommand,
            InitializeDanmakuCommand, ReloadDanmakuCommand, SendDanmakuCommand, SendKeyCommand,
        ];
        foreach (var command in commands) command.NotifyCanExecuteChanged();
    }
}

// Media-kind admission only. Account, settings, readiness and available rows keep their existing checks.
// This value type is deliberately independent of WinUI so its complete matrix can be checked headlessly.
internal readonly struct PlayerMediaCapabilities(BiliMediaType? mediaType)
{
    private bool IsOnDemand => mediaType is BiliMediaType.Video or BiliMediaType.Pgc;

    public bool CanSeek => IsOnDemand;
    public bool CanChangePlaybackRate => IsOnDemand;
    public bool CanUseSubtitles => IsOnDemand;
    public bool CanSelectAudioTrack => IsOnDemand;
    public bool CanSelectOnDemandQuality => IsOnDemand;
    public bool CanUseChapters => IsOnDemand;
    public bool CanUsePlaybackHistory => IsOnDemand;
    public bool CanNavigatePlaylist => IsOnDemand;
    public bool CanUseSponsorContent => IsOnDemand;
    public bool CanUseOnDemandNetwork => IsOnDemand;
    public bool CanUseInteractiveDanmaku => IsOnDemand;
    public bool CanLoadOnDemandDanmaku => IsOnDemand;
    public bool CanSendOnDemandDanmaku => IsOnDemand;
    public bool CanAutoReplay => IsOnDemand;
    public bool CanSendDanmaku => IsOnDemand || mediaType == BiliMediaType.Live;
}

public sealed partial class PlayerViewModel
{
    /// <summary>
    /// Whether a media has been admitted, so the capabilities above describe it. Before the first media they are
    /// all false and describe nothing: a view that drops what the media cannot do keeps its ordinary layout until then.
    /// It changes only together with the capabilities and has no notification of its own; read it alongside one of them.
    /// </summary>
    public bool HasAdmittedMedia => _snapshot is not null;
}
