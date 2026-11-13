// Copyright (c) Bili Copilot. All rights reserved.

#if DEBUG
using Microsoft.Extensions.Logging;
using Richasy.MpvKernel.Core.Models;

namespace BiliCopilot.UI.Resolvers;

/// <summary>
/// Debug-only reproduction aid, active only when BILIFLUENT_DEBUG_FAIL_REPLAY is 1.
/// Every source request of one resolver after its first (replay, reload and the automatic retry
/// that follows a failed load) gets media that cannot load. A resolver serves one media, so other
/// videos are unaffected.
/// </summary>
internal sealed class PlaybackFailureInjection
{
    // Undecodable in-memory content: mpv really runs loadfile -> load failure -> idle, without network access.
    private const string UnplayableLocation = "memory://bilifluent-debug-replay-failure";
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("BILIFLUENT_DEBUG_FAIL_REPLAY") == "1";
    private int _requests;

    internal bool TryReplace(ref string location, MpvPlayOptions options)
    {
        if (!Enabled || ++_requests < 2) return false;
        location = UnplayableLocation;
        options.AudioTracks = null;
        // The injected source carries no cookie or request header of the real stream.
        options.HttpHeaders = new();
        options.EnableCookies = false;
        return true;
    }
}

internal sealed partial class VideoMediaSourceResolver
{
    private readonly PlaybackFailureInjection _failureInjection = new();

    partial void InjectDebugReplayFailure(ref string location, MpvPlayOptions options)
    {
        if (_failureInjection.TryReplace(ref location, options))
            logger.LogInformation("Debug replay failure injected.");
    }
}
#endif
