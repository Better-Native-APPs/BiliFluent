// Copyright (c) Bili Copilot. All rights reserved.

namespace Richasy.MpvKernel.Relay;

/// <summary>A complete original file with some nonoverlapping byte ranges already in memory.</summary>
public sealed class RelayPreloadedStreamRegistration
{
    /// <summary>The original file's nodes, in failover order.</summary>
    public required IReadOnlyList<RelayUpstream> Upstreams { get; init; }

    /// <summary>The initial node's index.</summary>
    public int Current { get; init; }

    /// <summary>Headers used only for upstream requests.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>The full original-file length.</summary>
    public required long Length { get; init; }

    /// <summary>Ranges in original-file coordinates. Do not modify their bytes until unregistered.</summary>
    public required IReadOnlyList<RelayPreload> Preloaded { get; init; }
}

/// <summary>A rewritten MP4 head and contiguous original fragments, followed by the upstream tail.</summary>
public sealed class RelayVirtualMp4Registration
{
    /// <summary>The original file's nodes, in failover order.</summary>
    public required IReadOnlyList<RelayUpstream> Upstreams { get; init; }

    /// <summary>The initial node's index.</summary>
    public int Current { get; init; }

    /// <summary>Headers used only for upstream requests.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Initialization + rewritten sidx. Do not modify until unregistered.</summary>
    public required ReadOnlyMemory<byte> Head { get; init; }

    /// <summary>One or more contiguous original fragments. Do not modify until unregistered.</summary>
    public required ReadOnlyMemory<byte> Fragments { get; init; }

    /// <summary>The first preloaded fragment's original-file offset.</summary>
    public required long FirstFragmentOffset { get; init; }

    /// <summary>The full original-file length, not the shorter virtual length.</summary>
    public required long UpstreamLength { get; init; }
}
