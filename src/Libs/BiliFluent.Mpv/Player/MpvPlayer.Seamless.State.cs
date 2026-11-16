// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using Richasy.MpvKernel.Player.Models;
using Richasy.MpvKernel.Relay;

namespace Richasy.MpvKernel.Player;

public sealed partial class MpvPlayer
{
    private MpvSeamlessStreamState SelectedSeamlessState(MpvRelayStreamKind kind, MpvMediaTrack? selected)
    {
        if (selected is { } track && _seamlessTracks.TryGetValue((kind, track.Id), out var owned))
        {
            return new(track.Id, track.IsExternal, owned.MinimumSeekTime, owned.Key, owned.Index, owned.Map, _relay?.GetStatistics(owned.Key));
        }

        var initial = kind == MpvRelayStreamKind.Video ? _seamlessInitialVideoId : _seamlessInitialAudioId;
        var known = selected is not null && (selected.Id == initial || !selected.IsExternal);
        var key = known ? _relayCurrent?.GetKey(kind) : null;
        return new(selected?.Id, selected?.IsExternal ?? false, known ? 0 : null, key, null, null, key is null ? null : _relay?.GetStatistics(key));
    }

    private string? SelectedSeamlessKey(MpvRelayStreamKind kind)
    {
        if (Client.IsDisposed || !int.TryParse(Client.GetPropertyString(kind == MpvRelayStreamKind.Video ? "vid" : "aid"), CultureInfo.InvariantCulture, out var id)) return null;
        if (_seamlessTracks.TryGetValue((kind, id), out var owned)) return owned.Key;
        var initial = kind == MpvRelayStreamKind.Video ? _seamlessInitialVideoId : _seamlessInitialAudioId;
        return initial is null || initial == id ? _relayCurrent?.GetKey(kind) : null;
    }

    private void AttachSeamlessHooks()
    {
        if (_seamlessHooksAttached) return;
        _seamlessHooksAttached = true;
        Client.ReachFileLoading += OnSeamlessFileLoading;
        Client.ReachFileEnd += OnSeamlessPlaybackEnded;
        Client.Shutdown += OnSeamlessPlaybackEnded;
        Client.SeekStarted += OnSeamlessSeek;
        Client.PauseChanged += OnSeamlessPause;
        Client.DataNotify += OnSeamlessDataNotify;
    }

    private void OnSeamlessFileLoading(object? sender, EventArgs e)
    {
        CancelPendingSeamless(MpvSeamlessSwitchReason.PlaybackChanged);
        ReleaseSeamlessTracks();
    }

    private void OnSeamlessPlaybackEnded(object? sender, EventArgs e) => CancelPendingSeamless(MpvSeamlessSwitchReason.PlaybackChanged);

    private void OnSeamlessSeek(object? sender, EventArgs e) => CancelPendingSeamless(MpvSeamlessSwitchReason.Seeked);

    private void OnSeamlessPause(object? sender, bool paused)
    {
        if (paused) CancelPendingSeamless(MpvSeamlessSwitchReason.Paused);
    }

    private void OnSeamlessDataNotify(object? sender, MpvClientNotifyEventArgs e)
    {
        if (e.Id == MpvClientEventId.SpeedChanged && e.Data is double speed)
        {
            lock (_seamlessSync)
            {
                if (_seamlessPending is { Speed: > 0 } operation && Math.Abs(operation.Speed - speed) > 0.001) operation.Cancel(MpvSeamlessSwitchReason.SpeedChanged);
            }
        }
    }

    private void CancelPendingSeamless(MpvSeamlessSwitchReason reason)
    {
        lock (_seamlessSync) _seamlessPending?.Cancel(reason);
    }

    private void SubscribeSeamlessRelay(LoopbackMediaRelay relay)
    {
        if (ReferenceEquals(_seamlessSubscribedRelay, relay)) return;
        if (_seamlessSubscribedRelay is { } previous) previous.UpstreamFailed -= OnSeamlessRelayUpstreamFailed;
        _seamlessSubscribedRelay = relay;
        relay.UpstreamFailed += OnSeamlessRelayUpstreamFailed;
    }

    private void OnSeamlessRelayUpstreamFailed(object? sender, RelayUpstreamFailedEventArgs e)
    {
        MpvRelayStreamKind? kind;
        lock (_seamlessSync)
        {
            kind = _seamlessPending is { } pending && (e.Key == pending.PrefetchKey || e.Key == pending.TargetKey)
                ? pending.Kind : e.Key == _seamlessSelectedVideoKey ? MpvRelayStreamKind.Video
                : e.Key == _seamlessSelectedAudioKey ? MpvRelayStreamKind.Audio : null;
        }

        if (kind is { } known) SeamlessRelayUpstreamFailed?.Invoke(this, new(known, e));
    }

    private void ReleaseSeamlessTracks()
    {
        string[] keys;
        lock (_seamlessSync)
        {
            keys = _seamlessTracks.Values.Select(track => track.Key).ToArray();
            _seamlessTracks.Clear();
            _seamlessInitialVideoId = null;
            _seamlessInitialAudioId = null;
            _seamlessSelectedVideoKey = null;
            _seamlessSelectedAudioKey = null;
        }

        foreach (var key in keys) _relay?.Unregister(key);
    }

    private async Task DisposeSeamlessAsync()
    {
        Task[] pending;
        lock (_seamlessSync)
        {
            _seamlessPending?.Cancel(MpvSeamlessSwitchReason.Disposed);
            pending = [.. _seamlessWork.Values, .. _seamlessRetirements];
        }

        await _seamlessLifetime.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(pending).ConfigureAwait(false);
        // A selection already committing when disposal began can have scheduled a retirement.
        lock (_seamlessSync) pending = [.. _seamlessRetirements];
        await Task.WhenAll(pending).ConfigureAwait(false);
        if (_seamlessHooksAttached)
        {
            Client.ReachFileLoading -= OnSeamlessFileLoading;
            Client.ReachFileEnd -= OnSeamlessPlaybackEnded;
            Client.Shutdown -= OnSeamlessPlaybackEnded;
            Client.SeekStarted -= OnSeamlessSeek;
            Client.PauseChanged -= OnSeamlessPause;
            Client.DataNotify -= OnSeamlessDataNotify;
        }

        if (_seamlessSubscribedRelay is { } relay) relay.UpstreamFailed -= OnSeamlessRelayUpstreamFailed;
        ReleaseSeamlessTracks();
        _seamlessHttp?.Dispose();
        _seamlessLifetime.Dispose();
        _seamlessGate.Dispose();
    }
}
