// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>Visit history independent of cached page controls and playback ownership.</summary>
internal sealed class NavigationHistory<T>(Func<T?, T?, bool>? sameVisit = null) where T : class
{
    private readonly List<T> _entries = [];
    private readonly Func<T?, T?, bool> _sameVisit = sameVisit ?? EqualityComparer<T?>.Default.Equals;
    private int _position = -1;

    internal T? Current => _position >= 0 ? _entries[_position] : null;
    internal bool CanGoBack => _position > 0;
    internal bool CanGoForward => _position >= 0 && _position < _entries.Count - 1;

    internal void Navigate(T entry)
    {
        if (_sameVisit(Current, entry))
        {
            ReplaceCurrent(entry);
            return;
        }
        if (CanGoForward) _entries.RemoveRange(_position + 1, _entries.Count - _position - 1);
        _entries.Add(entry);
        _position = _entries.Count - 1;
    }

    internal void ReplaceCurrent(T entry)
    {
        if (_position < 0) Navigate(entry);
        else
        {
            _entries[_position] = entry;
            // Keep the updated visit, including its latest resume position, without adjacent duplicates.
            while (_position > 0 && _sameVisit(_entries[_position - 1], entry))
                _entries.RemoveAt(--_position);
            while (CanGoForward && _sameVisit(entry, _entries[_position + 1]))
                _entries.RemoveAt(_position + 1);
        }
    }

    internal bool TryMove(bool back, out T? entry)
    {
        if (back ? CanGoBack : CanGoForward)
        {
            _position += back ? -1 : 1;
            entry = Current;
            return true;
        }

        entry = null;
        return false;
    }

    /// <summary>Discard unavailable playback, retaining its page instead of restoring another player.</summary>
    internal void DiscardCurrent(T page) => ReplaceCurrent(page);
}
