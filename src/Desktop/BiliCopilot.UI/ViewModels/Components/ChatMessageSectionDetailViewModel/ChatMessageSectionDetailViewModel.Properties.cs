// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.User;
using System.Collections.ObjectModel;
using BiliCopilot.UI.ViewModels.View;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 聊天消息区块详情视图模型.
/// </summary>
public sealed partial class ChatMessageSectionDetailViewModel
{
    private readonly IMessageService _service;
    private readonly ILogger<ChatMessageSectionDetailViewModel> _logger;

    private bool _preventLoadMore;
    private readonly AccountViewModel _account;
    private MessageRequestOwner? _operation;
    private MessageRequestOwner? _sendOwner;
    private MessageRequestOwner? _readOwner;
    private long _maxSequence;
    private long _generation;
    private bool _retired;
    private bool _needsRefresh;
    private bool _active;
    private long _activationEpoch;
    internal bool IsInitialized { get; private set; }

    [ObservableProperty]
    private string _lastMessage;

    [ObservableProperty]
    private string _lastMessageTime;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MarkAsReadCommand))]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MarkAsReadCommand))]
    private int _unreadCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MarkAsReadCommand))]
    private bool _isMarkingRead;

    [ObservableProperty]
    private bool _isSending;

    [ObservableProperty]
    private string _inputText;

    /// <summary>
    /// 消息列表.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<ChatMessageItemViewModel> _messages = new();

    /// <summary>
    /// 请求滚动到底部.
    /// </summary>
    public event EventHandler RequestScrollToBottom;

    /// <summary>
    /// 头像.
    /// </summary>
    public Uri? Avatar { get; init; }

    /// <summary>
    /// 用户名.
    /// </summary>
    public string UserName { get; init; }

    /// <summary>Gets whether this conversation is read-only.</summary>
    public bool IsSystemSession => Data.IsSystemSession;

    /// <summary>Gets whether ordinary private-message operations are available.</summary>
    public bool CanSendMessage => !IsSystemSession;

    /// <summary>Gets whether the server omitted the system conversation avatar.</summary>
    public bool ShowSystemAvatarFallback => IsSystemSession && Avatar is null;

    /// <summary>Gets whether a visible, loaded system conversation can update its read cursor.</summary>
    public bool CanMarkAsRead => IsSystemSession && _active && !_retired && !_needsRefresh && IsInitialized
        && _maxSequence > 0 && UnreadCount > 0 && !IsLoading && !IsMarkingRead
        && _operation?.Task.IsCompleted != false && _readOwner?.Task.IsCompleted != false;
}
