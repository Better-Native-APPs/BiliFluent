// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>Visit history independent of cached page controls and playback ownership.</summary>
internal sealed class NavigationHistory<T> where T : class
{
    private readonly List<T> _entries = [];
    private int _position = -1;

    internal T? Current => _position >= 0 ? _entries[_position] : null;
    internal bool CanGoBack => _position > 0;
    internal bool CanGoForward => _position >= 0 && _position < _entries.Count - 1;

    internal void Navigate(T entry)
    {
        if (EqualityComparer<T>.Default.Equals(Current, entry)) return;
        if (CanGoForward) _entries.RemoveRange(_position + 1, _entries.Count - _position - 1);
        _entries.Add(entry);
        _position = _entries.Count - 1;
    }

    internal void ReplaceCurrent(T entry)
    {
        if (_position < 0) Navigate(entry);
        else _entries[_position] = entry;
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

    /// <summary>Drop an unavailable destination, continuing in the requested direction.</summary>
    internal T? DiscardCurrent(bool back)
    {
        if (_position < 0) return null;
        _entries.RemoveAt(_position);
        _position = _entries.Count == 0 ? -1
            : back ? Math.Max(0, _position - 1) : Math.Min(_position, _entries.Count - 1);
        return Current;
    }
}
