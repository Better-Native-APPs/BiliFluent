// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Dash;
using Richasy.MpvKernel.Player.Models;
using Richasy.MpvKernel.Relay;

namespace Richasy.MpvKernel.Player;

public sealed partial class MpvPlayer
{
    private static readonly TimeSpan SeamlessPreparationTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SeamlessRetireDelay = TimeSpan.FromSeconds(3.5);
    private readonly Lock _seamlessSync = new();
    private readonly SemaphoreSlim _seamlessGate = new(1, 1);
    private readonly CancellationTokenSource _seamlessLifetime = new();
    private readonly Dictionary<long, Task<MpvSeamlessSwitchResult>> _seamlessWork = [];
    private readonly Dictionary<(MpvRelayStreamKind Kind, int Id), SeamlessTrack> _seamlessTracks = [];
    private readonly List<Task> _seamlessRetirements = [];
#pragma warning disable CA2213 // Each operation is disposed by its worker's finally, including superseded ones.
    private SeamlessOperation? _seamlessPending;
#pragma warning restore CA2213
    private HttpClient? _seamlessHttp;
    private LoopbackMediaRelay? _seamlessSubscribedRelay;
    private long _seamlessRequestId;
    private bool _seamlessHooksAttached;
    private int? _seamlessInitialVideoId;
    private int? _seamlessInitialAudioId;
    private string? _seamlessSelectedVideoKey;
    private string? _seamlessSelectedAudioKey;

    /// <summary>
    /// Progress on the seamless worker thread. Applications should marshal to their UI context
    /// and ignore events for superseded request IDs. Existing player events are unchanged.
    /// </summary>
    public event EventHandler<MpvSeamlessSwitchEventArgs>? SeamlessSwitchProgress;

    /// <summary>Node failure of a prepared or selected seamless stream, on the relay worker thread.</summary>
    public event EventHandler<MpvRelayUpstreamFailedEventArgs>? SeamlessRelayUpstreamFailed;

    /// <summary>
    /// Prepares and selects a full-file external audio track. The current and following two
    /// fragments are prefetched through the relay. Small opening gaps are filled up to the
    /// measured 165291-byte limit; distant positions use normal Range seeks, never large prefixes.
    /// The caller is never blocked and failures retain
    /// the old stream. Paused, live and caller-marked unsupported media return NotSupported.
    /// A newer audio or video request cancels the previous pending request. Cancellation is returned
    /// as a Cancelled result, not thrown. No reload, buffering policy or application UI is performed.
    /// </summary>
    public Task<MpvSeamlessSwitchResult> SwitchAudioAsync(MpvSeamlessTarget target, CancellationToken cancellationToken = default)
        => StartSeamlessSwitch(target, MpvRelayStreamKind.Audio, cancellationToken);

    /// <summary>
    /// Prepares a virtual MP4 video beginning at a future starting keyframe. At 1x the boundary is
    /// at least one second ahead; at 2x, 2.5 seconds. Two contiguous fragments are prefetched. The
    /// worker selects near boundary minus 20 ms and retains the old external track for 3.5 seconds.
    /// Pause, seek, speed change, another request or cancellation invalidate an uncommitted switch.
    /// Failure keeps playing the old stream and returns a reason for application-side reload policy.
    /// </summary>
    public Task<MpvSeamlessSwitchResult> SwitchVideoAsync(MpvSeamlessTarget target, CancellationToken cancellationToken = default)
        => StartSeamlessSwitch(target, MpvRelayStreamKind.Video, cancellationToken);

    /// <summary>
    /// Reads the actual selected track IDs/external flags and combines them with the owned relay
    /// keys, original fragment indexes, earliest seek times and statistics. Runs off the caller;
    /// cancellation throws. Unknown external tracks have no relay mapping or earliest-time promise.
    /// </summary>
    public async Task<MpvResult<MpvSeamlessPlaybackState>> GetSeamlessPlaybackStateAsync(CancellationToken cancellationToken = default)
    {
        var tracks = await Client.GetMediaTracksAsync(cancellationToken).ConfigureAwait(false);
        if (tracks.IsFailed) return MpvResult.Fail(tracks.ErrorMessage!);
        var video = tracks.Value.FirstOrDefault(track => track.Type == MpvTrackType.Video && track.IsSelected);
        var audio = tracks.Value.FirstOrDefault(track => track.Type == MpvTrackType.Audio && track.IsSelected);
        lock (_seamlessSync)
        {
            _seamlessInitialVideoId ??= video?.Id;
            _seamlessInitialAudioId ??= audio?.Id;
            var selectedVideo = SelectedSeamlessState(MpvRelayStreamKind.Video, video);
            var selectedAudio = SelectedSeamlessState(MpvRelayStreamKind.Audio, audio);
            _seamlessSelectedVideoKey = selectedVideo.FragmentIndex is null ? null : selectedVideo.RelayKey;
            _seamlessSelectedAudioKey = selectedAudio.FragmentIndex is null ? null : selectedAudio.RelayKey;
            return new MpvSeamlessPlaybackState(selectedVideo, selectedAudio, _seamlessPending is not null);
        }
    }

    /// <summary>Gets statistics for the currently selected owned track, falling back to the original file's key.</summary>
    public RelayStreamStatistics? GetSelectedRelayStatistics(MpvRelayStreamKind kind)
    {
        lock (_seamlessSync)
        {
            return _relay is { } relay && SelectedSeamlessKey(kind) is { } key ? relay.GetStatistics(key) : null;
        }
    }

    /// <summary>Changes a selected stream's node without replacing the file, timeline or track ID.</summary>
    public bool TrySwitchSelectedRelayNode(MpvRelayStreamKind kind, string upstreamUrl)
    {
        lock (_seamlessSync)
        {
            return !_isDisposed && _relay is { } relay && SelectedSeamlessKey(kind) is { } key && relay.SwitchNode(key, upstreamUrl);
        }
    }

    /// <summary>
    /// Replaces the selected stream's node table. Virtual tracks check the original-file length,
    /// not the virtual length. Cancellation throws and leaves the old table intact.
    /// </summary>
    public Task<RelayUpdateResult> TryUpdateSelectedRelayUrlsAsync(MpvRelayStreamKind kind, MpvRelayAddresses addresses,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        lock (_seamlessSync)
        {
            return !_isDisposed && _relay is { } relay && SelectedSeamlessKey(kind) is { } key
                ? relay.UpdateUrlsAsync(key, addresses.Upstreams, addresses.Current, headers, cancellationToken)
                : Task.FromResult(RelayUpdateResult.UnknownKey);
        }
    }

    private Task<MpvSeamlessSwitchResult> StartSeamlessSwitch(MpvSeamlessTarget target, MpvRelayStreamKind kind, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_seamlessSync)
        {
            if (_isDisposed) return Task.FromResult(new MpvSeamlessSwitchResult(MpvSeamlessSwitchStatus.Failed, MpvSeamlessSwitchReason.Disposed));
            AttachSeamlessHooks();
            _seamlessPending?.Cancel(MpvSeamlessSwitchReason.Superseded);
            var operation = new SeamlessOperation(++_seamlessRequestId, kind, token, _seamlessLifetime.Token);
            _seamlessPending = operation;
            var work = Task.Run(() => RunSeamlessSwitchAsync(target, operation), CancellationToken.None);
            _seamlessWork.Add(operation.Id, work);
            return work;
        }
    }

    private async Task<MpvSeamlessSwitchResult> RunSeamlessSwitchAsync(MpvSeamlessTarget target, SeamlessOperation operation)
    {
        var entered = false;
        var preparation = Stopwatch.StartNew();
        MpvSeamlessSwitchResult result;
        try
        {
            await _seamlessGate.WaitAsync(operation.Token).ConfigureAwait(false);
            entered = true;
            NotifySeamlessProgress(operation, MpvSeamlessSwitchPhase.Preparing);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
            deadline.CancelAfter(SeamlessPreparationTimeout);
            result = await PrepareAndSwitchAsync(target, operation, deadline.Token, preparation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var reason = operation.CancelReason;
            result = new(reason == MpvSeamlessSwitchReason.None ? MpvSeamlessSwitchStatus.Failed : MpvSeamlessSwitchStatus.Cancelled,
                reason == MpvSeamlessSwitchReason.None ? MpvSeamlessSwitchReason.PreparationTimeout : reason, Preparation: preparation.Elapsed, BoundaryWait: operation.WaitElapsed);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException or HttpRequestException or IOException or InvalidOperationException)
        {
            _logger.LogWarning("Playback | seamless preparation failed kind={Kind} error={Error}", operation.Kind, ex.GetType().Name);
            result = new(ex is NotSupportedException ? MpvSeamlessSwitchStatus.NotSupported : MpvSeamlessSwitchStatus.Failed,
                MpvSeamlessSwitchReason.PreparationFailed, Preparation: preparation.Elapsed);
        }
        finally
        {
            if (entered) _seamlessGate.Release();
            lock (_seamlessSync)
            {
                if (ReferenceEquals(_seamlessPending, operation)) _seamlessPending = null;
                _seamlessWork.Remove(operation.Id);
            }

            operation.Dispose();
        }

        NotifySeamlessProgress(operation, MpvSeamlessSwitchPhase.Finished, result);
        return result;
    }

    private void NotifySeamlessProgress(SeamlessOperation operation, MpvSeamlessSwitchPhase phase, MpvSeamlessSwitchResult? result = null)
    {
        operation.Phase = phase;
        SeamlessSwitchProgress?.Invoke(this, new(operation.Id, operation.Kind, phase, operation.Boundary, result) { RelayKey = operation.TargetKey });
    }

    private sealed record SeamlessTrack(int Id, MpvRelayStreamKind Kind, string Key, double MinimumSeekTime, DashIndex Index, RelayVirtualOffsetMap? Map);

    private sealed class SeamlessOperation : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly CancellationToken _caller;
        private int _reason;

        public SeamlessOperation(long id, MpvRelayStreamKind kind, CancellationToken caller, CancellationToken lifetime)
        {
            Id = id;
            Kind = kind;
            _caller = caller;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime);
        }

        public long Id { get; }
        public MpvRelayStreamKind Kind { get; }
        public CancellationToken Token => _cancellation.Token;
        public MpvSeamlessSwitchPhase Phase { get; set; }
        public double? Boundary { get; set; }
        public string? PrefetchKey { get; set; }
        public string? TargetKey { get; set; }
        public RelayedSource? MainSource { get; set; }
        public double Speed { get; set; }
        public TimeSpan WaitElapsed { get; set; }
        public bool Committed { get; set; }
        public MpvSeamlessSwitchReason CancelReason => (MpvSeamlessSwitchReason)Volatile.Read(ref _reason) is var reason && reason != MpvSeamlessSwitchReason.None
            ? reason : _caller.IsCancellationRequested ? MpvSeamlessSwitchReason.CallerCancelled : MpvSeamlessSwitchReason.None;

        public void Cancel(MpvSeamlessSwitchReason reason)
        {
            if (Committed) return;
            Interlocked.CompareExchange(ref _reason, (int)reason, 0);
            _cancellation.Cancel();
        }

        public void Dispose() => _cancellation.Dispose();
    }
}
