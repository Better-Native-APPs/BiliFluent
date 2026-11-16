// Copyright (c) Bili Copilot. All rights reserved.

namespace Richasy.MpvKernel.Player.Models;

/// <summary>The outcome of a seamless request. Only Switched changes the selected stream.</summary>
public enum MpvSeamlessSwitchStatus
{
    /// <summary>The target track was selected.</summary>
    Switched,

    /// <summary>The route does not support the target or current playback state.</summary>
    NotSupported,

    /// <summary>Preparation or selection failed. The previous stream is retained.</summary>
    Failed,

    /// <summary>The request was superseded, cancelled, or invalidated by playback changes.</summary>
    Cancelled,
}

/// <summary>A credential-free reason suitable for fallback policy and diagnostics.</summary>
public enum MpvSeamlessSwitchReason
{
    /// <summary>No failure.</summary>
    None,
    /// <summary>The request began while paused, or playback was paused while waiting.</summary>
    Paused,
    /// <summary>segment_base ranges are missing or too large.</summary>
    MissingMetadata,
    /// <summary>The caller marked the format unsupported, or its required preload exceeds the memory budget.</summary>
    RestrictedFormat,
    /// <summary>The loopback relay is disabled, unavailable, or an HTTP proxy is configured.</summary>
    RelayUnavailable,
    /// <summary>No finite, playing timeline is available.</summary>
    PlaybackUnavailable,
    /// <summary>No future fragment with a starting keyframe is available.</summary>
    NoKeyFrame,
    /// <summary>The preparation budget expired.</summary>
    PreparationTimeout,
    /// <summary>The boundary wait budget expired.</summary>
    WaitTimeout,
    /// <summary>A new main file was loaded, playback ended, or playback stopped.</summary>
    PlaybackChanged,
    /// <summary>A newer seamless request replaced this request.</summary>
    Superseded,
    /// <summary>A seek invalidated the prepared boundary or audio window.</summary>
    Seeked,
    /// <summary>Playback speed changed during preparation or boundary waiting.</summary>
    SpeedChanged,
    /// <summary>The caller cancelled the request.</summary>
    CallerCancelled,
    /// <summary>The player was disposed.</summary>
    Disposed,
    /// <summary>Network, MP4 parsing or preload preparation failed.</summary>
    PreparationFailed,
    /// <summary>The native track operation failed.</summary>
    TrackOperationFailed,
    /// <summary>Preparation or scheduling missed the safe video selection window.</summary>
    MissedBoundary,
}

/// <summary>The small set of stages exposed to an application's pending-switch UI.</summary>
public enum MpvSeamlessSwitchPhase
{
    /// <summary>Fetching the head and fragments, then adding the track.</summary>
    Preparing,
    /// <summary>The video track is ready and the boundary has not been reached.</summary>
    Waiting,
    /// <summary>The selected track property is being changed.</summary>
    Selecting,
    /// <summary>The request is complete; Result is available.</summary>
    Finished,

    /// <summary>The relay is prepared and the native add command is about to start.</summary>
    Adding,
}

/// <summary>A switch outcome without upstream URLs, headers, or exception messages.</summary>
/// <param name="Status">Whether the target was selected.</param>
/// <param name="Reason">A machine-readable reason.</param>
/// <param name="TrackId">The selected target's mpv ID, only on success.</param>
/// <param name="SwitchTimeSeconds">The video fragment boundary, or the audio selection position.</param>
/// <param name="SelectionPositionSeconds">The actual position at selection.</param>
/// <param name="Preparation">Time spent fetching and adding the target.</param>
/// <param name="BoundaryWait">Time spent waiting after video preparation.</param>
/// <param name="PreparationFailovers">Node rotations during head and fragment preparation.</param>
public sealed record MpvSeamlessSwitchResult(
    MpvSeamlessSwitchStatus Status,
    MpvSeamlessSwitchReason Reason,
    int? TrackId = null,
    double? SwitchTimeSeconds = null,
    double? SelectionPositionSeconds = null,
    TimeSpan Preparation = default,
    TimeSpan BoundaryWait = default,
    int PreparationFailovers = 0)
{
    /// <summary>Whether the target is now selected.</summary>
    public bool IsSuccess => Status == MpvSeamlessSwitchStatus.Switched;

    /// <summary>The target's total memory preload, in bytes; populated on success.</summary>
    public long PreloadedBytes { get; init; }
    /// <summary>The gap between the head and current audio fragments, whether filled or not; populated on success.</summary>
    public long AudioOpeningGapBytes { get; init; }
    /// <summary>The additional audio opening bytes before the current fragments; populated on success.</summary>
    public long AdditionalOpeningBytes { get; init; }
    /// <summary>Time fetching those additional opening bytes; populated on success.</summary>
    public TimeSpan OpeningPreparation { get; init; }
}

/// <summary>Progress raised on the worker thread, not marshalled to the UI context.</summary>
/// <param name="requestId">A monotonically increasing request ID. Ignore older requests in UI code.</param>
/// <param name="kind">The stream being replaced.</param>
/// <param name="phase">The current stage.</param>
/// <param name="switchTimeSeconds">The planned boundary, when known.</param>
/// <param name="result">The final outcome; only set for Finished.</param>
public sealed class MpvSeamlessSwitchEventArgs(long requestId, MpvRelayStreamKind kind, MpvSeamlessSwitchPhase phase, double? switchTimeSeconds, MpvSeamlessSwitchResult? result) : EventArgs
{
    /// <summary>The request ID.</summary>
    public long RequestId { get; } = requestId;
    /// <summary>The stream being replaced.</summary>
    public MpvRelayStreamKind Kind { get; } = kind;
    /// <summary>The current stage.</summary>
    public MpvSeamlessSwitchPhase Phase { get; } = phase;
    /// <summary>The planned boundary, when known.</summary>
    public double? SwitchTimeSeconds { get; } = switchTimeSeconds;
    /// <summary>The final outcome; only set for Finished.</summary>
    public MpvSeamlessSwitchResult? Result { get; } = result;
    /// <summary>The prepared track's relay key, once registered; never includes the access token.</summary>
    public string? RelayKey { get; init; }
}
