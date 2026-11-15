// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Components;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models;
using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.Services.WatchRecords;

/// <summary>
/// In-memory index of what the signed-in account has watched, keyed by video aid.
/// It feeds the watched marks of the dynamic page, because the dynamic interface itself does not report progress.
/// </summary>
/// <remarks>
/// Two things fill it: pages of the server watch history, pulled in the background while the dynamic page loads,
/// and the positions the player reports during this run. Nothing is written to disk, and switching account empties it.
/// Only ordinary videos are indexed. How a bangumi history record maps to a dynamic card is not known, so those are left out.
/// </remarks>
public sealed class WatchRecordIndex
{
    // History pages hold 20 records. The first pages cover what a fresh dynamic feed shows,
    // and every further feed page digs a little deeper until the cap.
    private const int StartPages = 3;
    private const int MorePages = 2;
    private const int MaxDeepPages = 15;
    private const int MaxRecords = 300;

    private static readonly TimeSpan _startInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan _retryInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(20);

    private readonly IViewHistoryService _history;
    private readonly IBiliTokenResolver _tokens;
    private readonly AccountViewModel _account;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<WatchRecordIndex> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly Dictionary<string, Record> _records = new(StringComparer.Ordinal);

    private long _generation;
    private long _lastStartTick;
    private long _retryAfterTick;
    private bool _hasCursor;
    private long _deepCursor;
    private int _deepPages;
    private bool _isExhausted;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchRecordIndex"/> class.
    /// </summary>
    public WatchRecordIndex(
        IViewHistoryService history,
        IBiliTokenResolver tokens,
        AccountViewModel account,
        DispatcherQueue dispatcher,
        ILogger<WatchRecordIndex> logger)
    {
        _history = history;
        _tokens = tokens;
        _account = account;
        _dispatcher = dispatcher;
        _logger = logger;
        _account.AccountChanged += OnAccountChanged;
    }

    /// <summary>
    /// Raised on the UI thread after records changed. The argument is the aid that changed, or null when many did.
    /// </summary>
    public event Action<string?>? Changed;

    /// <summary>
    /// Number of videos in the index.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _records.Count;
            }
        }
    }

    /// <summary>
    /// Whether the index knows the video.
    /// </summary>
    public bool Contains(string? aid)
    {
        if (string.IsNullOrEmpty(aid))
        {
            return false;
        }

        lock (_gate)
        {
            return _records.ContainsKey(aid);
        }
    }

    /// <summary>
    /// Picks the watch progress of a video: the indexed record when there is one, otherwise what the card itself reported.
    /// </summary>
    /// <param name="aid">Video aid.</param>
    /// <param name="fromCard">Progress the dynamic card came with, if any.</param>
    public VideoWatchProgress? Resolve(string? aid, VideoWatchProgress? fromCard)
    {
        if (!string.IsNullOrEmpty(aid))
        {
            Record record;
            bool found;
            lock (_gate)
            {
                found = _records.TryGetValue(aid, out record);
            }

            if (found && VideoWatchProgress.FromHistory(record.Progress, record.Duration) is { } progress)
            {
                return progress;
            }
        }

        return fromCard;
    }

    /// <summary>
    /// Notes a position the player has just reported, so the mark shows up without waiting for the next history pull.
    /// Safe to call from any thread.
    /// </summary>
    /// <param name="aid">Video aid.</param>
    /// <param name="position">Position in seconds.</param>
    /// <param name="duration">Duration in seconds of the part being played.</param>
    public void RecordPlayback(string? aid, double position, double duration)
    {
        if (string.IsNullOrEmpty(aid) || double.IsNaN(position) || position < 1)
        {
            return;
        }

        var record = new Record((long)position, double.IsNaN(duration) || duration < 0 ? 0 : (long)duration, DateTimeOffset.Now);
        lock (_gate)
        {
            if (_records.TryGetValue(aid, out var existing) && existing.Progress == record.Progress && existing.Duration == record.Duration)
            {
                return;
            }

            _records[aid] = record;
        }

        RaiseChanged(aid);
    }

    /// <summary>
    /// Pulls watch history for a dynamic feed that has just loaded a page. It never throws and never shows anything:
    /// signed out, throttled, or failed all end quietly.
    /// </summary>
    /// <param name="feedStart">True for the first page of a feed, false for a later page.</param>
    public async Task SyncAsync(bool feedStart)
    {
        long generation;
        lock (_gate)
        {
            generation = _generation;
        }

        await _sync.WaitAsync();
        try
        {
            if (_tokens.GetToken() is null)
            {
                return;
            }

            var now = Environment.TickCount64;
            long offset;
            int pages;
            bool fromStart;
            lock (_gate)
            {
                if (generation != _generation || now < _retryAfterTick)
                {
                    return;
                }

                fromStart = (feedStart || !_hasCursor) && (_lastStartTick == 0 || now - _lastStartTick >= (long)_startInterval.TotalMilliseconds);
                if (fromStart)
                {
                    offset = 0;
                    pages = StartPages;
                }
                else if (feedStart || !_hasCursor || _isExhausted || _deepPages >= MaxDeepPages || _records.Count >= MaxRecords)
                {
                    return;
                }
                else
                {
                    offset = _deepCursor;
                    pages = MorePages;
                }
            }

            await PullAsync(generation, offset, pages, fromStart, now);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                // An account switch cancels the pull on purpose. That is not a failure.
                if (generation != _generation)
                {
                    return;
                }

                _retryAfterTick = Environment.TickCount64 + (long)_retryInterval.TotalMilliseconds;
            }

            // The exception type only: messages may carry request details.
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning("Moment | watch-index failed error={Error}", ex.GetType().Name);
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>
    /// Writes the one diagnostic line of a dynamic feed load: how many video cards it had,
    /// how many came with the server's own progress flag, and how many the index knew.
    /// </summary>
    public void ReportFeedLoad(int cards, int serverField, int matched)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Moment | watch-marks cards={Cards} server-field={ServerField} history-index={HistoryIndex} matched={Matched}",
                cards,
                serverField,
                Count,
                matched);
        }
    }

    private async Task PullAsync(long generation, long offset, int pages, bool fromStart, long startTick)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_account.AccountCancellationToken);
        cancellation.CancelAfter(_requestTimeout);
        var pulled = 0;
        var changed = false;
        var exhausted = false;
        try
        {
            while (pulled < pages)
            {
                var group = await _history.GetViewHistoryAsync(ViewHistoryTabType.Video, offset, cancellation.Token);
                pulled++;
                lock (_gate)
                {
                    if (generation != _generation)
                    {
                        return;
                    }

                    changed |= Merge(group?.Videos);
                }

                var next = group?.Offset ?? 0;
                if (next == 0 || next == offset)
                {
                    exhausted = true;
                    break;
                }

                offset = next;
            }
        }
        finally
        {
            // Pages that did arrive still count when a later one fails.
            if (changed)
            {
                RaiseChanged(null);
            }
        }

        int count;
        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            if (fromStart)
            {
                _lastStartTick = startTick == 0 ? 1 : startTick;
            }

            // A pull from the start must not move a cursor that is already deeper.
            if (!fromStart || !_hasCursor)
            {
                _hasCursor = true;
                _deepCursor = offset;
                _deepPages = fromStart ? pulled : _deepPages + pulled;
                _isExhausted = exhausted;
            }

            count = _records.Count;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Moment | watch-index kind={Kind} pages={Pages} records={Records}", fromStart ? "start" : "more", pulled, count);
        }
    }

    // Call with the gate held.
    private bool Merge(IList<VideoInformation>? videos)
    {
        if (videos is null)
        {
            return false;
        }

        var changed = false;
        foreach (var video in videos)
        {
            var aid = video.Identifier.Id;
            var progress = video.GetExtensionIfNotNull<int?>(VideoExtensionDataId.Progress) ?? 0;
            if (string.IsNullOrEmpty(aid) || aid == "0" || progress == 0)
            {
                continue;
            }

            var record = new Record(progress, video.Duration ?? 0, video.GetExtensionIfNotNull<DateTimeOffset?>(VideoExtensionDataId.CollectTime) ?? default);

            // The newer record wins, so a position played in this run is not replaced by an older history page.
            if (_records.TryGetValue(aid, out var existing) && (existing.At > record.At || existing == record))
            {
                continue;
            }

            _records[aid] = record;
            changed = true;
        }

        return changed;
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        bool hadRecords;
        lock (_gate)
        {
            _generation++;
            hadRecords = _records.Count > 0;
            _records.Clear();
            _lastStartTick = 0;
            _retryAfterTick = 0;
            _hasCursor = false;
            _deepCursor = 0;
            _deepPages = 0;
            _isExhausted = false;
        }

        if (hadRecords)
        {
            RaiseChanged(null);
        }
    }

    private void RaiseChanged(string? aid)
    {
        if (_dispatcher.HasThreadAccess)
        {
            Changed?.Invoke(aid);
        }
        else
        {
            _ = _dispatcher.TryEnqueue(() => Changed?.Invoke(aid));
        }
    }

    private readonly record struct Record(long Progress, long Duration, DateTimeOffset At);
}
