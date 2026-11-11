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
    /// <summary>
    /// 播放器关闭事件，应在此事件中释放客户端.
    /// </summary>
    public event EventHandler? Shutdown;

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
                ReachFileLoading?.Invoke(this, EventArgs.Empty);
                break;
            case NativeEventId.EndFile:
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
                ReachFileLoaded?.Invoke(this, EventArgs.Empty);
                break;
            case NativeEventId.EventIdle:
            case NativeEventId.Seek:
                NotifyPlayerState();
                break;
            case NativeEventId.PropertyChange:
                if (@event->Data != null)
                {
                    HandleObservePropertyChanged((MpvEventProperty*)@event->Data);
                }

                break;
            case NativeEventId.PlaybackRestart:
                SendNotify(MpvClientEventId.PlaybackRestart, default);
                break;
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

    private async void NotifyPlayerState()
    {
        var stateResult = await GetPlayerStateAsync();
        if (stateResult.IsFailed)
        {
            return;
        }

        SendNotify(MpvClientEventId.StateChanged, stateResult.Value);
    }

    private unsafe void HandleObservePropertyChanged(MpvEventProperty* eventProp)
    {
        var data = eventProp->Data;
        if (data == null)
        {
            return;
        }

        var name = Marshal.PtrToStringUTF8((nint)eventProp->Name);
        if (name == Pause || name == CoreIdle || name == Seeking)
        {
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
                };

                CacheStateChanged?.Invoke(this, cacheStateArgs);
            }
        }
    }
}
