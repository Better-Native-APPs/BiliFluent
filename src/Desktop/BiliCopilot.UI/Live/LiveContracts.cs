#nullable enable

using System.Collections.ObjectModel;
using System.ComponentModel;
using BiliCopilot.UI.Live.Application;
using Richasy.BiliKernel.Models.Media;
using Richasy.BiliKernel.Models.User;

namespace BiliCopilot.UI.Live;

public readonly record struct LiveAccountStamp(string? AccountId, long Generation, CancellationToken Lifetime);

public readonly record struct LiveBinding(Guid SessionId, long RoomGeneration, long RoomId,
    long AccountGeneration, string? AccountId);

public enum LivePlaybackState { Idle, Opening, Playing, Paused, Ended, Failed, Closing, Closed }

public enum LiveChatState { Idle, Connecting, Authenticating, Connected, Retrying, Unavailable, Failed, Closed }

public enum LiveSendOutcome { NotSent, Accepted, Rejected, Unknown }

public sealed record LiveChatMessage(LiveBinding Binding, string? EventId, long UserId,
    string UserName, string Text, DateTimeOffset ReceivedAt);

public sealed record LiveChatStatus(LiveBinding Binding, LiveChatState State, string? SafeError);

public enum LiveAudienceMetric { OnlineRank, Watched }

public sealed record LiveAudienceUpdate(LiveBinding Binding, long Count, LiveAudienceMetric Metric);

// Optional control-data channel: audience counts never enter the display-message collection.
public interface ILiveAudienceSource
{
    event EventHandler<LiveAudienceUpdate>? AudienceChanged;
}

public sealed record LiveSendResult(LiveBinding Binding, LiveSendOutcome Outcome,
    int? ApiCode, string SafeMessage);

public interface ILiveChatClient : IAsyncDisposable
{
    event EventHandler<LiveChatMessage>? MessageReceived;
    event EventHandler<LiveChatStatus>? StatusChanged;
    Task ConnectAsync(LiveBinding binding, CancellationToken cancellationToken);
    Task DisconnectAsync();
    Task<LiveSendResult> SendManuallyAsync(LiveBinding binding, string text, CancellationToken cancellationToken);
}

// Optional same-assembly diagnostics; the public client/room contracts stay unchanged.
internal interface ILiveChatDiagnosticSink
{
    void ReportRoomStatistics(long inboxIn, long inboxDrop, long published, long dispatchFail);
    void ReportCloseStep(string name, Exception? error, TimeSpan elapsed);
    void ReportOwnedTaskFault(string name, Exception error, bool expectedShutdown = false);
}

/// <summary>Room/chat ownership, independent of video playback and window presentation.</summary>
public interface ILiveRoomSession : INotifyPropertyChanged, IAsyncDisposable
{
    LiveBinding? Binding { get; }
    LiveRoomScope? CurrentScope { get; }
    string Title { get; }
    string AnchorName { get; }
    Uri? AnchorAvatar { get; }
    UserProfile? Anchor { get; }
    bool? IsFollowing { get; }
    long? AudienceCount { get; }
    string AudienceLabel { get; }
    LiveChatState ChatState { get; }
    string? ChatError { get; }
    bool CanSend { get; }
    bool IsClosing { get; }
    bool ReleaseConfirmed { get; }
    ReadOnlyObservableCollection<LiveChatMessage> Messages { get; }
    event EventHandler<LiveChatMessage>? MessageReceived;
    LiveRoomScope PrepareRoom(LiveInformation live, LiveAccountStamp account, CancellationToken cancellationToken);
    void PublishRoomReset(LiveRoomScope scope);
    void BindRoom(LiveRoomScope scope, long roomId, LiveInformation information);
    Task ConnectAsync(LiveRoomScope scope);
    Task StopAsync(LiveRoomScope scope);
    void RequestClose();
    Task<LiveSendResult> SendManuallyAsync(LiveBinding binding, string text, CancellationToken cancellationToken);
    Task CloseAsync();
    Task RetryCloseAsync() => CloseAsync();
}
