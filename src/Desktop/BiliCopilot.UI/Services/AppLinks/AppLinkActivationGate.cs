// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.Toolkits;

namespace BiliCopilot.UI.Services.AppLinks;

/// <summary>A single, in-memory protocol mailbox. UI wakeups never retain activation payloads.</summary>
internal sealed class AppLinkActivationGate
{
    private readonly object _sync = new();
    private AppLinkResolution? _pending;
    private long _version;
    private bool _warmCaptured;
    private object? _root;
    private bool _navigationComplete;
    private bool _ready;
    private bool _closed;

    internal AppLinkCapture Capture(string? address, bool initial = false)
    {
        lock (_sync)
        {
            if (initial && _warmCaptured) return new("invalid", "dropped-superseded", null);
            if (!initial) _warmCaptured = true;
            var superseded = _pending?.Kind;
            _pending = null;
            _version++;
            var resolution = TextLinkResolver.ResolveAppLink(address);
            if (!resolution.IsValid || _closed) return new(resolution.Kind, "dropped", superseded);
            _pending = resolution;
            return new(resolution.Kind, "pending", superseded);
        }
    }

    internal bool HasPending
    {
        get { lock (_sync) return _pending is not null; }
    }

    internal bool CanDispatchPending
    {
        get { lock (_sync) return CanDispatch && _pending is not null; }
    }

    internal void UpdateReadiness(bool signedIn, bool initialLoading, bool closed, bool currentMainRoot)
    {
        lock (_sync)
        {
            _closed = closed;
            _ready = signedIn && !initialLoading && !closed && currentMainRoot;
            if (closed)
            {
                _pending = null;
                _version++;
            }
        }
    }

    internal void BeginRoot(object owner)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_root, owner)) return;
            _root = owner;
            _navigationComplete = _ready = false;
        }
    }

    internal void CompleteNavigation(object owner)
    {
        lock (_sync)
            if (ReferenceEquals(_root, owner)) _navigationComplete = true;
    }

    internal void RetireRoot(object owner)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_root, owner)) return;
            _root = null;
            _navigationComplete = _ready = false;
        }
    }

    internal AppLinkDispatch? TakePending()
    {
        lock (_sync)
        {
            if (!CanDispatch || _pending is not { } resolution) return null;
            _pending = null;
            return new(resolution, _root!, _version);
        }
    }

    internal bool TryConsume(AppLinkDispatch request)
    {
        lock (_sync)
        {
            if (request.Consumed) return false;
            request.Consumed = true;
            return IsCurrentCore(request);
        }
    }

    internal bool IsCurrent(AppLinkDispatch request)
    {
        lock (_sync) return IsCurrentCore(request);
    }

    internal bool DropPending()
    {
        lock (_sync)
        {
            var hadPending = _pending is not null;
            _pending = null;
            _version++;
            return hadPending;
        }
    }

    private bool CanDispatch => _ready && _navigationComplete && _root is not null;
    private bool IsCurrentCore(AppLinkDispatch request)
        => CanDispatch && request.Version == _version && ReferenceEquals(request.Root, _root);
}

internal sealed record AppLinkCapture(string Kind, string Result, string? SupersededKind);

internal sealed class AppLinkDispatch(AppLinkResolution resolution, object root, long version)
{
    internal AppLinkResolution Resolution { get; } = resolution;
    internal object Root { get; } = root;
    internal long Version { get; } = version;
    internal bool Consumed { get; set; }
}
