// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>Copies launch preferences; history never owns a player, host, or native stream.</summary>
internal static class MediaNavigationSnapshot
{
    internal static MediaSnapshot Copy(MediaSnapshot source)
    {
        var copy = source.Type switch
        {
            BiliMediaType.Video => new MediaSnapshot(source.Video!, source.IsPrivate),
            BiliMediaType.Pgc => new MediaSnapshot(source.Season, source.Episode),
            BiliMediaType.Live => new MediaSnapshot(source.Live!, source.PreferQuality, source.PreferEotf),
            _ => throw new NotSupportedException("This media cannot be restored in the main player."),
        };
        CopyPreferences(source, copy);
        return copy;
    }

    internal static void CopyPreferences(MediaSnapshot source, MediaSnapshot target)
    {
        target.PreferQuality = source.PreferQuality;
        target.PreferEotf = source.PreferEotf;
        target.PreferAudioLanguageId = source.PreferAudioLanguageId;
        target.PreferAudioQualityId = source.PreferAudioQualityId;
        target.IsAutomaticAudioStandardOnly = source.IsAutomaticAudioStandardOnly;
        target.PreferPart = source.PreferPart;
        target.Playlist = source.Playlist is { } playlist ? [.. playlist] : null;
        target.StartPosition = source.Type == BiliMediaType.Live ? null : source.StartPosition;
    }

    internal static void SetPosition(MediaSnapshot snapshot, double? position)
    {
        if (snapshot.Type != BiliMediaType.Live && position is { } value && double.IsFinite(value) && value >= 0)
            snapshot.StartPosition = value;
    }
}
