// Copyright (c) Bili Copilot. All rights reserved.

namespace Richasy.MpvKernel.Relay;

public sealed partial class LoopbackMediaRelay
{
    /// <summary>
    /// Registers a full original file whose known ranges are served from memory first. Range,
    /// seek, node switching and URL replacement use original-file coordinates. Memory is held
    /// until unregistered. Invalid, overlapping or out-of-file ranges are rejected.
    /// </summary>
    public string RegisterPreloaded(string key, RelayPreloadedStreamRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(registration.Preloaded);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(registration.Length);
        var sorted = registration.Preloaded.OrderBy(part => part.Offset).ToArray();
        long end = 0;
        foreach (var part in sorted)
        {
            if (part.Data.IsEmpty || part.Offset < end || part.Offset > registration.Length || part.Data.Length > registration.Length - part.Offset)
            {
                throw new ArgumentException("Preloaded ranges overlap, are empty or exceed the file.", nameof(registration));
            }

            end = part.Offset + part.Data.Length;
        }

        return Register(key, new RelayStreamRegistration
        {
            Upstreams = registration.Upstreams,
            Current = registration.Current,
            Headers = registration.Headers,
            Length = registration.Length,
            Preloaded = sorted,
        });
    }

    /// <summary>
    /// Registers a seekable virtual MP4. Requests crossing head, memory and upstream boundaries
    /// remain one HTTP response. Replacement nodes are checked against the full original length.
    /// </summary>
    public string RegisterVirtualMp4(string key, RelayVirtualMp4Registration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var map = new RelayVirtualOffsetMap(registration.Head.Length, registration.Fragments.Length, registration.FirstFragmentOffset, registration.UpstreamLength);
        return Register(key, new RelayStreamRegistration
        {
            Upstreams = registration.Upstreams,
            Current = registration.Current,
            Headers = registration.Headers,
            Length = map.Length,
            Preloaded = [new(0, registration.Head), new(registration.Head.Length, registration.Fragments)],
            VirtualMap = map,
        });
    }
}
