// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Hosting;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Pages.Overlay;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class NavigationViewModel
{
    private readonly NavigationHistory<NavigationEntry> _history = new();
    private PageEntry? _mainPage;
    private PageEntry? _overlayPage;
    private MediaSnapshot? _restoringPlayer;
    private Task? _localBack;

    public bool CanGoBack => _history.CanGoBack || this.Get<AppViewModel>().HasEmbeddedWork || this.Get<AppViewModel>().HasLiveWork;
    public bool CanGoForward => _history.CanGoForward;

    /// <summary>Every user Back entry has the same local-presentation-then-history priority.</summary>
    internal Task GoBackAsync()
    {
        var app = this.Get<AppViewModel>();
        if (app.IsPlaybackStopping) return Task.CompletedTask;
        if (!CanGoBack && LastNavigationTask.IsCompleted) return Task.CompletedTask;
        if (_localBack is { IsCompleted: false }) return _localBack;
        if (app.EmbeddedPlayer is { IsPlaybackClosing: false } player
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
        if (ReferenceEquals(snapshot, _restoringPlayer)) return open();
        SaveCurrentPlayback();
        ++_navigationSequence;
        // Replacement close remains inside the existing shared route, not a new navigation lease.
        var navigation = _navigationOperations.Enqueue(() => Task.CompletedTask, open);
        OnPropertyChanged(nameof(LastNavigationTask));
        _ = ObserveNavigationAsync(navigation);
        return navigation;
    }

    internal void RecordOpenedPlayer(MediaSnapshot snapshot)
    {
        var entry = new NavigationEntry(_mainPage, _overlayPage, snapshot);
        if (ReferenceEquals(snapshot, _restoringPlayer)) _history.ReplaceCurrent(entry);
        else _history.Navigate(entry);
        NotifyHistoryAvailability();
    }

    private void SaveCurrentPlayback()
    {
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
        if (!_history.TryMove(back, out var entry))
        {
            _navFrame?.Focus(FocusState.Programmatic);
            NotifyHistoryAvailability();
            return;
        }

        var app = this.Get<AppViewModel>();
        while (entry is not null)
        {
            RestorePages(entry);
            NotifyHistoryAvailability();
            if (entry.Playback is not { } playback) return;
            // Rapid Back/Forward still moves the cursor, but must not start a superseded player.
            if (sequence != _navigationSequence || app.IsPlaybackStopping) return;
            var snapshot = MediaNavigationSnapshot.Copy(playback);
            _restoringPlayer = snapshot;
            try
            {
                await app.RestoreMainPlayerAsync(snapshot);
                if (sequence != _navigationSequence || app.IsPlaybackStopping) return;
                if (app.HasRestoredMainPlayer) return;
            }
            catch (Exception) when (sequence == _navigationSequence && !app.IsPlaybackStopping)
            {
                // A dispatch/source failure is an unavailable visit; the real close below
                // still decides whether its newly-created owner can be discarded.
            }
            finally { _restoringPlayer = null; }

            // Opening already reports the original error. Cleanup must really finish before
            // dropping this visit or revealing another page; failed close retains ownership/history.
            await app.CloseEmbeddedPlayerAsync();
            entry = _history.DiscardCurrent(back);
            NotifyHistoryAvailability();
            app.ShowTipCommand.Execute(("无法返回到之前的播放内容，已跳过该记录。", InfoType.Error));
        }
    }

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

    private void NotifyHistoryAvailability()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    private sealed record PageEntry(Type PageType, object? Parameter);
    private sealed record NavigationEntry(PageEntry? MainPage, PageEntry? OverlayPage, MediaSnapshot? Playback);
}
