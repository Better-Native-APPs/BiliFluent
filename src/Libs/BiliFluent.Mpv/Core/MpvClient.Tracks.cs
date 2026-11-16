// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Concurrent;
using System.Globalization;

namespace Richasy.MpvKernel.Core;

/// <summary>
/// Asynchronous, cancellable operations on independent video and audio tracks.
/// </summary>
public sealed partial class MpvClient
{
    private static readonly TimeSpan MediaTrackTimeout = TimeSpan.FromSeconds(8);
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<MpvError>> _trackCommands = new();
#pragma warning disable CA2213 // Outstanding add operations release this gate after native completion.
    private readonly SemaphoreSlim _trackAddGate = new(1, 1);
#pragma warning restore CA2213
    private long _trackCommandId;

    /// <summary>
    /// Adds a video track with an explicit title and returns its ID. The command has an eight-second
    /// timeout and never blocks the calling thread. Cancellation throws; a late add cannot select itself.
    /// </summary>
    public Task<MpvResult<int>> AddVideoTrackAsync(string url, bool select = false, string? title = null, CancellationToken cancellationToken = default)
        => AddMediaTrackAsync(MpvTrackType.Video, url, select, title ?? "BiliFluent video", cancellationToken);

    /// <summary>
    /// Adds an audio track with an explicit title and returns its ID. Cancellation throws; failure
    /// leaves the old selection intact. The add and subsequent selection run off the calling thread.
    /// </summary>
    public Task<MpvResult<int>> AddAudioTrackAsync(string url, bool select = true, string? title = null, CancellationToken cancellationToken = default)
        => AddMediaTrackAsync(MpvTrackType.Audio, url, select, title ?? "BiliFluent audio", cancellationToken);

    /// <summary>Selects a video track by its mpv ID. Cancellation throws.</summary>
    public Task<MpvResult> SetVideoTrackAsync(int id, CancellationToken cancellationToken = default)
        => SelectMediaTrackAsync("vid", id, cancellationToken);

    /// <summary>Selects an audio track by its mpv ID. Cancellation throws.</summary>
    public Task<MpvResult> SetAudioTrackAsync(int id, CancellationToken cancellationToken = default)
        => SelectMediaTrackAsync("aid", id, cancellationToken);

    /// <summary>Removes an external video track. The main file's video track cannot be removed.</summary>
    public Task<MpvResult> RemoveVideoTrackAsync(int id, CancellationToken cancellationToken = default)
        => RemoveMediaTrackAsync("video-remove", id, cancellationToken);

    /// <summary>Removes an external audio track.</summary>
    public Task<MpvResult> RemoveAudioTrackAsync(int id, CancellationToken cancellationToken = default)
        => RemoveMediaTrackAsync("audio-remove", id, cancellationToken);

    /// <summary>
    /// Reads video and audio tracks, without changing the existing subtitle-oriented track API.
    /// Runs off the calling thread; cancellation throws.
    /// </summary>
    public Task<MpvResult<IReadOnlyList<MpvMediaTrack>>> GetMediaTracksAsync(CancellationToken cancellationToken = default)
        => Task.Run(ReadMediaTracks, cancellationToken);

    private async Task<MpvResult<int>> AddMediaTrackAsync(MpvTrackType type, string url, bool select, string title, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var work = Task.Run(() => AddMediaTrackCoreAsync(type, url, select, title, operation.Token), CancellationToken.None);
        try
        {
            return await work.WaitAsync(MediaTrackTimeout, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await operation.CancelAsync().ConfigureAwait(false);
            ObserveLateTrackAdd(work);
            return MpvResult.Fail("Mpv | add media track timed out");
        }
        catch (OperationCanceledException)
        {
            await operation.CancelAsync().ConfigureAwait(false);
            ObserveLateTrackAdd(work);
            throw;
        }
    }

    private async Task<MpvResult<int>> AddMediaTrackCoreAsync(MpvTrackType type, string url, bool select, string title, CancellationToken token)
    {
        await _trackAddGate.WaitAsync(token).ConfigureAwait(false);
        int? addedId = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var before = ReadMediaTracks();
            if (before.IsFailed) return MpvResult.Fail(before.ErrorMessage!);
            var ids = before.Value.Where(track => track.Type == type).Select(track => track.Id).ToHashSet();

            // Like subtitle loading, add without selection. Even a command that completes after
            // timeout/cancellation then cannot change playback. Selection follows only on success.
            var command = type == MpvTrackType.Video ? "video-add" : "audio-add";
            var result = await RunTrackCommandAsync([command, url, "auto", title], token).ConfigureAwait(false);
            var after = ReadMediaTracks();
            if (after.IsSuccess)
            {
                addedId = after.Value.FirstOrDefault(track => track.Type == type && track.IsExternal && !ids.Contains(track.Id) && track.Title == title)?.Id;
            }

            token.ThrowIfCancellationRequested();
            if (result.IsFailed) return result;
            if (addedId is not { } id) return MpvResult.Fail("Mpv | added media track was not found");
            if (select)
            {
                var selection = await SelectMediaTrackAsync(type == MpvTrackType.Video ? "vid" : "aid", id, token).ConfigureAwait(false);
                if (selection.IsFailed) return selection;
            }

            addedId = null; // Ownership passes to the caller.
            return id;
        }
        finally
        {
            if (addedId is { } orphan && !IsDisposed)
            {
                await RemoveMediaTrackAsync(type == MpvTrackType.Video ? "video-remove" : "audio-remove", orphan, CancellationToken.None).ConfigureAwait(false);
            }

            _trackAddGate.Release();
        }
    }

    private Task<MpvResult> SelectMediaTrackAsync(string property, int id, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var result = WrapAsResult(SetPropertyString(property, id.ToString(CultureInfo.InvariantCulture)), "Mpv | select media track failed");
            return result.IsSuccess && GetPropertyString(property) != id.ToString(CultureInfo.InvariantCulture)
                ? MpvResult.Fail("Mpv | media track selection did not take effect")
                : result;
        }, token);
    }

    private async Task<MpvResult> RemoveMediaTrackAsync(string command, int id, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        try
        {
            return await Task.Run(() => RunTrackCommandAsync([command, id.ToString(CultureInfo.InvariantCulture)], token), token)
                .WaitAsync(MediaTrackTimeout, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return MpvResult.Fail("Mpv | remove media track timed out");
        }
    }

    private MpvResult<IReadOnlyList<MpvMediaTrack>> ReadMediaTracks()
    {
        if (IsDisposed) return MpvResult.Fail("Mpv | get media tracks failed: client disposed");
        var error = _handle.GetPropertyNode("track-list", out var node);
        if (error != MpvError.Success) return WrapAsResult(error, "Mpv | get media tracks failed");
        if (node is not List<object?> list) return MpvResult.Fail("Mpv | get media tracks failed: invalid node format");
        var tracks = new List<MpvMediaTrack>();
        foreach (var item in list)
        {
            if (item is not Dictionary<string, object?> track) continue;
            var type = track.GetString("type") switch
            {
                "video" => MpvTrackType.Video,
                "audio" => MpvTrackType.Audio,
                _ => MpvTrackType.Unknown,
            };
            if (type != MpvTrackType.Unknown && track.GetInteger("id") is long id)
            {
                tracks.Add(new(checked((int)id), type, track.GetString("title"), track.GetFlag("selected"), track.GetFlag("external")));
            }
        }

        return tracks;
    }

    private async Task<MpvResult> RunTrackCommandAsync(string[] args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (IsDisposed) return MpvResult.Fail("Mpv | media track command failed: client disposed");
        var id = unchecked((ulong)Interlocked.Increment(ref _trackCommandId));
        var completion = new TaskCompletionSource<MpvError>(TaskCreationOptions.RunContinuationsAsynchronously);
        _trackCommands[id] = completion;
        var error = _handle.SendTrackCommand(id, args);
        if (error != MpvError.Success)
        {
            _trackCommands.TryRemove(id, out _);
            return WrapAsResult(error, "Mpv | media track command failed");
        }

        using var registration = token.Register(() =>
        {
            if (!IsDisposed) _handle.AbortTrackCommand(id);
        });
        // Await native completion even after abort: the add gate stays held until the before/after
        // comparison and orphan cleanup are done. The public wait above is independently bounded.
#pragma warning disable VSTHRD003 // This completion belongs to this native command and its reply event.
        return WrapAsResult(await completion.Task.ConfigureAwait(false), "Mpv | media track command failed");
#pragma warning restore VSTHRD003
    }

    private void CompleteTrackCommand(ulong id, int error)
    {
        if (_trackCommands.TryRemove(id, out var completion)) completion.TrySetResult((MpvError)error);
    }

    private void CompletePendingTrackCommands()
    {
        foreach (var (id, _) in _trackCommands) CompleteTrackCommand(id, (int)MpvError.Uninitialized);
    }

    private static void ObserveLateTrackAdd(Task<MpvResult<int>> work)
    {
        _ = work.ContinueWith(static finished => _ = finished.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
