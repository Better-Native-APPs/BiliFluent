// Copyright (c) Richasy. All rights reserved.
// Licensed under the MIT License.
// Modified for BiliFluent: native events are read through HanumanInstitute.LibMpv.

using System.Runtime.InteropServices;
using HanumanInstitute.LibMpv.Core;
using Microsoft.Extensions.Logging;
using static Richasy.MpvKernel.Core.Enums.MpvClientProperties;
using NativeEvent = HanumanInstitute.LibMpv.Core.MpvEvent;
using NativeEventId = HanumanInstitute.LibMpv.Core.MpvEventId;
using NativeNode = HanumanInstitute.LibMpv.Core.MpvNode;

namespace Richasy.MpvKernel.Core;

public sealed partial class MpvClient
{
    // MPV_END_FILE_REASON_ERROR: the file could not be opened, or playback broke off.
    private const int EndFileReasonError = 4;

    /// <summary>
    /// 播放器关闭事件，应在此事件中释放客户端.
    /// </summary>
    public event EventHandler? Shutdown;

    /// <summary>
    /// The file ended because of the given error, such as a source that could not be opened.
    /// Raised on the event loop thread, right before <see cref="ReachFileEnd"/>.
    /// </summary>
    public event EventHandler<MpvError>? FileFailed;

    /// <summary>
    /// 文件播放结束事件.
    /// </summary>
    public event EventHandler? ReachFileEnd;

    /// <summary>
    /// 文件开始加载事件.
    /// </summary>
    public event EventHandler? ReachFileLoading;

    /// <summary>
    /// 文件加载完成事件.（此时开始尝试播放，如果是网络文件，此时开始缓冲）.
    /// </summary>
    public event EventHandler? ReachFileLoaded;

    /// <summary>
    /// The pause set by the user or the app changed; the value is the new state.
    /// Raised on the event loop thread.
    /// </summary>
    public event EventHandler<bool>? PauseChanged;

    /// <summary>
    /// A seek has started; it drops the forward cache. Raised on the event loop thread.
    /// </summary>
    public event EventHandler? SeekStarted;

    /// <summary>
    /// 发生错误.
    /// </summary>
    public event EventHandler<MpvError>? ErrorOccurred;

    /// <summary>
    /// 缓冲状态发生变化事件.
    /// </summary>
    public event EventHandler<MpvCacheStateEventArgs>? CacheStateChanged;

    /// <summary>
    /// Handles one native event on the event loop thread. Everything the event points to is
    /// copied here, because mpv reuses that memory on the next wait.
    /// </summary>
    private unsafe void HandleEvent(NativeEvent* @event)
    {
        var error = @event->Error;
        switch (@event->EventId)
        {
            case NativeEventId.StartFile:
                _audioResumeRecovery.ResetMedia();
                TraceSubtitleState("start-file", "event");
                ReachFileLoading?.Invoke(this, EventArgs.Empty);
                break;
            case NativeEventId.EndFile:
                _audioResumeRecovery.ResetMedia();
                TraceSubtitleState("end-file", "event");
                // mpv_event_end_file begins with the reason the file ended, followed by the error code.
                if (@event->Data != null && *(int*)@event->Data == EndFileReasonError)
                {
                    FileFailed?.Invoke(this, (MpvError)((int*)@event->Data)[1]);
                }

                ReachFileEnd?.Invoke(this, EventArgs.Empty);
                break;
            case NativeEventId.LogMessage:
                if (@event->Data != null)
                {
                    var logMessage = (MpvEventLogMessage*)@event->Data;
                    HandleLogMessage(
                        Marshal.PtrToStringUTF8((nint)logMessage->Level) ?? string.Empty,
                        Marshal.PtrToStringUTF8((nint)logMessage->Text) ?? string.Empty,
                        (MpvLogLevel)(int)logMessage->LogLevel);
                }

                break;
            case NativeEventId.FileLoaded:
                TraceSubtitleState("file-loaded", "event");
                ReachFileLoaded?.Invoke(this, EventArgs.Empty);
                break;
            case NativeEventId.EventIdle:
                NotifyPlayerState();
                break;
            case NativeEventId.Seek:
                _audioResumeRecovery.SeekStarted();
                SeekStarted?.Invoke(this, EventArgs.Empty);
                NotifyPlayerState();
                break;
            case NativeEventId.PropertyChange:
                if (@event->Data != null)
                {
                    HandleObservePropertyChanged((MpvEventProperty*)@event->Data);
                }

                break;
            case NativeEventId.PlaybackRestart:
                _audioResumeRecovery.PlaybackRestarted();
                SendNotify(MpvClientEventId.PlaybackRestart, default);
                NotifyPlayerState();
                break;

            case NativeEventId.EventAudioReconfig:
                HandlePausedAudioOutputReconfiguration();
                _logger.LogInformation("[MPV] Event received: EventAudioReconfig");
                break;
            case NativeEventId.CommandReply:
                CompleteTrackCommand(@event->ReplyUserData, error);
                return;
            default:
                _logger.LogInformation($"[MPV] Event received: {@event->EventId}");
                break;
        }

        if (error < 0)
        {
            ErrorOccurred?.Invoke(this, (MpvError)error);
        }
    }

    private void HandleLogMessage(string level, string text, MpvLogLevel logLevel)
    {
#if DEBUG
        System.Diagnostics.Debug.WriteLine($"[MPV] Log message: {level} - {text}");
#endif
        _logger.LogInformation($"[MPV] Log message: {level} - {text}");
        if (logLevel == MpvLogLevel.Warn)
        {
            if (text.Contains("HTTP error", StringComparison.OrdinalIgnoreCase))
            {
                if (text.Contains("404", StringComparison.OrdinalIgnoreCase))
                {
                    ErrorOccurred?.Invoke(this, MpvError.Http404);
                }
                else if (text.Contains("403", StringComparison.OrdinalIgnoreCase))
                {
                    ErrorOccurred?.Invoke(this, MpvError.Http403);
                }
                else if (text.Contains("500", StringComparison.OrdinalIgnoreCase))
                {
                    ErrorOccurred?.Invoke(this, MpvError.Http500);
                }

                ErrorOccurred?.Invoke(this, MpvError.VoInitFailed);
            }

            if (text.Contains("error reading packet", StringComparison.OrdinalIgnoreCase))
            {
                ErrorOccurred?.Invoke(this, MpvError.LoadingFailed);
            }
            else if (text.Contains("Failed to seek when reading header element", StringComparison.Ordinal))
            {
                ErrorOccurred?.Invoke(this, MpvError.TlsError);
            }
        }
        else if (logLevel == MpvLogLevel.Error)
        {
            if (text.Contains("Subprocess failed", StringComparison.OrdinalIgnoreCase))
            {
                // 意味着播放失败，需要抛出该异常.
                ErrorOccurred?.Invoke(this, MpvError.VoInitFailed);
            }
            else if (text.Contains("Couldn't open Blu-ray device", StringComparison.OrdinalIgnoreCase))
            {
                ErrorOccurred?.Invoke(this, MpvError.BluRayInitFailed);
            }
            else if (text.Contains("Couldn't open DVD device", StringComparison.Ordinal))
            {
                ErrorOccurred?.Invoke(this, MpvError.DvdInitFailed);
            }
            else if (text.Contains("Failed to open http", StringComparison.Ordinal))
            {
                ErrorOccurred?.Invoke(this, MpvError.VoInitFailed);
            }
            else if (text.Contains("tls: IO error", StringComparison.Ordinal) || text.Contains("Seek failed", StringComparison.Ordinal))
            {
                ErrorOccurred?.Invoke(this, MpvError.TlsError);
            }
            else if (text.Contains("Passthrough format unsupported", StringComparison.Ordinal))
            {
                ErrorOccurred?.Invoke(this, MpvError.PassthroughFormatUnsupported);
            }
        }
    }

    private void NotifyPlayerState() => _ = NotifyPlayerStateAsync();

    private async Task NotifyPlayerStateAsync()
    {
        var presentation = _audioResumeRecovery.PresentationVersion;
        try
        {
            Task<MpvResult<MpvPlayerState>> pending;
            using (var callback = _callbackWork.TryEnter())
            {
                if (!callback.IsActive || IsDisposed) return;
                pending = GetPlayerStateAsync();
            }

            var stateResult = await pending.ConfigureAwait(false);
            if (stateResult.IsSuccess && !IsDisposed && presentation == _audioResumeRecovery.PresentationVersion)
                SendNotify(MpvClientEventId.StateChanged, PresentAudioRecoveryState(stateResult.Value), presentation);
        }
        catch (Exception ex) { MpvCallbackError.Log(_logger, "player-state", ex); }
    }

    private unsafe void HandleObservePropertyChanged(MpvEventProperty* eventProp)
    {
        var data = eventProp->Data;
        if (data == null)
        {
            return;
        }

        var name = Marshal.PtrToStringUTF8((nint)eventProp->Name);
        if (name is "sid" or "secondary-sid" or "sub-visibility" or "secondary-sub-visibility" or "track-list")
        {
            TraceSubtitleState(name, "property-change");
            return;
        }

        if (name == Pause || name == CoreIdle || name == Seeking)
        {
            if (name == Pause)
            {
                PauseChanged?.Invoke(this, *(int*)data != 0);
            }

            NotifyPlayerState();
        }
        else if (name == Volume)
        {
            SendNotify(MpvClientEventId.VolumeChanged, *(double*)data);
        }
        else if (name == Duration)
        {
            SendNotify(MpvClientEventId.DurationChanged, *(double*)data);
        }
        else if (name == TimePosition)
        {
            if (IsTimePositionNotificationCountingEnabled)
            {
                Interlocked.Increment(ref _timePositionNotificationCount);
            }

            SendNotify(MpvClientEventId.PositionChanged, *(double*)data);
        }
        else if (name == FullScreen)
        {
            SendNotify(MpvClientEventId.FullScreenChanged, *(int*)data != 0);
        }
        else if (name == CompactOverlay)
        {
            SendNotify(MpvClientEventId.CompactOverlayChanged, *(int*)data != 0);
        }
        else if (name == Speed)
        {
            SendNotify(MpvClientEventId.SpeedChanged, *(double*)data);
        }
        else if (name == CacheSpeed)
        {
            SendNotify(MpvClientEventId.CacheSpeedChanged, *(long*)data);
        }
        else if (name == Metadata)
        {
            if (MpvNodeReader.Read((NativeNode*)data) is Dictionary<string, object?> nodeDict)
            {
                SendNotify(MpvClientEventId.MetadataLoaded, nodeDict.ToDictionary(p => p.Key, p => p.Value as string));
            }
        }
        else if (name == TrackCount)
        {
            SendNotify(MpvClientEventId.TrackCountChanged, default);
        }
        else if (name == DemuxerCacheState)
        {
            if (MpvNodeReader.Read((NativeNode*)data) is Dictionary<string, object?> nodeDict
                && nodeDict.TryGetValue("seekable-ranges", out var rangeNodes))
            {
                var ranges = new List<MpvSeekableRange>();
                foreach (var item in rangeNodes as List<object?> ?? [])
                {
                    if (item is Dictionary<string, object?> map
                        && map.GetDouble("start") is double start
                        && map.GetDouble("end") is double end)
                    {
                        ranges.Add(new MpvSeekableRange
                        {
                            Start = start,
                            End = end,
                        });
                    }
                }

                var cacheStateArgs = new MpvCacheStateEventArgs(ranges)
                {
                    BofCached = nodeDict.GetFlag("bof-cached"),
                    EofCached = nodeDict.GetFlag("eof-cached"),
                    FwBytes = nodeDict.GetInteger("fw-bytes") ?? 0,
                    FileCacheBytes = nodeDict.GetInteger("file-cache-bytes") ?? 0,
                    RawInputRate = nodeDict.GetInteger("raw-input-rate") ?? 0,
                };

                if (nodeDict.TryGetValue("ts-per-stream", out var streamNodes))
                {
                    foreach (var item in streamNodes as List<object?> ?? [])
                    {
                        if (item is not Dictionary<string, object?> map
                            || !map.TryGetValue("type", out var type)
                            || map.GetDouble("cache-duration") is not double seconds)
                        {
                            continue;
                        }

                        if (type as string == "video")
                        {
                            cacheStateArgs.VideoCacheSeconds = seconds;
                        }
                        else if (type as string == "audio")
                        {
                            cacheStateArgs.AudioCacheSeconds = seconds;
                        }
                    }
                }

                CacheStateChanged?.Invoke(this, cacheStateArgs);
            }
        }
    }
}
