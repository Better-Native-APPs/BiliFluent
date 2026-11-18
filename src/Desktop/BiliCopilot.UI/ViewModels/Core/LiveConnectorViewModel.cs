// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Live;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Richasy.MpvKernel.Player;
using Richasy.WinUIKernel.Share.ViewModels;
using System.ComponentModel;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>A room/chat adapter. It owns no player, native window, media resolver or render resource.</summary>
public sealed partial class LiveConnectorViewModel : ViewModelBase, IPlayerConnectorViewModel
{
    private readonly Func<bool> _canSendPlayback;
    private long _generation;
    private long _draftRevision;
    private Task<LiveSendResult>? _sendTask;
    private bool _sendAdmitted;
    private bool _active;

    /// <summary>Creates a connector for the room owned and closed by the player.</summary>
    public LiveConnectorViewModel(ILiveRoomSession room, Func<bool> canSendPlayback)
    {
        Room = room ?? throw new ArgumentNullException(nameof(room));
        _canSendPlayback = canSendPlayback ?? throw new ArgumentNullException(nameof(canSendPlayback));
        Room.PropertyChanged += OnRoomPropertyChanged;
        Room.MessageReceived += OnRoomMessageReceived;
    }

    /// <summary>The actual room session, including its bounded messages and real close task.</summary>
    public ILiveRoomSession Room { get; }

    public string Title => Room.Title;
    public string AnchorName => Room.AnchorName;
    public Uri? AnchorAvatar => Room.AnchorAvatar;
    public bool IsActive => _active;

    [ObservableProperty]
    public partial string Draft { get; set; } = string.Empty;

    /// <summary>The input sets this during IME composition; composition cannot submit a message.</summary>
    [ObservableProperty]
    public partial bool IsComposing { get; set; }

    [ObservableProperty]
    public partial bool IsSending { get; private set; }

    [ObservableProperty]
    public partial LiveSendResult? LastSendResult { get; private set; }

    /// <summary>A single explicit send is admitted only when both playback and room permit it.</summary>
    public bool CanSend => CanSubmit(Draft);

    /// <summary>Includes the real send result and this connector's draft/state continuation.</summary>
    public Task PendingSend => _sendTask ?? Task.CompletedTask;

    public event EventHandler<LiveChatMessage>? MessageReceived;
    public event EventHandler<PlayerInformationUpdatedEventArgs>? PropertiesUpdated;

    // Live has no playlist navigation or connector-driven media replacement.
    public event EventHandler<PlaylistInitializedEventArgs> PlaylistInitialized { add { } remove { } }
    public event EventHandler<MediaSnapshot> NewMediaRequest { add { } remove { } }
    public event EventHandler RequestOpenExtraPanel { add { } remove { } }

    /// <inheritdoc/>
    public Task InitializeAsync(MediaSnapshot snapshot, IMpvMediaSourceResolver sourceResolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.Type != BiliMediaType.Live || snapshot.Live is null || Room.IsClosing)
            throw new InvalidOperationException("The live room connector cannot bind this media.");
        ++_generation;
        _active = true;
        LastSendResult = null;
        PublishRoomProperties();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void InvalidateRequests()
    {
        ++_generation;
        _active = false;
        NotifyPlaybackStateChanged();
    }

    public Task PlayNextAsync() => Task.CompletedTask;
    public Task PlayPreviousAsync() => Task.CompletedTask;

    /// <summary>Stops forwarding after real room release. It does not dispose the player's room.</summary>
    internal void Detach()
    {
        InvalidateRequests();
        Room.PropertyChanged -= OnRoomPropertyChanged;
        Room.MessageReceived -= OnRoomMessageReceived;
    }

    /// <summary>Rechecks the playback-side send admission without changing the chat connection.</summary>
    public void NotifyPlaybackStateChanged()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanSend));
        SendDanmakuCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Sends once for an explicit binding. Unknown results are returned, never retried or echoed.</summary>
    public Task<LiveSendResult> SendManuallyAsync(LiveBinding binding, string text,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested || Room.Binding != binding || !CanSubmit(text))
            return Task.FromResult(new LiveSendResult(binding, LiveSendOutcome.NotSent, null, "Live message was not sent."));
        var generation = _generation;
        var revision = _draftRevision;
        _sendAdmitted = true;
        _sendTask = CompleteAsync();
        IsSending = true;
        NotifyPlaybackStateChanged();
        return _sendTask;
        async Task<LiveSendResult> CompleteAsync()
        {
            await Task.Yield();
            try
            {
                LiveSendResult result;
                try
                {
                    result = !_active || generation != _generation || cancellationToken.IsCancellationRequested
                        ? new(binding, LiveSendOutcome.NotSent, null, "Live message was not sent.")
                        : await Room.SendManuallyAsync(binding, text, cancellationToken);
                }
                catch (Exception)
                {
                    result = new(binding, LiveSendOutcome.Unknown, null, "Live message result is unknown. It was not resent.");
                }
                if (_active && generation == _generation && Room.Binding == binding && !Room.IsClosing
                    && Room.CurrentScope?.Token.IsCancellationRequested == false && !cancellationToken.IsCancellationRequested)
                {
                    LastSendResult = result;
                    if (result.Binding == binding && result.Outcome == LiveSendOutcome.Accepted
                        && revision == _draftRevision && string.Equals(Draft, text, StringComparison.Ordinal))
                        Draft = string.Empty;
                }
                return result;
            }
            finally
            {
                _sendAdmitted = false;
                IsSending = false;
                NotifyPlaybackStateChanged();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendDanmakuAsync()
    {
        if (Room.Binding is { } binding)
            await SendManuallyAsync(binding, Draft, Room.CurrentScope?.Token ?? CancellationToken.None);
    }

    private bool CanSubmit(string text) => _active && !IsComposing && !IsSending && !_sendAdmitted
        && _canSendPlayback() && Room.CanSend
        && !string.IsNullOrWhiteSpace(text) && text.Length <= 100;

    partial void OnDraftChanged(string value) { ++_draftRevision; NotifyPlaybackStateChanged(); }
    partial void OnIsComposingChanged(bool value) => NotifyPlaybackStateChanged();

    private void OnRoomPropertyChanged(object? sender, PropertyChangedEventArgs args) => PublishRoomProperties();

    private void PublishRoomProperties()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(AnchorName));
        OnPropertyChanged(nameof(AnchorAvatar));
        NotifyPlaybackStateChanged();
        if (_active) PropertiesUpdated?.Invoke(this, new(Title, AnchorName, AnchorAvatar));
    }

    private void OnRoomMessageReceived(object? sender, LiveChatMessage message)
    {
        if (_active && Room.Binding == message.Binding && !Room.IsClosing)
            MessageReceived?.Invoke(this, message);
    }
}
