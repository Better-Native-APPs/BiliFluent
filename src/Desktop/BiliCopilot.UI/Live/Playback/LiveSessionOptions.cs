// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Live.Playback;

/// <summary>Immutable live engine preferences; retired player settings are never read.</summary>
public sealed record LiveSessionOptions(LiveDecodePreference DecodePreference);
