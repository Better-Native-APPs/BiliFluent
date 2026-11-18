// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Hosting;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Pages.Overlay;
using Richasy.WinUIKernel.Share.Base;
using System.Runtime.CompilerServices;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class NavigationViewModel
{
    private readonly NavigationHistory<NavigationEntry> _history = new(IsSameVisit);
    private PageEntry? _mainPage;
    private PageEntry? _overlayPage;
    private readonly ConditionalWeakTable<MediaSnapshot, HistoryRestore> _historyRestores = new();
    private CancellationTokenSource? _playbackRestoreCancellation;
    private NavigationEntry? _unavailablePlayback;
    private Task? _localBack;

    public bool CanGoBack => _history.CanGoBack || this.Get<AppViewModel>().HasEmbeddedWork || this.Get<AppViewModel>().HasLiveWork;
    public bool CanGoForward => _history.CanGoForward;

    /// <summary>Every user Back entry has the same local-presentation-then-history priority.</summary>
    internal Task GoBackAsync()
    {
        var app = this.Get<AppViewModel>();
        if (app.IsPlaybackStopping) return Task.CompletedTask;
        if (app.TryDismissMainNavigationTransient()) return Task.CompletedTask;
        if (!CanGoBack && LastNavigationTask.IsCompleted) return Task.CompletedTask;
        if (_localBack is { IsCompleted: false }) return _localBack;
        if (_playbackRestoreCancellation is null && app.EmbeddedPlayer is { IsPlaybackClosing: false } player
            && (player.IsExtraPanelVisible || player.Presentation != PlayerPresentationMode.Normal))
        {
            _localBack = player.BackToDefaultModeCommand.ExecuteAsync(null);
            _ = ObserveNavigationAsync(_localBack);
            return _localBack;
        }

        return QueueNavigation(sequence => MoveHistoryAsync(back: true, sequence));
    }

    internal Task GoForwardAsync()
        => !CanGoForward && LastNavigationTask.IsCompleted ? Task.CompletedTask
            : QueueNavigation(sequence => MoveHistoryAsync(back: false, sequence));

    /// <summary>The existing application open still captures identity and owns route admission.</summary>
    internal Task OpenPlayerAsync(MediaSnapshot snapshot, Func<Task> open)
    {
        // A history restore calls OpenMainPlayerAsync, but must not enqueue behind itself.
        if (_playbackRestoreCancellation is not null && _historyRestores.TryGetValue(snapshot, out var restore)
            && restore.Sequence == _navigationSequence) return open();
        _historyRestores.Remove(snapshot); // A later explicit open may reuse a previously restored snapshot.
        SaveCurrentPlayback();
        SupersedeNavigation();
        // Replacement close remains inside the existing shared route, not a new navigation lease.
        var navigation = _navigationOperations.Enqueue(() => Task.CompletedTask, open);
        OnPropertyChanged(nameof(LastNavigationTask));
        _ = ObserveNavigationAsync(navigation);
        return navigation;
    }

    internal void RecordOpenedPlayer(MediaSnapshot snapshot)
    {
        var entry = new NavigationEntry(_mainPage, _overlayPage, snapshot);
        if (_historyRestores.TryGetValue(snapshot, out var restore))
        {
            // Open remains application-owned after its observation is cancelled. A late create
            // must still be recognizable as a superseded restore, not a fresh user visit.
            if (restore.Sequence != _navigationSequence) return;
            _history.ReplaceCurrent(entry);
        }
        else _history.Navigate(entry);
        NotifyHistoryAvailability();
    }

    internal void NoteUnavailablePlayback(MediaSnapshot snapshot)
    {
        if (_history.Current is { Playback: { } current } entry && MediaNavigationSnapshot.IsSameVisit(current, snapshot))
            _unavailablePlayback = entry;
    }

    internal void DiscardReleasedUnavailablePlayback()
    {
        // A known load failure is removed only after the actual owner release, never on a failed close.
        if (_unavailablePlayback is { } entry && ReferenceEquals(_history.Current, entry))
            _history.DiscardCurrent(entry with { Playback = null });
        _unavailablePlayback = null;
        NotifyHistoryAvailability();
    }

    private void SaveCurrentPlayback()
    {
        // A half-open player has not reached the saved position yet. Cancellation must preserve it.
        if (_playbackRestoreCancellation is not null) return;
        if (_history.Current is { Playback: not null } entry && this.Get<AppViewModel>().CaptureMainPlayerNavigation() is { } snapshot)
            _history.ReplaceCurrent(entry with { Playback = snapshot });
    }

    private void RecordCurrentPage()
    {
        var entry = new NavigationEntry(_mainPage, _overlayPage, null);
        // Initial feature selection replaces the empty shell instead of creating a blank Back step.
        if (_history.Current is { MainPage: null, OverlayPage: null, Playback: null }) _history.ReplaceCurrent(entry);
        else _history.Navigate(entry);
        NotifyHistoryAvailability();
    }

    private async Task MoveHistoryAsync(bool back, long sequence)
    {
        if (!_history.TryMove(back, out var entry) || entry is null)
        {
            _navFrame?.Focus(FocusState.Programmatic);
            NotifyHistoryAvailability();
            return;
        }

        var app = this.Get<AppViewModel>();
        RestorePages(entry);
        NotifyHistoryAvailability();
        if (entry.Playback is not { } playback) return;
        // Rapid Back/Forward still moves the cursor, but must not start a superseded player.
        if (sequence != _navigationSequence || app.IsPlaybackStopping) return;
        var snapshot = MediaNavigationSnapshot.Copy(playback);
        using var cancellation = new CancellationTokenSource();
        _playbackRestoreCancellation = cancellation;
        _historyRestores.Add(snapshot, new(sequence));
        try
        {
            var result = await app.RestoreMainPlayerAsync(snapshot, cancellation.Token);
            if (result != PlaybackNavigationRestoreResult.Failed || cancellation.IsCancellationRequested
                || sequence != _navigationSequence || app.IsPlaybackStopping) return;

            // Opening already reports the original error. Cleanup must really finish before
            // dropping this visit; failed close retains ownership/history. Never chain media restores.
            await app.CloseEmbeddedPlayerAsync();
            _history.DiscardCurrent(entry with { Playback = null });
            NotifyHistoryAvailability();
            if (sequence == _navigationSequence && !app.IsPlaybackStopping)
                app.ShowTipCommand.Execute(("无法返回到之前的播放内容，已移除该记录。", InfoType.Error));
        }
        finally
        {
            _playbackRestoreCancellation = null;
            NotifyHistoryAvailability();
        }
    }

    private long SupersedeNavigation()
    {
        var sequence = ++_navigationSequence;
        // This source cancels only the observation. The successor uses the existing real close/route barrier.
#pragma warning disable VSTHRD103
        _playbackRestoreCancellation?.Cancel();
#pragma warning restore VSTHRD103
        return sequence;
    }

    private static bool IsSameVisit(NavigationEntry? left, NavigationEntry? right)
        => ReferenceEquals(left, right) || left is not null && right is not null
            && left.MainPage == right.MainPage && left.OverlayPage == right.OverlayPage
            && MediaNavigationSnapshot.IsSameVisit(left.Playback, right.Playback);

    private void RestorePages(NavigationEntry entry)
    {
        if (entry.MainPage is { } main) NavigateToCore(main.PageType, main.Parameter);
        else TryClearOverlayPages();
        if (entry.OverlayPage is { } overlay)
        {
            NavigateToOverCore(overlay.PageType, overlay.Parameter);
            if (overlay.PageType == typeof(SearchPage) && overlay.Parameter is string keyword)
                SearchCore(keyword);
        }
        else
        {
            TryClearOverlayPages(); // Preserve Back's cancellation of the page being left.
            CheckSubPageStatus();
        }
        _navFrame?.Focus(FocusState.Programmatic);
    }

    internal void NotifyHistoryAvailability()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    private sealed record PageEntry(Type PageType, object? Parameter);
    private sealed record NavigationEntry(PageEntry? MainPage, PageEntry? OverlayPage, MediaSnapshot? Playback);
    private sealed record HistoryRestore(long Sequence);
}
