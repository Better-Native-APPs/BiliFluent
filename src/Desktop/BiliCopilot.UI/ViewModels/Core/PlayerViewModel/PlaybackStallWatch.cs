// Copyright (c) BiliFluent. All rights reserved.

#if DEBUG
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;

namespace BiliCopilot.UI.ViewModels.Core;

internal readonly record struct PlaybackStallGcCounts(int Gen0, int Gen1, int Gen2)
{
    internal static PlaybackStallGcCounts Read()
        => new(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
}

internal readonly record struct PlaybackStallRead(long Id, long StartedAt, PlaybackStallGcCounts Collections,
    bool WasPlaying, long PreviousReadAt, double Position);

// Only the probe writes these fields. Publishing an id last gives the watcher a coherent,
// allocation-free snapshot; the final interlocked read rejects a completed/replaced read.
internal sealed class PlaybackStallProbe
{
    private readonly Func<long> _timestamp;
    private readonly Func<PlaybackStallGcCounts> _collections;
    private long _nextId;
    private long _pendingId;
    private long _startedAt;
    private int _gen0;
    private int _gen1;
    private int _gen2;
    private bool _wasPlaying;
    private long _previousReadAt;
    private double _position;
    private bool _lastPlaying;
    private long _lastReadAt;
    private double _lastPosition;

    internal PlaybackStallProbe(Func<long>? timestamp = null, Func<PlaybackStallGcCounts>? collections = null)
    {
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _collections = collections ?? PlaybackStallGcCounts.Read;
    }

    internal void BeginRead()
    {
        var collections = _collections();
        _gen0 = collections.Gen0;
        _gen1 = collections.Gen1;
        _gen2 = collections.Gen2;
        _wasPlaying = _lastPlaying;
        _previousReadAt = _lastReadAt;
        _position = _lastPosition;
        _startedAt = _timestamp();
        Volatile.Write(ref _pendingId, ++_nextId);
    }

    internal void EndRead() => Volatile.Write(ref _pendingId, 0);

    internal void Observe(bool playing, double position, long timestamp)
    {
        _lastPlaying = playing;
        _lastReadAt = timestamp;
        _lastPosition = position;
    }

    internal PlaybackStallRead Read()
    {
        var id = Volatile.Read(ref _pendingId);
        if (id == 0) return default;
        var read = new PlaybackStallRead(id, Volatile.Read(ref _startedAt),
            new(Volatile.Read(ref _gen0), Volatile.Read(ref _gen1), Volatile.Read(ref _gen2)),
            Volatile.Read(ref _wasPlaying), Volatile.Read(ref _previousReadAt), Volatile.Read(ref _position));
        return Interlocked.Read(ref _pendingId) == id ? read : default;
    }

    internal bool IsPending(long id) => Volatile.Read(ref _pendingId) == id;
}

internal enum PlaybackStallDecision
{
    None,
    Dump,
    SkippedGc,
    SkippedSettling,
    SkippedRate,
}

// Shared by all diagnostics sessions, including workers still draining after a new load.
// A failed attempt also consumes its slot: diagnostics never retry that dump.
internal sealed class PlaybackStallDumpBudget(long frequency)
{
    private readonly object _sync = new();
    private readonly long _minimumInterval = frequency * 180;
    private long _lastAttemptAt;
    private int _attempts;

    internal bool TryReserve(long timestamp)
    {
        lock (_sync)
        {
            if (_attempts >= 5 || (_attempts != 0 && timestamp - _lastAttemptAt < _minimumInterval)) return false;
            _lastAttemptAt = timestamp;
            _attempts++;
            return true;
        }
    }
}

internal sealed class PlaybackStallPolicy(PlaybackStallDumpBudget budget, long frequency, long startedAt)
{
    private readonly long _pendingThreshold = frequency * 60 / 1000;
    private readonly long _maximumWakeGap = frequency * 30 / 1000;
    private readonly long _maximumPreviousReadAge = frequency * 100 / 1000;
    private readonly long _settlingInterval = frequency * 5;
    private long _settlingAt = startedAt;
    private long _seenId;

    internal int Stalls { get; private set; }

    internal int SkippedGc { get; private set; }

    internal int SkippedSettling { get; private set; }

    internal int SkippedRate { get; private set; }

    internal void NotifySettling(long timestamp)
    {
        var previous = Volatile.Read(ref _settlingAt);
        while (timestamp > previous)
        {
            var observed = Interlocked.CompareExchange(ref _settlingAt, timestamp, previous);
            if (observed == previous) return;
            previous = observed;
        }
    }

    internal PlaybackStallDecision Observe(long timestamp, long previousWakeAt, PlaybackStallRead read,
        PlaybackStallGcCounts collections)
    {
        if (read.Id == 0 || timestamp - read.StartedAt < _pendingThreshold || read.Id == _seenId)
            return PlaybackStallDecision.None;
        _seenId = read.Id;
        Stalls++;

        if (timestamp - previousWakeAt > _maximumWakeGap || read.Collections != collections)
        {
            SkippedGc++;
            return PlaybackStallDecision.SkippedGc;
        }

        // An inactive/stale probe is settling too; none of its gaps describes steady playback.
        if (!read.WasPlaying || timestamp - read.PreviousReadAt > _maximumPreviousReadAge
            || timestamp - Volatile.Read(ref _settlingAt) < _settlingInterval)
        {
            SkippedSettling++;
            return PlaybackStallDecision.SkippedSettling;
        }

        if (!budget.TryReserve(timestamp))
        {
            SkippedRate++;
            return PlaybackStallDecision.SkippedRate;
        }

        return PlaybackStallDecision.Dump;
    }
}

internal sealed class PlaybackStallWatch
{
    private static readonly PlaybackStallDumpBudget ProcessBudget = new(Stopwatch.Frequency);
    private readonly PlaybackStallProbe _probe = new();
    private readonly PlaybackStallPolicy _policy = new(ProcessBudget, Stopwatch.Frequency, Stopwatch.GetTimestamp());
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _directory;
    private readonly ILogger _logger;
    private readonly Thread _thread;
    private volatile bool _stopped;
    private int _dumps;

    internal PlaybackStallWatch(string directory, ILogger logger)
    {
        _directory = directory;
        _logger = logger;
        _thread = new Thread(Watch) { IsBackground = true, Name = "PlaybackStallWatch" };
    }

#pragma warning disable VSTHRD003 // This TCS belongs to the dedicated background thread, which never needs the dispatcher.
    internal Task Completion => _finished.Task;
#pragma warning restore VSTHRD003

    internal void Start() => _thread.Start();

    internal void Stop() => _stopped = true;

    internal void BeginRead() => _probe.BeginRead();

    internal void EndRead() => _probe.EndRead();

    internal void Observe(bool playing, double position, long timestamp) => _probe.Observe(playing, position, timestamp);

    internal void NotifySettling() => _policy.NotifySettling(Stopwatch.GetTimestamp());

    internal void NotifySettling(long timestamp) => _policy.NotifySettling(timestamp);

    private void Watch()
    {
        try
        {
            var previousWakeAt = Stopwatch.GetTimestamp();
            while (!_stopped)
            {
                Thread.Sleep(10);
                if (_stopped) break;
                var timestamp = Stopwatch.GetTimestamp();
                var read = _probe.Read();
                var decision = _policy.Observe(timestamp, previousWakeAt, read, PlaybackStallGcCounts.Read());
                previousWakeAt = timestamp;
                if (decision != PlaybackStallDecision.Dump || _stopped || !_probe.IsPending(read.Id)) continue;

                if (PlaybackStallDump.TryWrite(_directory, DateTime.UtcNow, out var result, out var error))
                {
                    _dumps++;
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation(
                            "PlaybackStutter | stall-dump file={File} pending-ms={PendingMs} write-ms={WriteMs} bytes={Bytes} pos={Position}",
                            result.FileName, ((timestamp - read.StartedAt) * 1000d / Stopwatch.Frequency).ToString("0.0", CultureInfo.InvariantCulture),
                            result.WriteMilliseconds.ToString("0.0", CultureInfo.InvariantCulture), result.Bytes,
                            read.Position.ToString("0.###", CultureInfo.InvariantCulture));
                    }
                }
                else
                {
                    _logger.LogWarning("PlaybackStutter | stall-dump failed error={Error}", error);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("PlaybackStutter | stall-dump failed error={Error}", ex.GetType().Name);
        }
        finally
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "PlaybackStutter | stall-watch stalls={Stalls} dumps={Dumps} skipped-gc={SkippedGc} skipped-settling={SkippedSettling} skipped-rate={SkippedRate}",
                    _policy.Stalls, _dumps, _policy.SkippedGc, _policy.SkippedSettling, _policy.SkippedRate);
            }
            _finished.TrySetResult();
        }
    }
}

#endif
