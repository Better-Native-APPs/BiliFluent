// Copyright (c) Bili Copilot. All rights reserved.

namespace Richasy.MpvKernel.Core.Models;

/// <summary>
/// A video or audio track, including whether it belongs to an external file.
/// </summary>
/// <param name="Id">The mpv track ID, not an index into the track list.</param>
/// <param name="Type">Video or audio.</param>
/// <param name="Title">The title given when the track was added.</param>
/// <param name="IsSelected">Whether mpv currently selects this track.</param>
/// <param name="IsExternal">Whether the track can be removed independently of the main file.</param>
public sealed record MpvMediaTrack(int Id, MpvTrackType Type, string? Title, bool IsSelected, bool IsExternal);
