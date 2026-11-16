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
    // Below these card widths the chapter title, then the whole chapter entry, give their room back.
    private const double ChapterTitleMinWidth = 740;
    private const double ChapterEntryMinWidth = 560;

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

    private void OnTransportCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ChapterEntry is null) return;

        // Narrow first drops the chapter title, then the entry; the segments on the track always stay.
        ChapterEntry.IsTitleVisible = e.NewSize.Width >= ChapterTitleMinWidth;
        ChapterEntry.IsEntryVisible = e.NewSize.Width >= ChapterEntryMinWidth;
    }
}
