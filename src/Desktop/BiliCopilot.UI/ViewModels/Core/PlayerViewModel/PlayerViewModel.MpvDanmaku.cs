// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Danmaku;
using Microsoft.Extensions.Logging;
using Richasy.MpvKernel.Core;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private readonly List<MpvAssDanmakuRenderer> _mpvDanmakuRenderers = [];
    private MpvClient? _mpvDanmakuClient;
    private bool _mpvDanmakuMediaChanging;
    private long _mpvDanmakuTransitionVersion;

    internal event EventHandler? MpvDanmakuHostReady;

    internal MpvAssDanmakuRenderer? CreateMpvDanmakuRenderer()
    {
        if (IsPlaybackClosing || Client is not { } client) return null;
        ObserveMpvDanmakuClient(client);
        // A renderer selected during replay must not bind to the old file after its drain.
        // Creation reopens on the actual file-loaded event, not an old initialized UI flag.
        if (_mpvDanmakuMediaChanging) return null;
        _mpvDanmakuRenderers.RemoveAll(renderer => renderer.IsDisposed);
        var renderer = new MpvAssDanmakuRenderer(client, Player?.IsPlaybackInitialized == true && !IsConnecting,
            this.Get<ILogger<MpvAssDanmakuRenderer>>());
        _mpvDanmakuRenderers.Add(renderer);
        return renderer;
    }

    private async Task SuspendMpvDanmakuAsync()
    {
        _mpvDanmakuMediaChanging = true;
        Interlocked.Increment(ref _mpvDanmakuTransitionVersion);
        if (Client is { } client) ObserveMpvDanmakuClient(client, allowLoadedBinding: false);
        foreach (var renderer in _mpvDanmakuRenderers.ToArray()) await renderer.SuspendAsync();
    }

    private async Task DisposeMpvDanmakuAsync()
    {
        if (_mpvDanmakuClient is not null) _mpvDanmakuClient.ReachFileLoaded -= OnMpvDanmakuFileLoaded;
        _mpvDanmakuClient = null;
        foreach (var renderer in _mpvDanmakuRenderers.ToArray()) await renderer.DisposeAsync();
        _mpvDanmakuRenderers.Clear();
    }

    private void HideMpvDanmaku()
    {
        foreach (var renderer in _mpvDanmakuRenderers) renderer.SetVisible(false);
    }

    private void ObserveMpvDanmakuClient(MpvClient client, bool allowLoadedBinding = true)
    {
        if (ReferenceEquals(client, _mpvDanmakuClient)) return;
        if (_mpvDanmakuClient is not null) _mpvDanmakuClient.ReachFileLoaded -= OnMpvDanmakuFileLoaded;
        _mpvDanmakuClient = client;
        client.ReachFileLoaded += OnMpvDanmakuFileLoaded;
        // The first panel may be materialized after a newly created client's file-loaded event.
        if (allowLoadedBinding && !IsConnecting && Player?.IsPlaybackInitialized == true) _mpvDanmakuMediaChanging = false;
    }

    private void OnMpvDanmakuFileLoaded(object? sender, EventArgs args)
    {
        var version = Volatile.Read(ref _mpvDanmakuTransitionVersion);
        queue.TryEnqueue(() =>
        {
            if (IsPlaybackClosing || version != _mpvDanmakuTransitionVersion || !ReferenceEquals(sender, _mpvDanmakuClient)) return;
            _mpvDanmakuMediaChanging = false;
            MpvDanmakuHostReady?.Invoke(this, EventArgs.Empty);
        });
    }
}
