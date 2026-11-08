// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;

namespace BiliCopilot.UI.Controls.Danmaku;

/// <summary>
/// Owns a secondary ASS track. Native calls, file IO and document generation run off the UI thread.
/// Player transitions drain this owner before loading another file or destroying the client.
/// </summary>
internal sealed class MpvAssDanmakuRenderer : IAsyncDisposable
{
    private static readonly ConditionalWeakTable<MpvClient, ClientState> Clients = new();
    private readonly object _sync = new();
    private readonly MpvClient _client;
    private readonly ClientState _native;
    private readonly ILogger<MpvAssDanmakuRenderer> _logger;
    private readonly HashSet<Task> _jobs = [];
    private readonly List<MpvAssDanmakuItem> _realtime = [];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "danmaku-ass", Guid.NewGuid().ToString("N"));
    private MpvAssDanmakuItem[] _items = [];
    private MpvAssDanmakuStyle? _style;
    // Each build disposes its own CTS in its finally block; cached async disposal drains those
    // jobs before releasing the cache lease. CA2213 cannot follow these task-owned lifetimes.
#pragma warning disable CA2213
    private CancellationTokenSource? _buildCancellation;
    private FileStream? _cacheLease;
#pragma warning restore CA2213
    private Task? _disposeTask;
    private string? _currentFile;
    private long _epoch;
    private int _counter;
    private bool _ready;
    private bool _awaitingFile;
    private bool _suspended;
    private bool _visible;
    private bool _dirty = true;
    private bool _disposed;

    internal bool IsDisposed => _disposeTask?.IsCompletedSuccessfully == true;

    internal MpvAssDanmakuRenderer(MpvClient client, bool ready, ILogger<MpvAssDanmakuRenderer> logger)
    {
        _client = client;
        _native = Clients.GetValue(client, _ => new ClientState());
        _logger = logger;
        _ready = ready;
        client.ReachFileLoading += OnFileLoading;
        client.ReachFileLoaded += OnFileLoaded;
        client.ReachFileEnd += OnFileEnd;
        lock (_sync) StartWork(InitializeCacheAsync);
    }

    internal void SetItems(MpvAssDanmakuItem[] items)
    {
        lock (_sync)
        {
            _items = items;
            ScheduleBuild(0);
        }
    }

    internal void NotifyReady(bool refreshGeometry = false)
    {
        lock (_sync)
        {
            if (_disposed || _awaitingFile || _suspended || (_ready && !refreshGeometry)) return;
            _ready = true;
            ScheduleBuild(refreshGeometry ? 500 : 0);
        }
    }

    internal void SetStyle(MpvAssDanmakuStyle style, int delay = 300)
    {
        lock (_sync)
        {
            if (_style == style) return;
            _style = style;
            ScheduleBuild(delay);
        }
    }

    internal void AddRealtime(MpvAssDanmakuItem item)
    {
        lock (_sync)
        {
            if (_realtime.Count == 100) _realtime.RemoveAt(0);
            _realtime.Add(item);
            ScheduleBuild(200);
        }
    }

    internal void Invalidate()
    {
        lock (_sync)
        {
            _dirty = true;
            _buildCancellation?.Cancel();
        }
    }

    internal void SetVisible(bool visible)
    {
        lock (_sync)
        {
            if (_disposed || _visible == visible) return;
            _visible = visible;
            if (!visible) _buildCancellation?.Cancel();
            StartWork(UpdateVisibilityAsync);
            if (visible && _dirty) ScheduleBuild(0);
        }
    }

    internal void Clear()
    {
        lock (_sync)
        {
            _items = [];
            _realtime.Clear();
            _epoch++;
            _dirty = true;
            _buildCancellation?.Cancel();
            var file = _currentFile;
            if (file is not null) StartWork(() => RemoveFileAsync(file));
        }
    }

    internal Task SuspendAsync()
    {
        lock (_sync)
        {
            _ready = false;
            _awaitingFile = true;
            _suspended = true;
            _epoch++;
            _dirty = true;
            var cancelBuild = _buildCancellation?.CancelAsync() ?? Task.CompletedTask;
            return Task.Run(async () =>
            {
#pragma warning disable VSTHRD003 // This owner initiated cancellation; its callbacks never depend on this lock-free drain worker.
                await cancelBuild.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                await DrainAsync().ConfigureAwait(false);
            });
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposed = true;
            _ready = false;
            _awaitingFile = true;
            _visible = false;
            var cancelBuild = _buildCancellation?.CancelAsync() ?? Task.CompletedTask;
            _client.ReachFileLoading -= OnFileLoading;
            _client.ReachFileLoaded -= OnFileLoaded;
            _client.ReachFileEnd -= OnFileEnd;
            _disposeTask = Task.Run(async () =>
            {
#pragma warning disable VSTHRD003 // This owner initiated cancellation; its callbacks never depend on this lock-free disposal worker.
                await cancelBuild.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                await DrainAsync().ConfigureAwait(false);
                _cacheLease?.Dispose();
                try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
                catch (IOException ex) { Warn(ex, "Could not delete the danmaku ASS cache."); }
                catch (UnauthorizedAccessException ex) { Warn(ex, "Could not delete the danmaku ASS cache."); }
            });
            return new ValueTask(_disposeTask);
        }
    }

    private void OnFileLoading(object? sender, EventArgs args)
    {
        lock (_sync)
        {
            // The player's drain barrier precedes issuing loadfile. Only its new start-file
            // event reopens admission; a late old file-loaded event cannot reopen it.
            _suspended = false;
            OnFileEnd(sender, args);
        }
    }

    private void OnFileEnd(object? sender, EventArgs args)
    {
        lock (_sync)
        {
            _ready = false;
            _awaitingFile = true;
            _epoch++;
            _dirty = true;
            _buildCancellation?.Cancel();
        }
    }

    private void OnFileLoaded(object? sender, EventArgs args)
    {
        lock (_sync)
        {
            if (_disposed || _suspended) return;
            _ready = true;
            _awaitingFile = false;
            StartWork(UpdateVisibilityAsync);
            _epoch++;
            ScheduleBuild(0);
        }
    }

    // Caller owns _sync. Each delayed build owns/disposes its CTS, including cancellation paths.
    private void ScheduleBuild(int delay)
    {
        _dirty = true;
        _buildCancellation?.Cancel();
        if (_disposed || !_visible || !_ready || _style is null || _style.PanelWidth <= 0 || _style.PanelHeight <= 0) return;
        var cancellation = new CancellationTokenSource();
        _buildCancellation = cancellation;
        var token = cancellation.Token;
        var epoch = _epoch;
        var style = _style;
        var items = _items;
        var realtime = _realtime.ToArray();
        StartWork(async () =>
        {
            string? file = null;
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                await _native.Gate.WaitAsync(token).ConfigureAwait(false);
                double aspect;
                try
                {
                    if (!CanBuild(epoch, token)) return;
                    aspect = ReadNumber("video-params/aspect");
                    if (!double.IsFinite(aspect) || aspect <= 0)
                        aspect = ReadNumber("video-params/dw") / ReadNumber("video-params/dh");
                    EnsureCache();
                }
                finally { _native.Gate.Release(); }

                var document = MpvAssDanmakuDocument.Build(items.Concat(realtime), style, aspect, token, out var advanced, out var count);
                if (advanced > 0 && _logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Skipped {Count} advanced danmaku in the mpv renderer.", advanced);
                token.ThrowIfCancellationRequested();
                if (count == 0)
                {
                    await CommitEmptyAsync(epoch, token).ConfigureAwait(false);
                    return;
                }

                file = Path.Combine(_directory, Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture) + ".ass");
                await File.WriteAllTextAsync(file, document, new UTF8Encoding(true), token).ConfigureAwait(false);
                await _native.Gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (!CanBuild(epoch, token)) return;
                    InitializeClient();
                    Command("sub-add", file, "auto", "Bili Danmaku", "");
                    var id = FindTrack(file) ?? throw new InvalidOperationException("The added danmaku subtitle track was not found.");
                    string? previous;
                    lock (_sync)
                    {
                        // Serialize the small selection commit with Clear/hide/dispose. A clear
                        // must either cancel this commit or capture its newly selected file.
                        if (!CanBuild(epoch, token)) return;
                        SetProperty("secondary-sub-visibility", "yes");
                        SetProperty("secondary-sid", id);
                        previous = _currentFile;
                        _currentFile = file;
                        file = null;
                        _dirty = false;
                    }

                    if (previous is not null) RemoveTrack(previous);
                }
                finally { _native.Gate.Release(); }
            }
            finally
            {
                if (file is not null)
                {
                    // sub-add can succeed before a later property write fails. Remove by path,
                    // never by an id retained across a media generation.
                    await RemoveFileAsync(file).ConfigureAwait(false);
                }

                lock (_sync)
                {
                    if (ReferenceEquals(_buildCancellation, cancellation)) _buildCancellation = null;
                    cancellation.Dispose();
                }
            }
        });
    }

    private bool CanBuild(long epoch, CancellationToken token)
    {
        lock (_sync) return _ready && _visible && epoch == _epoch && !token.IsCancellationRequested && !_client.IsDisposed;
    }

    private void StartWork(Func<Task> action)
    {
        if (_disposed || _suspended) return;
        var task = Task.Run(async () =>
        {
            try { await action().ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Warn(ex, "The mpv danmaku operation failed; the previous track is retained when possible."); }
        });
        _jobs.Add(task);
        _ = ObserveAsync(task);
    }

    private async Task ObserveAsync(Task task)
    {
#pragma warning disable VSTHRD003 // Join the renderer-owned Task.Run worker; it never awaits this observer or the UI dispatcher, and no gate is held across the wait.
        await task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        lock (_sync) _jobs.Remove(task);
    }

    private async Task DrainAsync()
    {
        Task[] jobs;
        lock (_sync) jobs = _jobs.ToArray();
        await Task.WhenAll(jobs).ConfigureAwait(false);
        string? file;
        lock (_sync) file = _currentFile;
        if (file is not null) await RemoveFileAsync(file).ConfigureAwait(false);
    }

    private async Task UpdateVisibilityAsync()
    {
        await _native.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool visible;
            lock (_sync) visible = _visible;
            if (_client.IsDisposed) return;
            InitializeClient();
            SetProperty("secondary-sub-visibility", visible ? "yes" : "no");
        }
        finally { _native.Gate.Release(); }
    }

    private async Task CommitEmptyAsync(long epoch, CancellationToken token)
    {
        await _native.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (!CanBuild(epoch, token)) return;
                if (_currentFile is { } file) RemoveTrack(file);
                _currentFile = null;
                _dirty = false;
            }
        }
        finally { _native.Gate.Release(); }
    }

    private async Task RemoveFileAsync(string file)
    {
        await _native.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            try { if (!_client.IsDisposed) RemoveTrack(file); }
            catch (Exception ex) { Warn(ex, "Could not remove a danmaku ASS track."); }
            lock (_sync)
            {
                if (_currentFile == file) _currentFile = null;
            }
            DeleteFile(file);
        }
        finally { _native.Gate.Release(); }
    }

    private void InitializeClient()
    {
        if (_native.Initialized) return;
        SetProperty("secondary-sub-ass-override", "no");
        SetProperty("secondary-sub-visibility", "yes");
        _native.Initialized = true;
    }

    private void RemoveTrack(string file)
    {
        var id = FindTrack(file);
        if (id is not null)
        {
            // Confirm ownership immediately before touching secondary selection or removing a track.
            if (MpvNative.GetPropertyString(_client.Handle, "secondary-sid") == id)
            {
                SetProperty("secondary-sub-visibility", "no");
                SetProperty("secondary-sid", "no");
            }

            Command("sub-remove", id);
        }

        DeleteFile(file);
    }

    private string? FindTrack(string file)
    {
        var count = ReadNumber("track-list/count");
        if (!double.IsFinite(count) || count < 0)
            throw new InvalidOperationException("The mpv track list is unavailable.");
        for (var i = 0; i < count; i++)
        {
            var prefix = "track-list/" + i.ToString(CultureInfo.InvariantCulture) + "/";
            if (MpvNative.GetPropertyString(_client.Handle, prefix + "type") == "sub"
                && string.Equals(MpvNative.GetPropertyString(_client.Handle, prefix + "external-filename"), file, StringComparison.OrdinalIgnoreCase))
                return MpvNative.GetPropertyString(_client.Handle, prefix + "id");
        }

        return null;
    }

    private double ReadNumber(string property)
        => double.TryParse(MpvNative.GetPropertyString(_client.Handle, property), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;

    private void SetProperty(string name, string value)
        => RequireSuccess(MpvNative.SetPropertyString(_client.Handle, name, value), name);

    private void Command(params string[] arguments)
        => RequireSuccess(MpvNative.SetCommand(_client.Handle, arguments), arguments[0]);

    private static void RequireSuccess(MpvError error, string operation)
    {
        if (error != MpvError.Success) throw new InvalidOperationException("mpv danmaku " + operation + " failed: " + error);
    }

    private void EnsureCache()
    {
        if (_cacheLease is not null) return;
        Directory.CreateDirectory(_directory);
        _cacheLease = new FileStream(Path.Combine(_directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // An exclusive lease distinguishes abandoned caches from other active players/processes.
        foreach (var directory in Directory.EnumerateDirectories(Path.GetDirectoryName(_directory)!))
        {
            if (directory == _directory) continue;
            try
            {
                var leasePath = Path.Combine(directory, ".lock");
                if (!File.Exists(leasePath)) continue;
                using (new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    foreach (var file in Directory.EnumerateFiles(directory, "*.ass")) File.Delete(file);
                }

                Directory.Delete(directory, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task InitializeCacheAsync()
    {
        await _native.Gate.WaitAsync().ConfigureAwait(false);
        try { EnsureCache(); }
        finally { _native.Gate.Release(); }
    }

    private void DeleteFile(string file)
    {
        try { File.Delete(file); }
        catch (IOException ex) { Warn(ex, "Could not delete a danmaku ASS file."); }
        catch (UnauthorizedAccessException ex) { Warn(ex, "Could not delete a danmaku ASS file."); }
    }

    private void Warn(Exception exception, string message)
    {
        if (_logger.IsEnabled(LogLevel.Warning)) _logger.LogWarning(exception, "{Message}", message);
    }

    private sealed class ClientState
    {
        // Shared across renderer switches; no wait handle is allocated or exposed.
        internal SemaphoreSlim Gate { get; } = new(1, 1);

        internal bool Initialized { get; set; }
    }
}
