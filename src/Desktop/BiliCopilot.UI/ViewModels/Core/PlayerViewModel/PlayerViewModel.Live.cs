// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Hosting;
using BiliCopilot.UI.Live;
using BiliCopilot.UI.Live.Application;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Resolvers;
using BiliCopilot.UI.SponsorBlock.Rules;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Models.Media;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Core.Enums;
using Richasy.MpvKernel.Core.Models;
using Richasy.MpvKernel.Player;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    private readonly LiveChatFactory _liveChatFactory = new();
    private readonly LiveRefreshGate _liveRefresh = new();
    private readonly LiveReleaseBarrier _liveCloseRelease = new();
    // DisposeAsync -> CloseAsync -> DisposeLiveCoreAsync waits the real room/chat close barrier;
    // failed release deliberately retains these owners rather than retrying a disposal no-op.
#pragma warning disable CA2213
    private ILiveRoomSession? _liveRoom;
    private ILiveChatClient? _unadoptedLiveChat;
#pragma warning restore CA2213
    private LiveRoomScope? _liveScope;
    private LiveAccountStamp? _liveAccount;
    private CancellationTokenRegistration _liveAccountRegistration;
    private bool _liveAccountActive;
    private volatile bool _liveAccountEnded;
    private bool _liveReleaseBlocked;
    private bool _livePreferHdr;
    private LiveInformation? _liveInformation;
    private long? _liveCanonicalRoom;
    private Task? _liveInitialization;
    private Task? _liveRefreshWork;
    private bool _liveMediaPending;
    // UnwatchLiveAttempt disposes the registration after invalidation, before native release.
#pragma warning disable CA2213
    private LivePlaybackAttempt? _liveAttempt;
#pragma warning restore CA2213
    private Action? _detachLiveAttempt;
    private LiveReleaseBarrier? _liveClientRelease;
    private LiveReleaseBarrier? _liveRendererRelease;
    private long _liveCloseStartedAt;
    private LiveCloseDiagnostics.Failure? _liveCloseFailure;
    private bool _liveCloseRetrying;
    private readonly List<(string Name, Action Retry, Exception Error)> _liveCloseActionFailures = [];

    public bool IsLiveMedia => _snapshot?.Type == BiliMediaType.Live;
    public LiveConnectorViewModel? LiveConnector => Connector as LiveConnectorViewModel;
    public ILiveRoomSession? LiveRoom => _liveRoom;
    public IAsyncRelayCommand? SendLiveDanmakuCommand => LiveConnector?.SendDanmakuCommand;
    public bool CanSendLiveDanmaku => LiveConnector?.CanSend == true;
    public ObservableCollection<SourceItemViewModel> LiveSources { get; } = [];
    public IReadOnlyList<PlayerFormatInformation> LivePlayableQualities
        => (_sourceResolver as LiveMediaSourceResolver)?.PlayableQualities ?? [];

    [ObservableProperty]
    public partial LiveMediaLoadState LiveLoadState { get; private set; }

    [ObservableProperty]
    public partial SourceItemViewModel? SelectedLiveSource { get; private set; }

    [ObservableProperty]
    public partial SourceItemViewModel? RequestedLiveSource { get; private set; }

    public bool IsLiveStarting => IsLiveMedia && LiveLoadState is LiveMediaLoadState.Resolving
        or LiveMediaLoadState.CommandSubmitted or LiveMediaLoadState.FileLoaded;
    public bool HasLiveFrame => IsLiveMedia && _liveAttempt is { HasFrame: true } attempt && attempt.State != LiveMediaLoadState.Closed;
    public bool IsLiveReady => HasLiveFrame && LiveLoadState is LiveMediaLoadState.Ready or LiveMediaLoadState.Paused;
    public bool IsLiveFailed => IsLiveMedia && LiveLoadState == LiveMediaLoadState.Failed;
    public bool IsLiveReleaseBlocked => _liveReleaseBlocked || _liveCloseRelease.IsBlocked || _liveClientRelease?.IsBlocked == true;
    public bool LiveReleaseConfirmed => _liveCloseRelease.ReleaseConfirmed && !IsLiveReleaseBlocked && Window?.IsDisposed == true;
    public bool CanSelectLiveQuality => IsLiveMedia && !IsPlaybackClosing && !IsConnecting && !IsLiveReleaseBlocked
        && !_liveMediaPending
        && _liveScope?.Token.IsCancellationRequested == false && LiveSources.Count > 1;

    private bool HasOwnedLiveResources => _liveAccountActive || _liveRoom is not null || _unadoptedLiveChat is not null
        || _liveInformation is not null || _liveChatFactory.HasWork || IsLiveReleaseBlocked;

    /// <summary>
    /// Starts live media for an account captured by the route before its admission lease.
    /// Attach the owned host first; this does not acquire an application route lease or switch the official entry.
    /// </summary>
    public Task InitializeLiveAsync(MediaSnapshot snapshot, LiveAccountStamp account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Type != BiliMediaType.Live || snapshot.Live is null)
            throw new ArgumentException("A live room snapshot is required.", nameof(snapshot));
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(IsPlaybackClosing, this);
        CheckLiveRelease();
        BindLiveAccount(account);
        LogLiveAction("open");
        return InitializeAsync(snapshot, cancellationToken);
    }

    /// <summary>Explicit Play is a no-op while playing; after pause/end/failure it resolves the live edge.</summary>
    public Task PlayLiveAsync()
    {
        if (!IsLiveMedia || IsPlaybackClosing) return Task.CompletedTask;
        if (LiveLoadState == LiveMediaLoadState.Ready) return Task.CompletedTask;
        LogLiveAction("resume");
        return RefreshLiveAsync();
    }

    /// <summary>Pauses the current live output, without seeking or changing the room connection.</summary>
    public Task PauseLiveAsync()
    {
        if (!IsLiveReady || IsPlaybackClosing || IsConnecting || LiveLoadState == LiveMediaLoadState.Paused)
            return Task.CompletedTask;
        var client = Client;
        return RunWithoutPlayerInputAsync(async () =>
        {
            if (!IsLiveReady || !ReferenceEquals(client, Client)) return;
            var result = await client!.PauseAsync();
            CheckMediaAdmission();
            if (result.IsFailed) throw new InvalidOperationException("Live pause failed.");
            LiveLoadState = LiveMediaLoadState.Paused;
            LogLiveAction("pause");
        });
    }

    /// <summary>Coalesces an in-flight refresh and reuses the player's real media generation/gate.</summary>
    public Task RefreshLiveAsync()
    {
        if (!IsLiveMedia) return Task.CompletedTask;
        CheckLiveRelease();
        if (IsPlaybackClosing) return Task.CompletedTask;
        if (_liveRefreshWork is { IsCompleted: false }) return _liveRefreshWork;
        if (_liveInitialization is { IsCompleted: false })
#pragma warning disable VSTHRD003 // Return the player's retained actual initialization without a media gate or synchronous wait.
            return _liveInitialization;
#pragma warning restore VSTHRD003
        LogLiveAction("refresh");
        _liveMediaPending = true;
        _liveRefreshWork = OwnWork(_liveRefresh.RunAsync(() => InitializeAsync(_snapshot)));
        NotifyLivePropertiesChanged();
        return _liveRefreshWork;
    }

    [RelayCommand(CanExecute = nameof(CanSelectLiveQuality))]
    public Task SelectLiveSourceAsync(SourceItemViewModel? source)
    {
        if (!CanSelectLiveQuality || source is not { IsEnabled: true, IsAuto: false } || !LiveSources.Contains(source))
            return Task.CompletedTask;
        if (Equals(source, SelectedLiveSource)) return Task.CompletedTask;
        LogLiveAction("quality-select");
        RequestedLiveSource = source;
        _snapshot.PreferQuality = source.Data.Quality;
        _snapshot.PreferEotf = source.Eotf;
        // An explicit joint tier survives failure. No on-demand setting is read or written.
        return RefreshLiveAsync();
    }

    private LiveAccountStamp CaptureLiveAccount()
    {
        var account = this.Get<AccountViewModel>();
        return new(account.AccountId, account.AccountGeneration, account.AccountCancellationToken);
    }

    private void BindLiveAccount(LiveAccountStamp captured)
    {
        if (!queue.HasThreadAccess) throw new InvalidOperationException("Live media requires its UI dispatcher.");
        captured.Lifetime.ThrowIfCancellationRequested();
        if (captured != CaptureLiveAccount() || (_liveAccountActive && _liveAccount != captured))
            throw new OperationCanceledException("Live account scope changed.");
        if (_liveAccountActive) return;
        _liveAccount = captured;
        _liveAccountActive = true;
        _liveAccountEnded = false;
        _livePreferHdr = IsLiveHdrDisplay();
        _liveAccountRegistration = captured.Lifetime.Register(() =>
        {
            _liveAccountEnded = true;
            // Invalidation stops admission immediately; the UI owner performs the actual total close.
            queue.TryEnqueue(() => { if (_liveAccountActive && _liveAccount == captured) Close(); });
        });
    }

    private bool IsLiveHdrDisplay()
    {
        try
        {
            using var display = Microsoft.Graphics.Display.DisplayInformation.CreateForWindowId(Window!.GetWindow().Id);
            return display.GetAdvancedColorInfo().CurrentAdvancedColorKind
                == Microsoft.Graphics.Display.DisplayAdvancedColorKind.HighDynamicRange;
        }
        catch (Exception) { return false; }
    }

    private void EnsureLiveRoom()
    {
        CheckLiveRelease();
        if (_liveRoom is not null) return;
        var chat = _liveChatFactory.Create(this.Get<IBiliCookiesResolver>(), this.Get<IBiliTokenResolver>(), CaptureLiveAccount, queue);
        _unadoptedLiveChat = chat;
        _liveRoom = new LiveRoomSession(chat, CaptureLiveAccount, () => queue.HasThreadAccess,
            action => queue.TryEnqueue(() => action()), category => logger.LogWarning("Live room operation failed ({Category}).", category));
        _unadoptedLiveChat = null;
    }

    private async Task InitializeLiveMediaAsync(IMpvMediaHistoryResolver? previousHistory)
    {
        var startedAt = Stopwatch.GetTimestamp();
        EnsureLiveRoom();
        var room = _liveRoom!;
        var previous = _liveScope;
        var expectedRoom = ReferenceEquals(_liveInformation, _snapshot.Live) ? _liveCanonicalRoom : null;
        _liveInformation = _snapshot.Live;
        var scope = room.PrepareRoom(_liveInformation!, _liveAccount!.Value, _activeMediaToken);
        _liveScope = scope;
        _liveAttempt?.Invalidate();
        LiveLoadState = LiveMediaLoadState.Resolving;
        SelectedLiveSource = null;
        // InitializeCoreAsync is already registered as real owned work before this publication.
        room.PublishRoomReset(scope);
        try
        {
            if (previous is not null)
            {
                try { await room.StopAsync(previous); }
                catch (Exception) { _liveReleaseBlocked = true; throw new InvalidOperationException("Live room stop is unconfirmed; replacement is blocked."); }
            }
            CheckMediaAdmission();
            if (previousHistory is not null && Player is { } previousPlayer)
                await previousHistory.SaveHistoryAsync(previousPlayer.Position, previousPlayer.Duration, MpvPlayerState.End, true);
            _historyResolver = null;
            await RetireLivePlayerAsync();
            CheckMediaAdmission();

            var resolver = this.Get<LiveMediaSourceResolver>();
            _sourceResolver = resolver;
            var tokens = this.Get<IBiliTokenResolver>();
            var account = this.Get<AccountViewModel>();
            await resolver.ResolveAsync(_snapshot, Window!.RenderHwnd, CurrentVolume, tokens.GetToken() is not null,
                account.MyProfile?.IsVip == true, _livePreferHdr, scope.Token);
            CheckMediaAdmission();
            if (expectedRoom.HasValue && expectedRoom.Value != resolver.RoomId)
                throw new InvalidOperationException("Live canonical room changed during refresh.");
            _liveCanonicalRoom = resolver.RoomId;
            await InitializeInternalAsync();
            CheckMediaAdmission();
            await LiveConnector!.InitializeAsync(_snapshot, resolver, scope.Token);
            room.BindRoom(scope, resolver.RoomId, resolver.Room!);
            PublishLiveSources(resolver);
            Title = room.Title;

            for (var index = 0; index < resolver.CandidateCount; index++)
            {
                CheckMediaAdmission();
                if (index > 0)
                {
                    await RetireLivePlayerAsync();
                    CheckMediaAdmission();
                    await InitializeInternalAsync();
                    CheckMediaAdmission();
                }
                resolver.SelectCandidate(index);
                Player = new MpvPlayer(Client!, resolver, historyResolver: null, logger: NullLogger.Instance);
                Player.PropertyChanged += OnPlayerPropertyChanged;
                WatchAutomaticAudio();
                var attempt = WatchLiveAttempt(Client!, scope, _mediaVersion);
                // Cache/reset through the compatible initializer, but never interpret its swallowed
                // errors as load success. Replay returns the existing explicit command-submission result;
                // both source reads are in-memory, not another live request. Frame readiness is separate.
                await Player.InitializeAsync(alsoPlay: false);
                CheckMediaAdmission();
                var submitted = attempt.State != LiveMediaLoadState.Failed && await Player.ReplayAsync(resume: true, speed: 1);
                if (submitted) attempt.MarkSubmitted();
                else attempt.Fail(endedBeforeFrame: false);
                PublishLiveAttempt(attempt);
                var ready = await attempt.Ready;
                CheckMediaAdmission();
                if (ready && attempt.State == LiveMediaLoadState.Ready)
                {
                    SafeFailureContext.TryWriteFacts(() => logger, log =>
                    {
                        if (log.IsEnabled(LogLevel.Information))
                            log.LogInformation("Live | first-frame ms={Milliseconds}", (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
                    });
                    MarkLiveSource(resolver);
                    LiveLoadState = LiveMediaLoadState.Ready;
                    _ = OwnWork(room.ConnectAsync(scope));
                    await ResetPlayerPresenterModeAsync();
                    CheckMediaAdmission();
                    CheckFullScreen();
                    CheckCompactOverlay();
                    return;
                }
                if (!attempt.CanRetry || index + 1 >= resolver.CandidateCount) break;
                // The next candidate is admitted only after full disposal of this fresh client.
            }
            throw new InvalidOperationException("Live media did not produce a frame.");
        }
        catch (Exception error)
        {
            _liveAttempt?.Invalidate();
            var cancelled = scope.Token.IsCancellationRequested;
            var cleanupFailed = false;
            try { await room.StopAsync(scope); } catch (Exception) { cleanupFailed = true; }
            try { await RetireLivePlayerAsync(); } catch (Exception) { cleanupFailed = true; }
            if (cleanupFailed)
            {
                _liveReleaseBlocked = true;
                throw new InvalidOperationException("Live cleanup is unconfirmed; replacement is blocked.");
            }
            if (cancelled || error is OperationCanceledException)
            {
                if (!_isClosing) LiveLoadState = LiveMediaLoadState.Idle;
                throw new OperationCanceledException("Live media opening was cancelled.", scope.Token);
            }
            LiveLoadState = LiveMediaLoadState.Failed;
            throw new InvalidOperationException("Live playback is unavailable. Refresh to resolve a new stream.");
        }
    }

    private LivePlaybackAttempt WatchLiveAttempt(MpvClient client, LiveRoomScope scope, long version)
    {
        UnwatchLiveAttempt();
        var attempt = new LivePlaybackAttempt(scope.Token);
        _liveAttempt = attempt;
        bool Current() => ReferenceEquals(Client, client) && ReferenceEquals(_liveAttempt, attempt)
            && ReferenceEquals(_liveScope, scope) && IsCurrentPlayback(version) && !scope.Token.IsCancellationRequested;
        void Publish()
        {
            queue.TryEnqueue(() => { if (Current()) PublishLiveAttempt(attempt); });
        }
        void Loading(object? sender, EventArgs args) { if (Current()) attempt.MarkStarted(); }
        void Loaded(object? sender, EventArgs args) { if (Current()) { attempt.MarkLoaded(); Publish(); } }
        void Failed(object? sender, MpvError error) { if (Current()) { attempt.Fail(endedBeforeFrame: true); Publish(); } }
        void Ended(object? sender, EventArgs args) { if (Current()) { attempt.End(); Publish(); } }
        void Error(object? sender, MpvError error)
        {
            if (!Current()) return;
            // Before a frame, native end-file decides candidate retry. These two errors also report
            // InitializeAsync's synchronous source/loadfile failure, which must not masquerade as submission.
            if (attempt.HasFrame || error is MpvError.VoInitFailed or MpvError.OptionError)
            {
                attempt.Fail(endedBeforeFrame: false);
                Publish();
            }
        }
        void Notify(object? sender, MpvClientNotifyEventArgs args)
        {
            if (!Current() || args.Id != MpvClientEventId.PlaybackRestart || !attempt.FileLoaded) return;
            // mpv has started output, not merely parsed the file. Audio-only restart is not a video frame.
            if (client.GetProperty("vo-configured", out bool configured) == MpvError.Success && configured
                && client.GetProperty("video-out-params/w", out long width) == MpvError.Success && width > 0
                && client.GetProperty("video-out-params/h", out long height) == MpvError.Success && height > 0)
            {
                attempt.MarkFrame();
                Publish();
            }
        }
        client.ReachFileLoading += Loading;
        client.ReachFileLoaded += Loaded;
        client.FileFailed += Failed;
        client.ReachFileEnd += Ended;
        client.ErrorOccurred += Error;
        client.DataNotify += Notify;
        _detachLiveAttempt = () =>
        {
            client.ReachFileLoading -= Loading;
            client.ReachFileLoaded -= Loaded;
            client.FileFailed -= Failed;
            client.ReachFileEnd -= Ended;
            client.ErrorOccurred -= Error;
            client.DataNotify -= Notify;
        };
        return attempt;
    }

    private void UnwatchLiveAttempt()
    {
        _liveAttempt?.Invalidate();
        _detachLiveAttempt?.Invoke();
        _liveAttempt?.Dispose();
        _detachLiveAttempt = null;
    }

    private void PublishLiveAttempt(LivePlaybackAttempt attempt)
    {
        if (!ReferenceEquals(attempt, _liveAttempt) || IsPlaybackClosing) return;
        LiveLoadState = attempt.State;
        if (attempt.State == LiveMediaLoadState.Failed)
            ErrorMessage = "Live playback failed. Refresh to resolve a new stream.";
        CheckBlackBackgroundVisible();
    }

    private void PublishLiveSources(LiveMediaSourceResolver resolver)
    {
        LiveSources.Clear();
        foreach (var format in resolver.Qualities)
            LiveSources.Add(SourceItemViewModel.CreateLive(format, source => SelectLiveSourceAsync(source)));
        RequestedLiveSource = LiveSources.FirstOrDefault(source => source.Data.Quality == _snapshot.PreferQuality
            && (!_snapshot.PreferEotf.HasValue || source.Eotf == _snapshot.PreferEotf));
        NotifyLivePropertiesChanged();
    }

    private void MarkLiveSource(LiveMediaSourceResolver resolver)
    {
        foreach (var source in LiveSources)
            source.IsSelected = source.Data.Quality == resolver.SelectedQuality?.Quality && source.Eotf == resolver.SelectedQuality?.Eotf;
        SelectedLiveSource = LiveSources.FirstOrDefault(source => source.IsSelected);
        RequestedLiveSource = SelectedLiveSource;
        _snapshot.PreferQuality = resolver.SelectedQuality?.Quality;
        _snapshot.PreferEotf = resolver.SelectedQuality?.Eotf;
        ErrorMessage = null;
    }

    private void CheckLiveRelease()
    {
        if (IsLiveReleaseBlocked) throw new InvalidOperationException("Live release is unconfirmed; replacement is blocked.");
    }

    private async Task ObserveLiveTransportAsync(Task operation)
    {
#pragma warning disable VSTHRD003 // Play/Pause already own this real work; this notification observer owns no media gate or close prerequisite.
        try { await operation; }
#pragma warning restore VSTHRD003
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportPlaybackFailure(ex); }
    }

    private async Task SendInlineLiveDanmakuAsync(string text)
    {
        var connector = LiveConnector;
        var binding = _liveRoom?.Binding;
        if (connector is null || !IsLiveMedia || IsPlaybackClosing || IsConnecting || _liveMediaPending || !binding.HasValue)
            throw new InvalidOperationException("The live room is not ready to send a message.");
        var generation = _mediaVersion;
        var result = await connector.SendManuallyAsync(binding.Value, text, _liveScope!.Token);
        if (!IsCurrentPlayback(generation) || !ReferenceEquals(connector, LiveConnector) || _liveRoom?.Binding != binding)
            throw new OperationCanceledException("Live room changed; the message draft was retained.");
        if (result.Outcome != LiveSendOutcome.Accepted || result.Binding != binding.Value)
            throw new InvalidOperationException("Live message was not confirmed. It was not resent.");
        // Only the room's server messages are displayed; there is no optimistic AddDanmaku here.
    }

    private Task ReleaseLiveRenderersAsync()
    {
        _liveRendererRelease ??= new LiveReleaseBarrier();
        if (_mpvDanmakuClient is not null) _mpvDanmakuClient.ReachFileLoaded -= OnMpvDanmakuFileLoaded;
        return ReleaseAsync();
        async Task ReleaseAsync()
        {
            await (_liveCloseRetrying && _liveRendererRelease.HasStarted ? _liveRendererRelease.RetryAsync()
                : _liveRendererRelease.ReleaseAsync(_mpvDanmakuRenderers.ToArray()
                    .Select(renderer => (Func<Task>)(() => LiveCloseStepAsync("danmaku-renderer-dispose", () => renderer.DisposeAsync().AsTask()))).ToArray()));
            _mpvDanmakuRenderers.Clear();
            _mpvDanmakuClient = null;
        }
    }

    private async Task RetireLivePlayerAsync(bool releaseRenderers = true, bool clearOwners = true)
    {
        UnwatchLiveAttempt();
        _liveClientRelease ??= new LiveReleaseBarrier();
        var player = Player;
        var client = Client;
        var native = _nativeMp;
        var smtc = _smtc;
        await (_liveCloseRetrying && _liveClientRelease.HasStarted ? _liveClientRelease.RetryAsync() : _liveClientRelease.ReleaseAsync(
            () => LiveCloseStepAsync("playback-diagnostics-stop", SuspendPlaybackDiagnosticsAsync),
            () => LiveCloseStepAsync("network-monitor-stop", SuspendNetworkMonitorAsync),
            () => LiveCloseStepAsync("player-renderers", () => releaseRenderers ? ReleaseLiveRenderersAsync() : Task.CompletedTask),
            () => LiveCloseStepAsync("player-events-detach", () =>
            {
                if (player is not null)
                {
                    player.PropertyChanged -= OnPlayerPropertyChanged;
                    player.RelayUpstreamFailed -= OnRelayUpstreamFailed;
                }
                return Task.CompletedTask;
            }),
            () => LiveCloseStepAsync("client-events-detach", () =>
            {
                if (client is not null)
                {
                    client.ErrorOccurred -= OnClientErrorOccurred;
                    client.CacheStateChanged -= OnClientCacheStateChanged;
                    client.ReachFileLoaded -= OnChapterFileLoaded;
                }
                return Task.CompletedTask;
            }),
            () => LiveCloseStepAsync("mpv-quit", () => MpvShutdownHelper.QuitCoreAsync(client, NullLogger.Instance, TimeSpan.FromSeconds(3))),
            () => player is not null
                ? LiveCloseStepAsync("mpv-player-dispose", () => player.DisposeAsync().AsTask())
                : LiveCloseStepAsync("mpv-client-dispose", () => client?.DisposeAsync().AsTask() ?? Task.CompletedTask),
            () => LiveCloseStepAsync("smtc-events-detach", () =>
            {
                if (smtc is not null) smtc.ButtonPressed -= OnSmtcButtonPressed;
                return Task.CompletedTask;
            }),
            () => LiveCloseStepAsync("native-smtc-dispose", () => { native?.Dispose(); return Task.CompletedTask; })));
        // Terminal close waits all other owners before clearing even the successfully released pair.
        if (!clearOwners) return;
        ClearLivePlayerOwners();
    }

    private void ClearLivePlayerOwners()
    {
        // Clear only after every real release succeeded. Failed barriers retain all owners and HWND.
        Player = null;
        Client = null;
        _nativeMp = null;
        _smtc = null;
        _clientConfigured = false;
        _liveClientRelease = null;
        _liveRendererRelease = null;
    }

    private async Task LeaveLiveMediaAsync()
    {
        _liveAccountActive = false;
        UnwatchLiveAttempt();
        var release = new LiveReleaseBarrier();
        try
        {
            await release.ReleaseAsync(CloseLiveRoomAsync, () => RetireLivePlayerAsync(), UnregisterLiveAccountAsync);
        }
        catch (Exception) { _liveReleaseBlocked = true; throw; }
        LiveConnector?.Detach();
        _liveRoom = null;
        _liveScope = null;
        _liveInformation = null;
        _liveCanonicalRoom = null;
        _liveAccount = null;
        _liveAccountEnded = false;
        LiveSources.Clear();
        SelectedLiveSource = null;
        RequestedLiveSource = null;
        LiveLoadState = LiveMediaLoadState.Idle;
        NotifyLivePropertiesChanged();
    }

    private async Task CloseLiveRoomAsync()
    {
        if (_liveRoom is not null)
        {
            await LiveCloseStepAsync("room-close", () => _liveCloseRetrying ? _liveRoom.RetryCloseAsync() : _liveRoom.CloseAsync());
        }
        await LiveCloseStepAsync("room-release-confirmed", () => _liveRoom is null || _liveRoom.ReleaseConfirmed
            ? Task.CompletedTask : Task.FromException(new InvalidOperationException("Live room release is unconfirmed.")));
        await LiveCloseStepAsync("unadopted-chat-dispose", () => _unadoptedLiveChat?.DisposeAsync().AsTask() ?? Task.CompletedTask);
        await LiveCloseStepAsync("chat-factory-work", () =>
        {
            if (_liveCloseRetrying && _liveChatFactory.HasWork) _liveChatFactory.RetryRelease();
            _liveChatFactory.ForgetReleased();
            return !_liveChatFactory.HasWork ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException("Live chat allocation release is unconfirmed."));
        });
    }

    private async Task UnregisterLiveAccountAsync() => await _liveAccountRegistration.DisposeAsync();

    private void BeginLiveClose()
    {
        _liveCloseStartedAt = Stopwatch.GetTimestamp();
        _liveCloseFailure = null;
        SafeFailureContext.TryWriteFacts(() => logger, log =>
        {
            if (log.IsEnabled(LogLevel.Information))
                log.LogInformation("Live | close begin reason={Reason:l}", _liveAccountEnded ? "account" : LiveCloseDiagnostics.Reason);
        });
        void Attempt(string name, Action action)
        {
            var startedAt = Stopwatch.GetTimestamp();
            Exception? failure = null;
            try { action(); }
            catch (Exception error)
            {
                failure = error;
                _closeCancellationErrors.Add(error);
                _liveCloseActionFailures.Add((name, action, error));
            }
            finally { LogLiveCloseStep(name, failure, Stopwatch.GetElapsedTime(startedAt)); }
        }
        Attempt("attempt-invalidate", () => _liveAttempt?.Invalidate());
        Attempt("room-request-close", () => _liveRoom?.RequestClose());
        Attempt("connector-invalidate", () => LiveConnector?.InvalidateRequests());
        Attempt("danmaku-hide", HideMpvDanmaku);
        Attempt("command-danmaku-clear", ClearActiveCommandDanmakus);
        Attempt("sponsor-cleanup-begin", StartSponsorApplicationCleanup);
        Attempt("playback-diagnostics-stop-begin", () => _ = SuspendPlaybackDiagnosticsAsync());
        Attempt("network-monitor-stop-begin", () => _ = SuspendNetworkMonitorAsync());
    }

    internal Task RetryLiveCloseAsync()
    {
        if (_closeTask is not { IsFaulted: true } || !HasOwnedLiveResources && !IsLiveMedia) return CloseAsync();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _closeTask = completion.Task;
        _liveCloseRetrying = true;
        _liveCloseStartedAt = Stopwatch.GetTimestamp();
        _liveCloseFailure = null;
        LogLiveAction("close begin reason=retry");
        _closeWorker = CompleteLiveCloseAsync(completion, null);
        return completion.Task;
    }

    private async Task CompleteLiveCloseAsync(TaskCompletionSource completion, Action? cancelOpening)
    {
        await Task.Yield();
        if (_liveCloseRetrying)
        {
            foreach (var failure in _liveCloseActionFailures.ToArray())
            {
                var startedAt = Stopwatch.GetTimestamp();
                Exception? error = null;
                try
                {
                    failure.Retry();
                    _closeCancellationErrors.Remove(failure.Error);
                    _liveCloseActionFailures.Remove(failure);
                }
                catch (Exception exception) { error = exception; }
                finally { LogLiveCloseStep(failure.Name, error, Stopwatch.GetElapsedTime(startedAt)); }
            }
        }
        else
        {
        var cancelStartedAt = Stopwatch.GetTimestamp();
        var cancellationErrors = _closeCancellationErrors.Count;
        CancelOpeningForClose(cancelOpening);
        if (_closeCancellationErrors.Count > cancellationErrors && cancelOpening is not null)
            _liveCloseActionFailures.Add(("opening-cancel", cancelOpening, _closeCancellationErrors[cancellationErrors]));
        LogLiveCloseStep("opening-cancel", _closeCancellationErrors.Count > cancellationErrors ? _closeCancellationErrors[cancellationErrors] : null,
            Stopwatch.GetElapsedTime(cancelStartedAt));
        void Attempt(string name, Action action)
        {
            var startedAt = Stopwatch.GetTimestamp();
            Exception? failure = null;
            try { action(); }
            catch (Exception error)
            {
                failure = error;
                _closeCancellationErrors.Add(error);
                _liveCloseActionFailures.Add((name, action, error));
            }
            finally { LogLiveCloseStep(name, failure, Stopwatch.GetElapsedTime(startedAt)); }
        }
#pragma warning disable VSTHRD103 // Synchronous cancellation scopes finish before the real owned-work/native close drain.
        Attempt("player-lifetime-cancel", () => _lifetime.Cancel());
        Attempt("media-cancel", () => _mediaCancellation?.Cancel());
#pragma warning restore VSTHRD103
        Attempt("connector-work-retain", RetainConnectorWork);
        Attempt("overlay-input-cancel", () => _playerOverlay?.CancelPendingInput());
        Attempt("danmaku-dialog-hide", () => _activeDanmakuDialog?.Hide());
        }
        try
        {
            await DisposeLiveCoreAsync();
            _liveReleaseBlocked = false;
            LogLiveCloseResult(success: true);
            completion.SetResult();
        }
        catch (Exception error)
        {
            _liveReleaseBlocked = true;
            if (_liveCloseFailure is null)
            {
                LiveCloseDiagnostics.Record(error, "player-finalize");
                _liveCloseFailure = LiveCloseDiagnostics.GetFailure(error);
            }
            LogLiveCloseResult(success: false);
            completion.SetException(LiveCloseDiagnostics.Wrap("Live close is incomplete. Owned resources are retained.", error));
            NotifyLivePropertiesChanged();
        }
    }

    private async Task DisposeLiveCoreAsync()
    {
        await (_liveCloseRetrying && _liveCloseRelease.HasStarted ? _liveCloseRelease.RetryAsync() : _liveCloseRelease.ReleaseAsync(
            () => LiveCloseStepAsync("host-prepare", () => Window is MainPlayerHost host ? host.PrepareForCloseAsync() : Task.CompletedTask),
            () => LiveCloseStepAsync("sponsor-binding-retire", () =>
            {
                RetireSponsorBinding(RetirementReason.Closing);
                return Task.CompletedTask;
            }),
            () => LiveCloseStepAsync("player-work-drain", DrainPlayerWorkAsync),
            () => LiveCloseStepAsync("sponsor-cleanup", AwaitSponsorApplicationCleanupAsync),
            () => LiveCloseStepAsync("danmaku-renderers", ReleaseLiveRenderersAsync),
            () => LiveCloseStepAsync("room-release", CloseLiveRoomAsync),
            () => LiveCloseStepAsync("player-release", () => RetireLivePlayerAsync(releaseRenderers: false, clearOwners: false)),
            () => LiveCloseStepAsync("account-unregister", UnregisterLiveAccountAsync),
            () => LiveCloseStepAsync("cancellation-confirmed", () => _closeCancellationErrors.Count == 0 ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException("Live cancellation/invalidation failed; ownership retained.")))));

        if (_playerOverlay is not null)
        {
            _playerOverlay.KeyDown -= OnPlayerKeyDown;
            _playerOverlay.RemoveHandler(UIElement.KeyUpEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(OnPlayerKeyUp));
            _playerOverlay.LostFocus -= OnPlayerInputLostFocus;
            _playerOverlay = null;
        }
        Window?.SetActiveAction(null);
        Danmaku.ClearAll();
        ClearCommandDanmakuMedia();
        if (_tripleTimer is not null) { _tripleTimer.Tick -= OnTripleTimerTick; _tripleTimer.Stop(); _tripleTimer = null; }
        if (Connector is { } connector)
        {
            connector.PlaylistInitialized -= OnConnectorPlaylistInitialized;
            connector.NewMediaRequest -= OnConnectorNewMediaRequest;
            connector.RequestOpenExtraPanel -= OnRequestOpenExtraPanel;
            connector.PropertiesUpdated -= OnConnectorPropertiesUpdated;
        }
        LiveConnector?.Detach();
        if (Window?.IsDisposed == false)
        {
            Window.SizeChanged -= OnWindowSizeChanged;
            Window.CloseRequested -= OnHostCloseRequested;
            Window.PresentationChanged -= OnHostPresentationChanged;
            if (_pointerSource is not null) _pointerSource.PointerReleased -= OnWindowPointerReleased;
            await LiveCloseStepAsync("host-dispose", async () =>
            {
                await Window.DisposeAsync();
            });
        }
        this.Get<AppViewModel>().ReleasePlayer(this);
        ClearLivePlayerOwners();
        _mediaCancellation?.Dispose();
        foreach (var retired in _retiredMediaCancellations) retired.Dispose();
        _retiredMediaCancellations.Clear();
        _lifetime.Dispose();
        _liveAccountActive = false;
        LiveLoadState = LiveMediaLoadState.Closed;
        NotifyLivePropertiesChanged();
    }

    private void NotifyLivePropertiesChanged()
    {
        foreach (var property in new[] { nameof(IsLiveMedia), nameof(LiveConnector), nameof(LiveRoom), nameof(SendLiveDanmakuCommand),
            nameof(CanSendLiveDanmaku), nameof(LivePlayableQualities), nameof(IsLiveStarting), nameof(HasLiveFrame), nameof(IsLiveReady),
            nameof(IsLiveFailed), nameof(IsLiveReleaseBlocked), nameof(LiveReleaseConfirmed), nameof(CanSelectLiveQuality) })
            OnPropertyChanged(property);
        SelectLiveSourceCommand.NotifyCanExecuteChanged();
        LiveConnector?.NotifyPlaybackStateChanged();
    }

    partial void OnLiveLoadStateChanged(LiveMediaLoadState value) => NotifyLivePropertiesChanged();

    private void LogLiveAction(string action)
        => SafeFailureContext.TryWriteFacts(() => logger, log =>
        {
            if (log.IsEnabled(LogLevel.Information)) log.LogInformation("Live | {Action:l}", action);
        });

    private async Task LiveCloseStepAsync(string name, Func<Task> operation)
    {
        var startedAt = Stopwatch.GetTimestamp();
        Exception? failure = null;
        try { await operation(); }
        catch (Exception error) { failure = error; LiveCloseDiagnostics.Record(error, name); throw; }
        finally { LogLiveCloseStep(name, failure, Stopwatch.GetElapsedTime(startedAt)); }
    }

    private void LogLiveCloseStep(string name, Exception? error, TimeSpan elapsed)
    {
        if (error is not null)
        {
            LiveCloseDiagnostics.Record(error, name);
            _liveCloseFailure ??= LiveCloseDiagnostics.GetFailure(error);
        }
        SafeFailureContext.TryWriteFacts(() => logger, log =>
        {
            var level = error is null ? LogLevel.Information : LogLevel.Warning;
            if (log.IsEnabled(level))
                log.Log(level, "Live | close-step name={Name:l} result={Result:l} error={Error:l} inner={Inner:l} ms={Milliseconds}",
                    name, error is null ? "ok" : "failed", error?.GetType().Name ?? "none",
                    error?.InnerException?.GetType().Name ?? "none", (long)elapsed.TotalMilliseconds);
        });
    }

    private void LogLiveCloseResult(bool success)
        => SafeFailureContext.TryWriteFacts(() => logger, log =>
        {
            var level = success ? LogLevel.Information : LogLevel.Warning;
            if (log.IsEnabled(level))
                log.Log(level, "Live | close result={Result:l} failed-step={FailedStep:l} total-ms={Milliseconds}",
                    success ? "ok" : "incomplete", success ? "none" : _liveCloseFailure?.Step ?? "none",
                    (long)Stopwatch.GetElapsedTime(_liveCloseStartedAt).TotalMilliseconds);
        });
}
