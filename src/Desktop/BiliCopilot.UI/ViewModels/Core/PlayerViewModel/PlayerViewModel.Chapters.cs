// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Media;
using Richasy.MpvKernel.Core.Enums;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private long _chapterRevision;
    private long _requestedVideoChapterRevision = -1;
    private bool _usingBilibiliChapters;
    private bool _chapterFileLoaded;
    private IReadOnlyList<VideoChapter>? _pendingVideoChapters;

    [ObservableProperty]
    public partial ChapterItemViewModel? CurrentChapter { get; private set; }

    [ObservableProperty]
    public partial bool HasChapters { get; private set; }

    [RelayCommand]
    private Task JumpToChapterAsync(ChapterItemViewModel? chapter)
    {
        if (IsPlaybackClosing || IsPlayerInputCancellationPending || IsConnecting
            || Player is null || Player.PlaybackState == MpvPlayerState.Idle
            || chapter is null || !Chapters.Contains(chapter))
        {
            return Task.CompletedTask;
        }

        return SeekUserAsync(chapter.Start);
    }

    private void ResetChapters()
    {
        _chapterRevision++;
        _usingBilibiliChapters = false;
        _chapterFileLoaded = false;
        _pendingVideoChapters = null;
        _chapterFlagsCount = -1;
        CurrentChapter = null;
        HasChapters = false;
        Chapters.Clear();
        ChapterInitialized?.Invoke(this, EventArgs.Empty);
    }

    private void InitializeVideoChapters()
    {
        if (!IsCurrentPlayback(_mediaVersion) || _snapshot.Type != BiliMediaType.Video
            || Connector is not VideoConnectorViewModel video
            || _requestedVideoChapterRevision == _chapterRevision
            || !long.TryParse(video._part.Identifier.Id, out var cid))
        {
            return;
        }

        _requestedVideoChapterRevision = _chapterRevision;
        _ = OwnWork(LoadVideoChaptersAsync(video._view.Information.Identifier, cid, _mediaVersion, _chapterRevision, _activeMediaToken));
    }

    private bool IsCurrentChapterRequest(long mediaVersion, long revision)
        => IsCurrentPlayback(mediaVersion) && revision == _chapterRevision;

    private async Task LoadVideoChaptersAsync(MediaIdentifier video, long cid, long mediaVersion, long revision, CancellationToken cancellationToken)
    {
        try
        {
            var chapters = await this.Get<IPlayerService>().GetVideoChaptersAsync(video, cid, cancellationToken);
            if (!IsCurrentChapterRequest(mediaVersion, revision)) return;
            if (chapters.Count >= 2)
            {
                _pendingVideoChapters = chapters;
                TryApplyVideoChapters();
                return;
            }
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested && IsCurrentChapterRequest(mediaVersion, revision))
            {
                logger.LogWarning(ex, "Failed to load video chapters; using embedded chapters instead.");
            }
        }

        if (IsCurrentChapterRequest(mediaVersion, revision)) await VerifyChaptersAsync();
    }

    private bool TryApplyVideoChapters()
    {
        // Chapter controls use duration to position markers, so stage early API results.
        if (!_chapterFileLoaded || _pendingVideoChapters is not { Count: >= 2 } chapters || Player is not { Duration: > 0 }) return false;
        var items = chapters.OrderBy(chapter => chapter.Start)
            .Select((chapter, index) => new ChapterItemViewModel(index, chapter.Title, chapter.Start, chapter.End, chapter.ImageUrl, chapter.Type))
            .ToArray();
        _pendingVideoChapters = null;
        SetChapters(items, fromBilibili: true);
        return true;
    }

    private void SetChapters(IReadOnlyList<ChapterItemViewModel> chapters, bool fromBilibili)
    {
        _usingBilibiliChapters = fromBilibili;
        Chapters.Clear();
        for (var i = 0; i < chapters.Count; i++) Chapters.Add(chapters[i]);
        HasChapters = Chapters.Count >= 2;
        _chapterFlagsCount = -1;
        UpdateChapterEnds();
        UpdateChapterFlags(Player.Position);
        ChapterInitialized?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateChapterEnds()
    {
        if (_usingBilibiliChapters) return;
        for (var i = 0; i < Chapters.Count; i++)
        {
            var chapter = Chapters[i];
            var end = i + 1 < Chapters.Count ? Chapters[i + 1].Start : Math.Max(chapter.Start, Player?.Duration ?? 0);
            if (chapter.End == end) continue;
            chapter.End = end;
            _chapterFlagsCount = -1;
        }
    }

    private void UpdateChapterDuration()
    {
        if (TryApplyVideoChapters()) return;
        UpdateChapterEnds();
        UpdateChapterFlags(Player.Position);
        ChapterInitialized?.Invoke(this, EventArgs.Empty);
        if (_chapterFileLoaded && Chapters.Count == 0) _ = OwnWork(VerifyChaptersAsync());
    }

    private void OnChapterFileLoaded(object? sender, EventArgs e)
    {
        var mediaVersion = _mediaVersion;
        var revision = _chapterRevision;
        queue.TryEnqueue(() =>
        {
            if (IsCurrentChapterRequest(mediaVersion, revision) && ReferenceEquals(sender, Client))
            {
                _chapterFileLoaded = true;
                _ = OwnWork(VerifyChaptersAsync());
            }
        });
    }
}
