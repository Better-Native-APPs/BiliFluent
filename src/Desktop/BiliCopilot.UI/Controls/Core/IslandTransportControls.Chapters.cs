// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.ViewModels.Core;
using System.ComponentModel;

namespace BiliCopilot.UI.Controls.Core;

/// <summary>
/// Chapter presentation of the ordinary (island) transport: the progress track cut into chapter segments,
/// the chapter name in the hover / drag preview, and the room given to the chapter entry.
/// Display only. Seeking, the passive progress display and input admission stay in the main part.
/// </summary>
public sealed partial class IslandTransportControls
{
    // The width the middle content (the danmaku box) keeps next to a chapter title, and next to the bare icon.
    private const double MiddleContentWidth = 220;
    private const double MiddleContentMinWidth = 170;

    // The entry outside its title: margin, padding, icon and spacing. The bare icon takes the button width and the margin.
    private const double ChapterTitleChrome = 50;
    private const double ChapterIconWidth = 44;
    private const double ChapterTitleMinWidth = 56;
    private const double ChapterTitleMaxWidth = 140;

    private ChapterTrackLayer? _chapterTrack;
    private ChapterPreview? _chapterPreview;
    private PlayerViewModel? _chapterOwner;

    private void InitializeChapters()
    {
        _chapterTrack = new ChapterTrackLayer(ProgressSlider, (Style)Resources["ChapterGapStyle"], (Style)Resources["ChapterHighlightStyle"]);
        _chapterPreview = new ChapterPreview(ProgressSlider, _chapterTrack, ChapterTipPopup, ChapterTip, () => !IsInputBlocked);
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
        if (ReferenceEquals(sender, _chapterOwner)
            && e.PropertyName is nameof(PlayerViewModel.HasChapters) or nameof(PlayerViewModel.IsChapterVisible))
        {
            UpdateChapters();
        }
    }

    private void UpdateChapters()
    {
        if (_chapterTrack is null) return;
        var owner = _loaded ? _chapterOwner : null;
        var visible = owner is { HasChapters: true, IsChapterVisible: true };
        _chapterTrack.SetChapters(visible ? owner!.Chapters : null);
        if (!visible) _chapterPreview?.Reset();
    }

    // The slot is a star column: its width follows the card, never the entry, so this cannot feed back.
    private void OnMiddleSlotSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ChapterEntry is null) return;

        // Narrow first drops the chapter title, then the entry; the segments on the track always stay.
        var title = Math.Floor(e.NewSize.Width - MiddleContentWidth - ChapterTitleChrome);
        ChapterEntry.IsEntryVisible = e.NewSize.Width - MiddleContentMinWidth >= ChapterIconWidth;
        ChapterEntry.IsTitleVisible = title >= ChapterTitleMinWidth;
        if (title >= ChapterTitleMinWidth) ChapterEntry.TitleMaxWidth = Math.Min(ChapterTitleMaxWidth, title);
    }
}
