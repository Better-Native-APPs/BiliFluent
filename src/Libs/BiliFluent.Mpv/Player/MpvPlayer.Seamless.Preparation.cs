// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Richasy.MpvKernel.Dash;
using Richasy.MpvKernel.Player.Models;
using Richasy.MpvKernel.Relay;

namespace Richasy.MpvKernel.Player;

public sealed partial class MpvPlayer
{
    private const int SeamlessMaximumHeadBytes = 1024 * 1024;
    private const int SeamlessMaximumFragmentBytes = 64 * 1024 * 1024;
    // Bundled mpv reads small forward seeks instead of issuing a new HTTP Range. The
    // smallest observed direct-seek gap is 165291 bytes (142811 still read forward).
    // Use that measured upper bound; larger gaps must seek, never preload a large prefix.
    private const int SeamlessAudioOpeningGapLimit = 165_291;

    private async Task<MpvSeamlessSwitchResult> PrepareAndSwitchAsync(MpvSeamlessTarget target, SeamlessOperation operation, CancellationToken preparationToken, Stopwatch preparation)
    {
        if (target.Restrictions != MpvSeamlessRestrictions.None) return new(MpvSeamlessSwitchStatus.NotSupported, MpvSeamlessSwitchReason.RestrictedFormat);
        if (target.Initialization is not { } initialization || target.IndexRange is not { } indexRange || indexRange.Last >= SeamlessMaximumHeadBytes)
            return new(MpvSeamlessSwitchStatus.NotSupported, MpvSeamlessSwitchReason.MissingMetadata);
        if (Client.GetProperty("pause", out bool paused) == MpvError.Success && paused) return new(MpvSeamlessSwitchStatus.NotSupported, MpvSeamlessSwitchReason.Paused);
        if (!ReadSeamlessTimeline(out var position, out var speed)) return new(MpvSeamlessSwitchStatus.NotSupported, MpvSeamlessSwitchReason.PlaybackUnavailable);
        operation.Speed = speed;
        operation.MainSource = _relayCurrent;
        LoopbackMediaRelay? relay;
        lock (_relaySync) relay = EnsureRelay();
        if (relay is null) return new(MpvSeamlessSwitchStatus.NotSupported, MpvSeamlessSwitchReason.RelayUnavailable);
        SubscribeSeamlessRelay(relay);
        _seamlessHttp ??= new(new SocketsHttpHandler { UseProxy = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        var before = await Client.GetMediaTracksAsync(preparationToken).ConfigureAwait(false);
        if (before.IsFailed) return new(MpvSeamlessSwitchStatus.Failed, MpvSeamlessSwitchReason.TrackOperationFailed);
        var video = before.Value.FirstOrDefault(track => track.Type == MpvTrackType.Video && track.IsSelected);
        var audio = before.Value.FirstOrDefault(track => track.Type == MpvTrackType.Audio && track.IsSelected);
        var previous = operation.Kind == MpvRelayStreamKind.Video ? video : audio;
        string? previousKey;
        lock (_seamlessSync)
        {
            _seamlessInitialVideoId ??= video?.Id;
            _seamlessInitialAudioId ??= audio?.Id;
            previousKey = SelectedSeamlessKey(operation.Kind);
        }

        if (previous is null) return new(MpvSeamlessSwitchStatus.NotSupported, MpvSeamlessSwitchReason.PlaybackUnavailable);
        var nodes = target.Upstreams.ToArray();
        var headers = target.Headers is null ? null : new Dictionary<string, string>(target.Headers, StringComparer.OrdinalIgnoreCase);
        operation.PrefetchKey = "sp" + operation.Id;
        operation.TargetKey = (operation.Kind == MpvRelayStreamKind.Video ? "sv" : "sa") + operation.Id;
        var fetchUrl = relay.Register(operation.PrefetchKey, new() { Upstreams = nodes, Current = target.Current, Headers = headers });
        int? addedId = null;
        try
        {
            var (head, length) = await FetchSeamlessRangeAsync(fetchUrl, new(0, indexRange.Last), preparationToken).ConfigureAwait(false);
            var index = DashIndex.Parse(head, initialization, indexRange);
            if (index.EndOffset > length) throw new InvalidDataException("DASH fragment references exceed the original file.");
            var first = 0;
            var count = 0;
            byte[] fragments = [];
            byte[]? audioOpening = null;
            var openingPreparation = TimeSpan.Zero;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                CheckSeamlessContinuity(operation, out position);
                first = operation.Kind == MpvRelayStreamKind.Video ? FindSeamlessVideoFragment(index, position, speed) : index.FragmentAt(position);
                if (first < 0) return new(MpvSeamlessSwitchStatus.NotSupported, MpvSeamlessSwitchReason.NoKeyFrame, Preparation: preparation.Elapsed);
                count = Math.Min(operation.Kind == MpvRelayStreamKind.Video ? 2 : 3, index.Fragments.Count - first);
                var fragmentRange = new DashByteRange(index.Fragments[first].Offset, index.Fragments[first + count - 1].EndOffset - 1);
                if (fragmentRange.Length > SeamlessMaximumFragmentBytes) return new(MpvSeamlessSwitchStatus.NotSupported, MpvSeamlessSwitchReason.RestrictedFormat, Preparation: preparation.Elapsed);
                var fetched = await FetchSeamlessRangeAsync(fetchUrl, fragmentRange, preparationToken).ConfigureAwait(false);
                if (fetched.Length != length) throw new InvalidDataException("DASH file length changed during preparation.");
                fragments = fetched.Bytes;
                CheckSeamlessContinuity(operation, out position);
                if (operation.Kind == MpvRelayStreamKind.Audio || index.Fragments[first].Time >= position + SeamlessVideoLead(speed)) break;
                if (attempt == 2) return new(MpvSeamlessSwitchStatus.Failed, MpvSeamlessSwitchReason.MissedBoundary, Preparation: preparation.Elapsed);
            }

            if (operation.Kind == MpvRelayStreamKind.Audio)
            {
                // Cache only the observed small forward-read window. At distant positions
                // mpv opens at zero, then requests the current fragment directly by Range.
                var openingEnd = index.Fragments[first].Offset;
                if (openingEnd > head.LongLength && openingEnd - head.LongLength <= SeamlessAudioOpeningGapLimit)
                {
                    var openingClock = Stopwatch.StartNew();
                    var opening = await FetchSeamlessRangeAsync(fetchUrl, new(head.LongLength, openingEnd - 1), preparationToken).ConfigureAwait(false);
                    if (opening.Length != length) throw new InvalidDataException("DASH file length changed during opening preload.");
                    audioOpening = opening.Bytes;
                    openingPreparation = openingClock.Elapsed;
                }
            }

            var preparationStats = relay.GetStatistics(operation.PrefetchKey);
            var current = preparationStats is { } stats ? Array.FindIndex(nodes, node => node.Url == stats.Current.Url) : target.Current;
            if (current < 0) throw new InvalidOperationException("Prepared node is no longer in the target table.");
            RelayVirtualOffsetMap? map = null;
            long preloadedBytes;
            string targetUrl;
            if (operation.Kind == MpvRelayStreamKind.Video)
            {
                var rebuilt = index.BuildHead(first);
                map = new(rebuilt.Length, fragments.Length, index.Fragments[first].Offset, length);
                preloadedBytes = map.PrefixLength;
                operation.Boundary = index.Fragments[first].Time;
                targetUrl = relay.RegisterVirtualMp4(operation.TargetKey, new()
                {
                    Upstreams = nodes, Current = current, Headers = headers, Head = rebuilt, Fragments = fragments,
                    FirstFragmentOffset = index.Fragments[first].Offset, UpstreamLength = length,
                });
            }
            else
            {
                var preloaded = new List<RelayPreload> { new(0, head) };
                if (audioOpening is not null) preloaded.Add(new(head.LongLength, audioOpening));
                var currentStart = index.Fragments[first].Offset;
                var overlap = checked((int)Math.Clamp(head.LongLength - currentStart, 0, fragments.LongLength));
                if (overlap < fragments.Length) preloaded.Add(new(currentStart + overlap, fragments.AsMemory(overlap)));
                preloadedBytes = preloaded.Sum(part => (long)part.Data.Length);
                if (preloadedBytes > SeamlessMaximumFragmentBytes)
                    return new(MpvSeamlessSwitchStatus.NotSupported, MpvSeamlessSwitchReason.RestrictedFormat, Preparation: preparation.Elapsed);
                targetUrl = relay.RegisterPreloaded(operation.TargetKey, new()
                {
                    Upstreams = nodes, Current = current, Headers = headers, Length = length,
                    Preloaded = preloaded,
                });
            }

            var title = string.IsNullOrWhiteSpace(target.Title) ? "BiliFluent " + operation.Kind : target.Title;
            NotifySeamlessProgress(operation, MpvSeamlessSwitchPhase.Adding);
            var added = operation.Kind == MpvRelayStreamKind.Video
                ? await Client.AddVideoTrackAsync(targetUrl, false, title, preparationToken).ConfigureAwait(false)
                : await Client.AddAudioTrackAsync(targetUrl, false, title, preparationToken).ConfigureAwait(false);
            if (added.IsFailed) return new(MpvSeamlessSwitchStatus.Failed, MpvSeamlessSwitchReason.TrackOperationFailed, Preparation: preparation.Elapsed);
            addedId = added.Value;
            preparation.Stop();
            if (operation.Kind == MpvRelayStreamKind.Video)
            {
                NotifySeamlessProgress(operation, MpvSeamlessSwitchPhase.Waiting);
                var wait = await WaitSeamlessBoundaryAsync(operation).ConfigureAwait(false);
                position = wait.Position;
                if (wait.Reason != MpvSeamlessSwitchReason.None)
                    return new(MpvSeamlessSwitchStatus.Failed, wait.Reason, Preparation: preparation.Elapsed, BoundaryWait: operation.WaitElapsed);
            }
            else
            {
                CheckSeamlessContinuity(operation, out position);
                if (position < index.Fragments[first].Time || position >= index.Fragments[first + count - 1].Time + index.Fragments[first + count - 1].Duration)
                    return new(MpvSeamlessSwitchStatus.Failed, MpvSeamlessSwitchReason.MissedBoundary, Preparation: preparation.Elapsed);
                operation.Boundary = position;
            }

            operation.Token.ThrowIfCancellationRequested();
            NotifySeamlessProgress(operation, MpvSeamlessSwitchPhase.Selecting);
            // Already on the seamless worker: one property command avoids an extra thread-pool
            // scheduling hop in the narrow video selection window. File loading stays async.
            operation.Token.ThrowIfCancellationRequested();
            var property = operation.Kind == MpvRelayStreamKind.Video ? "vid" : "aid";
            var selectedId = addedId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (Client.SetPropertyString(property, selectedId) != MpvError.Success || Client.GetPropertyString(property) != selectedId)
                return new(MpvSeamlessSwitchStatus.Failed, MpvSeamlessSwitchReason.TrackOperationFailed, Preparation: preparation.Elapsed, BoundaryWait: operation.WaitElapsed);
            lock (_seamlessSync)
            {
                operation.Committed = true;
                _seamlessTracks[(operation.Kind, addedId.Value)] = new(addedId.Value, operation.Kind, operation.TargetKey,
                    operation.Kind == MpvRelayStreamKind.Video ? operation.Boundary!.Value : 0, index, map);
                if (operation.Kind == MpvRelayStreamKind.Video) _seamlessSelectedVideoKey = operation.TargetKey;
                else _seamlessSelectedAudioKey = operation.TargetKey;
                if (previous.IsExternal)
                {
                    _seamlessRetirements.RemoveAll(task => task.IsCompleted);
                    _seamlessRetirements.Add(Task.Run(() => RetireSeamlessTrackAsync(previous.Id, operation.Kind, previousKey, operation.MainSource), CancellationToken.None));
                }
            }

            return new(MpvSeamlessSwitchStatus.Switched, MpvSeamlessSwitchReason.None, addedId.Value, operation.Boundary, position,
                preparation.Elapsed, operation.WaitElapsed, preparationStats?.Failovers ?? 0)
            {
                PreloadedBytes = preloadedBytes,
                AudioOpeningGapBytes = operation.Kind == MpvRelayStreamKind.Audio ? Math.Max(0, index.Fragments[first].Offset - head.LongLength) : 0,
                AdditionalOpeningBytes = audioOpening?.LongLength ?? 0,
                OpeningPreparation = openingPreparation,
            };
        }
        finally
        {
            relay.Unregister(operation.PrefetchKey);
            if (!operation.Committed)
            {
                if (addedId is { } orphan && !Client.IsDisposed)
                {
                    if (Client.GetPropertyString(operation.Kind == MpvRelayStreamKind.Video ? "vid" : "aid") == orphan.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    {
                        if (operation.Kind == MpvRelayStreamKind.Video) await Client.SetVideoTrackAsync(previous.Id).ConfigureAwait(false);
                        else await Client.SetAudioTrackAsync(previous.Id).ConfigureAwait(false);
                    }

                    if (operation.Kind == MpvRelayStreamKind.Video) await Client.RemoveVideoTrackAsync(orphan).ConfigureAwait(false);
                    else await Client.RemoveAudioTrackAsync(orphan).ConfigureAwait(false);
                }

                relay.Unregister(operation.TargetKey);
            }
        }
    }

    private async Task<(byte[] Bytes, long Length)> FetchSeamlessRangeAsync(string url, DashByteRange range, CancellationToken token)
    {
        if (range.Length > SeamlessMaximumFragmentBytes) throw new InvalidDataException("DASH preload exceeds its memory budget.");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(range.First, range.Last);
        using var response = await _seamlessHttp!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange is not { From: { } first, To: { } last, Length: { } length }
            || first != range.First || last != range.Last || length <= last || response.Content.Headers.ContentLength != range.Length)
            throw new InvalidDataException("DASH preload response does not match its requested range.");
        var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        if (bytes.LongLength != range.Length) throw new InvalidDataException("DASH preload body is incomplete.");
        return (bytes, length);
    }

    private async Task RetireSeamlessTrackAsync(int id, MpvRelayStreamKind kind, string? key, RelayedSource? mainSource)
    {
        try
        {
            await Task.Delay(SeamlessRetireDelay, _seamlessLifetime.Token).ConfigureAwait(false);
            if (!ReferenceEquals(mainSource, _relayCurrent) || Client.IsDisposed) return;
            var tracks = await Client.GetMediaTracksAsync(_seamlessLifetime.Token).ConfigureAwait(false);
            if (tracks.IsFailed) return;
            var old = tracks.Value.FirstOrDefault(track => track.Id == id && track.Type == (kind == MpvRelayStreamKind.Video ? MpvTrackType.Video : MpvTrackType.Audio));
            if (old is { IsSelected: true } || old is { IsExternal: false }) return;
            if (old is not null)
            {
                var removed = kind == MpvRelayStreamKind.Video
                    ? await Client.RemoveVideoTrackAsync(id, _seamlessLifetime.Token).ConfigureAwait(false)
                    : await Client.RemoveAudioTrackAsync(id, _seamlessLifetime.Token).ConfigureAwait(false);
                if (removed.IsFailed) return;
            }

            lock (_seamlessSync) _seamlessTracks.Remove((kind, id));
            if (key is not null) _relay?.Unregister(key);
        }
        catch (OperationCanceledException)
        {
            // Player disposal releases every remaining owned key after the operations stop.
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning("Playback | seamless track retirement failed kind={Kind} id={Id} error={Error}", kind, id, ex.GetType().Name);
        }
    }
}
