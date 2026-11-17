// Copyright (c) BiliFluent. All rights reserved.

using Richasy.BiliKernel.Models.Media;
using Richasy.MpvKernel.Dash;
using Richasy.MpvKernel.Player.Models;
using Richasy.MpvKernel.Relay;

namespace BiliCopilot.UI.Resolvers;

/// <summary>Preferences and resolved IDs are separate: an automatic reload keeps the stream that actually plays.</summary>
internal readonly record struct OnDemandSelectionPreferences(
    int? VideoQuality,
    string? AudioQualityId,
    string? AudioLanguageId,
    bool StandardAudioOnly = false);

internal readonly record struct OnDemandSelectionRequest(
    OnDemandSelectionPreferences Preferences,
    int? ResolvedVideoQuality = null,
    string? ResolvedAudioQualityId = null);

internal readonly record struct OnDemandSelectionLoad(long Generation, OnDemandPlaybackSelection Selection);

/// <summary>An immutable proposal. Describing it does not change the playing streams or the menu.</summary>
internal sealed record OnDemandPlaybackSelection(
    PlayerFormatInformation Format,
    AudioSelections AudioSelections,
    OnDemandSelectionPreferences Preferences,
    SeamlessStreamDescription? Video,
    SeamlessStreamDescription? Audio)
{
    public OnDemandSelectionRequest ForReload()
        => new(Preferences, Format.Quality, AudioSelections.QualityId);

    public bool HasSameStream(PlaybackStreamKind kind, OnDemandPlaybackSelection other)
    {
        var left = kind == PlaybackStreamKind.Video ? Video : Audio;
        var right = kind == PlaybackStreamKind.Video ? other.Video : other.Audio;
        return left?.Segment.Id == right?.Segment.Id && left?.Segment.CodecId == right?.Segment.CodecId
            && left?.Segment.Codecs == right?.Segment.Codecs
            && (kind == PlaybackStreamKind.Video || AudioSelections.LanguageId == other.AudioSelections.LanguageId);
    }

    public OnDemandPlaybackSelection WithStream(PlaybackStreamKind kind, OnDemandPlaybackSelection selected)
        => kind == PlaybackStreamKind.Video
            ? this with { Format = selected.Format, Video = selected.Video }
            : this with { AudioSelections = selected.AudioSelections, Audio = selected.Audio };
}

/// <summary>Signed addresses stay in memory; only node categories may be logged.</summary>
internal sealed class SeamlessStreamDescription
{
    private SeamlessStreamDescription(DashSegmentInformation segment, IReadOnlyList<StreamCandidate> candidates,
        int current, MpvSeamlessTarget? target)
    {
        Segment = segment;
        Candidates = candidates;
        Current = current;
        Target = target;
    }

    public DashSegmentInformation Segment { get; }

    public IReadOnlyList<StreamCandidate> Candidates { get; }

    public int Current { get; }

    public StreamCandidate? CurrentCandidate => Current >= 0 && Current < Candidates.Count ? Candidates[Current] : null;

    public MpvSeamlessTarget? Target { get; }

    public static SeamlessStreamDescription? Create(DashSegmentInformation? segment, PlayerFormatInformation? format,
        AudioQualityKind audioKind, string? title, IReadOnlyDictionary<string, string> headers, bool excludeP2P,
        Func<string, bool> isP2P, CdnHostHealth health, DateTimeOffset now, Func<string, bool> isTried)
    {
        if (segment is null) return null;
        var candidates = StreamCandidates.Build(segment.BaseUrl, segment.BackupUrls, excludeP2P, isP2P, health, now);
        var current = candidates.Select((candidate, index) => (candidate, index))
            .FirstOrDefault(item => !isTried(item.candidate.Url)).index;
        MpvSeamlessTarget? target = null;
        if (candidates.Count > 0 && candidates.All(candidate => RelayUpstream.IsSupported(candidate.Url)))
        {
            target = new()
            {
                Upstreams = candidates.Select(candidate => new RelayUpstream(candidate.Url, candidate.Category)).ToArray(),
                Current = current,
                Headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
                Initialization = DashByteRange.TryParse(segment.Initialization, out var initialization) ? initialization : null,
                IndexRange = DashByteRange.TryParse(segment.IndexRange, out var indexRange) ? indexRange : null,
                Title = title,
                Restrictions = GetRestrictions(format, audioKind, segment.Codecs),
            };
        }

        return new(segment, candidates, current, target);
    }

    public SeamlessStreamDescription WithCurrentUrl(string? url)
    {
        var index = Candidates.Select((candidate, candidateIndex) => (candidate, candidateIndex))
            .Where(item => item.candidate.Url == url).Select(item => (int?)item.candidateIndex).FirstOrDefault();
        if (index is not { } current) return this;
        var target = Target is { } previous ? new MpvSeamlessTarget
        {
            Upstreams = previous.Upstreams,
            Current = current,
            Headers = previous.Headers,
            Initialization = previous.Initialization,
            IndexRange = previous.IndexRange,
            Title = previous.Title,
            Restrictions = previous.Restrictions,
        } : null;
        return new(Segment, Candidates, current, target);
    }

    internal static MpvSeamlessRestrictions GetRestrictions(PlayerFormatInformation? format, AudioQualityKind audioKind, string codecs)
    {
        var restrictions = MpvSeamlessRestrictions.None;
        // The on-demand API identifies HDR and Dolby Vision with qn 125 and 126, even without EOTF.
        if (format is { IsHdr: true } or { Quality: 125 }) restrictions |= MpvSeamlessRestrictions.Hdr;
        if (format is { Quality: 126 } || audioKind == AudioQualityKind.Dolby
            || codecs.Contains("dvh1", StringComparison.OrdinalIgnoreCase) || codecs.Contains("dvhe", StringComparison.OrdinalIgnoreCase))
            restrictions |= MpvSeamlessRestrictions.Dolby;
        if (audioKind == AudioQualityKind.HiRes) restrictions |= MpvSeamlessRestrictions.Lossless;
        return restrictions;
    }
}
