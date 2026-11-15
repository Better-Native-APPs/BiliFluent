// Copyright (c) BiliFluent. All rights reserved.

using Richasy.BiliKernel.Models.Danmaku;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// Selects timed commands without depending on a player, dispatcher, or UI collection.
/// </summary>
public sealed class CommandDanmakuTimeline
{
    public const int DefaultDurationMs = 8000;

    private readonly Window[] _windows;
    private readonly bool[] _dismissed;
    private long _lowerBound;
    private long _upperBound;
    private bool _selectionInvalid = true;

    /// <summary>
    /// Precomputes ordered candidates at each boundary. Construct this off the position-update path.
    /// Equal-priority commands are ordered by progress, then by their original input index.
    /// </summary>
    public CommandDanmakuTimeline(IReadOnlyList<CommandDanmakuTimelineEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var snapshot = entries.ToArray();
        _dismissed = new bool[snapshot.Length];
        var events = new List<Boundary>();
        for (var index = 0; index < snapshot.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = snapshot[index];
            if (GetPriority(entry.Kind) == int.MaxValue) continue;
            var duration = entry.DurationMs is > 0 ? entry.DurationMs.Value : DefaultDurationMs;
            events.Add(new Boundary(entry.ProgressMs, index, true));
            events.Add(new Boundary((long)entry.ProgressMs + duration, index, false));
        }

        events.Sort(static (left, right) =>
        {
            var comparison = left.PositionMs.CompareTo(right.PositionMs);
            return comparison != 0 ? comparison : left.IsStart.CompareTo(right.IsStart);
        });
        var active = new SortedSet<int>(Comparer<int>.Create((left, right) =>
        {
            var comparison = GetPriority(snapshot[left].Kind).CompareTo(GetPriority(snapshot[right].Kind));
            if (comparison != 0) return comparison;
            comparison = snapshot[left].ProgressMs.CompareTo(snapshot[right].ProgressMs);
            return comparison != 0 ? comparison : left.CompareTo(right);
        }));
        var windows = new List<Window>();
        for (var index = 0; index < events.Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = events[index].PositionMs;
            do
            {
                var boundary = events[index++];
                if (boundary.IsStart) active.Add(boundary.Index);
                else active.Remove(boundary.Index);
            }
            while (index < events.Count && events[index].PositionMs == position);

            windows.Add(new Window(position, active.ToArray()));
        }

        _windows = windows.ToArray();
    }

    public int FirstActiveIndex { get; private set; } = -1;

    public int SecondActiveIndex { get; private set; } = -1;

    /// <summary>
    /// Selects at most two commands in [progress, progress + duration). Returns true only when
    /// the selection changes. An unchanged window is an allocation-free constant-time return;
    /// crossing a boundary or seeking uses a binary search, never a scan of the commands.
    /// </summary>
    public bool Update(long positionMs)
    {
        if (_windows.Length == 0 || (!_selectionInvalid && positionMs >= _lowerBound && positionMs < _upperBound))
            return false;

        var low = 0;
        var high = _windows.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (_windows[middle].PositionMs <= positionMs) low = middle + 1;
            else high = middle;
        }

        var index = low - 1;
        _lowerBound = index < 0 ? long.MinValue : _windows[index].PositionMs;
        _upperBound = low == _windows.Length ? long.MaxValue : _windows[low].PositionMs;
        _selectionInvalid = false;
        var first = index < 0 ? -1 : _windows[index].FirstIndex;
        var second = index < 0 ? -1 : _windows[index].SecondIndex;
        if (first == FirstActiveIndex && second == SecondActiveIndex) return false;
        FirstActiveIndex = first;
        SecondActiveIndex = second;
        return true;
    }

    /// <summary>
    /// Suppresses an input entry until Reset. Replacement candidates are resolved here,
    /// on the explicit dismissal path, rather than during playback position updates.
    /// </summary>
    public bool Dismiss(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _dismissed.Length);
        if (_dismissed[index]) return false;
        _dismissed[index] = true;
        foreach (var window in _windows)
        {
            if (window.FirstIndex != index && window.SecondIndex != index) continue;
            window.FirstIndex = -1;
            window.SecondIndex = -1;
            foreach (var candidate in window.Candidates)
            {
                if (_dismissed[candidate]) continue;
                if (window.FirstIndex < 0) window.FirstIndex = candidate;
                else
                {
                    window.SecondIndex = candidate;
                    break;
                }
            }
        }

        _selectionInvalid = true;
        return true;
    }

    /// <summary>Starts another playback of the same entries, including previously dismissed ones.</summary>
    public void Reset()
    {
        Array.Clear(_dismissed);
        foreach (var window in _windows) window.ResetSelection();
        FirstActiveIndex = -1;
        SecondActiveIndex = -1;
        _selectionInvalid = true;
    }

    private static int GetPriority(CommandDanmakuKind kind)
        => kind switch
        {
            CommandDanmakuKind.Grade or CommandDanmakuKind.Vote => 0,
            CommandDanmakuKind.Attention => 1,
            CommandDanmakuKind.Link => 2,
            CommandDanmakuKind.Up => 3,
            _ => int.MaxValue,
        };

    private readonly record struct Boundary(long PositionMs, int Index, bool IsStart);

    private sealed class Window
    {
        public Window(long positionMs, int[] candidates)
        {
            PositionMs = positionMs;
            Candidates = candidates;
            ResetSelection();
        }

        public long PositionMs { get; }

        public int[] Candidates { get; }

        public int FirstIndex { get; set; }

        public int SecondIndex { get; set; }

        public void ResetSelection()
        {
            FirstIndex = Candidates.Length > 0 ? Candidates[0] : -1;
            SecondIndex = Candidates.Length > 1 ? Candidates[1] : -1;
        }
    }
}

/// <summary>One input interval; a missing or nonpositive duration uses the eight-second default.</summary>
public readonly record struct CommandDanmakuTimelineEntry(int ProgressMs, int? DurationMs, CommandDanmakuKind Kind);
