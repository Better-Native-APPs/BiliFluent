// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.ViewModels.Core;
using System.ComponentModel;

namespace BiliCopilot.UI.Controls.Core;

/// <summary>
/// Chapter presentation of the full-screen / compact transport: both progress tracks (real and gesture preview)
/// cut into chapter segments, the chapter name in the hover / drag / gesture preview, and the room given to the
/// chapter entry. Display only. Seeking, the passive progress display and input admission stay in the main part.
/// </summary>
public sealed partial class OverlayTransportControls
{
    // Below this bar width even the icon of the chapter entry gives its room back.
    private const double ChapterEntryMinWidth = 520;
    private const double ChapterTitleWideWidth = 180;
    private const double ChapterTitleWidth = 120;

    private ChapterTrackLayer? _chapterTrack;
    private ChapterTrackLayer? _chapterPreviewTrack;
    private ChapterPreview? _chapterPreview;
    private PlayerViewModel? _chapterOwner;

    private bool IsProgressBarShown
        => _loaded && ProgressBarVisible && Visibility == Visibility.Visible && Opacity > 0
            && ViewModel?.IsControlsVisible == true;

    private void InitializeChapters()
    {
        var gap = (Style)Resources["ChapterGapStyle"];
        var highlight = (Style)Resources["ChapterHighlightStyle"];
        _chapterTrack = new ChapterTrackLayer(ProgressSlider, gap, highlight);
        _chapterPreviewTrack = new ChapterTrackLayer(ProgressSliderFake, gap, highlight);
        _chapterPreview = new ChapterPreview(ProgressSlider, _chapterTrack, ChapterTipPopup, ChapterTip, () => CaptureInput().HasValue);
    }

    private void SubscribeChapters(PlayerViewModel? viewModel)
    {
        if (!ReferenceEquals(_chapterOwner, viewModel))
        {
            if (_chapterOwner is not null)
            {
                _chapterOwner.ChapterInitialized -= OnChapterInitialized;
                _chapterOwner.PropertyChanged -= OnChapterOwnerPropertyChanged;
            }

            _chapterOwner = viewModel;
            if (viewModel is not null)
            {
                viewModel.ChapterInitialized += OnChapterInitialized;
                viewModel.PropertyChanged += OnChapterOwnerPropertyChanged;
            }
        }

        UpdateChapters();
    }

    private void OnChapterInitialized(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _chapterOwner)) return;
        if (DispatcherQueue.HasThreadAccess) UpdateChapters();
        else DispatcherQueue.TryEnqueue(UpdateChapters);
    }

    private void OnChapterOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _chapterOwner)) return;
        if (e.PropertyName is nameof(PlayerViewModel.HasChapters) or nameof(PlayerViewModel.IsChapterVisible))
        {
            UpdateChapters();
        }
        else if (e.PropertyName is nameof(PlayerViewModel.PreviewPosition) or nameof(PlayerViewModel.IsPreviewProgressChanging))
        {
            UpdateGesturePreview();
        }
    }

    private void UpdateChapters()
    {
        if (_chapterTrack is null || _chapterPreviewTrack is null) return;
        var owner = _chapterOwner;
        var visible = owner is { HasChapters: true, IsChapterVisible: true };
        var chapters = visible ? owner!.Chapters : null;
        _chapterTrack.SetChapters(chapters);
        _chapterPreviewTrack.SetChapters(chapters);
        if (!visible) _chapterPreview?.Reset();
    }

    // A seek gesture on the video previews a position on the second slider; name its chapter the same way.
    private void UpdateGesturePreview()
    {
        if (_chapterPreview is null || _chapterPreviewTrack is null || _chapterOwner is not { } owner) return;
        if (owner.IsPreviewProgressChanging && IsProgressBarShown && _chapterPreviewTrack.HasChapters)
        {
            _chapterPreview.ShowAt(_chapterPreviewTrack, owner.PreviewPosition);
        }
        else if (!owner.IsPreviewProgressChanging || !IsProgressBarShown)
        {
            _chapterPreview.Hide();
        }
    }

    // Narrow first drops the chapter title, then the entry; the segments on the track always stay.
    private void UpdateChapterLayout(double width, double narrowWidth)
    {
        if (ChapterEntry is null) return;
        ChapterEntry.TitleMaxWidth = width >= 1280 ? ChapterTitleWideWidth : ChapterTitleWidth;
        ChapterEntry.IsTitleVisible = width >= narrowWidth;
        ChapterEntry.IsEntryVisible = width >= ChapterEntryMinWidth;
    }
}
