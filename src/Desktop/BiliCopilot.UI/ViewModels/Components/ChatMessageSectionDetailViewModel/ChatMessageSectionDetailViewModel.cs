// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Pages.Overlay;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Humanizer;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models.User;
using Richasy.WinUIKernel.Share.Base;
using Richasy.WinUIKernel.Share.ViewModels;
using Richasy.WinUIKernel.Share.Toolkits;
using BiliCopilot.UI.ViewModels.View;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 聊天消息区块详情视图模型.
/// </summary>
public sealed partial class ChatMessageSectionDetailViewModel : ViewModelBase<ChatSession>, IMessageSectionDetailViewModel
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ChatMessageSectionDetailViewModel"/> class.
    /// </summary>
    public ChatMessageSectionDetailViewModel(
        ChatSession session,
        AccountViewModel account)
        : base(session)
    {
        _service = this.Get<IMessageService>();
        _account = account;
        _logger = this.Get<ILogger<ChatMessageSectionDetailViewModel>>();

        Avatar = (session.Avatar ?? session.User?.Avatar)?.Uri;
        UserName = session.Name ?? session.User?.Name ?? string.Empty;
        if (session.IsSystemSession && string.IsNullOrWhiteSpace(UserName))
            UserName = this.Get<IResourceToolkit>().GetLocalizedString("SystemChatSession");
        UnreadCount = session.UnreadCount;
        LastMessage = Data.LastMessage ?? string.Empty;
        LastMessageTime = Data.Time.Humanize(default, new System.Globalization.CultureInfo("zh-CN"));
    }

    [RelayCommand]
    private Task InitializeAsync() => _operation is { Task.IsCompleted: false } pending && IsCurrent(pending, _operation)
        ? pending.Task : _needsRefresh ? StartLoad(refresh: true) : IsInitialized ? Task.CompletedTask : StartLoad(refresh: false);

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private void ShowUserSpace()
    {
        if (CanSendMessage) this.Get<NavigationViewModel>().NavigateToOver(typeof(UserSpacePage), Data.User);
    }

    [RelayCommand]
    private Task RefreshAsync() => StartLoad(refresh: true);

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private Task SendMessageAsync()
    {
        if (!CanSendMessage) return Task.CompletedTask;
#pragma warning disable VSTHRD003
        if (_sendOwner is { Task.IsCompleted: false } pending && IsCurrent(pending, _sendOwner)) return pending.Task;
#pragma warning restore VSTHRD003
        if (string.IsNullOrEmpty(InputText) || _retired || _account.AccountId is null) return Task.CompletedTask;
        var owner = new MessageRequestOwner(_generation, _account.AccountGeneration, _account.AccountId, _sendOwner?.Task ?? Task.CompletedTask);
        var user = Data.User;
        var input = InputText;
        _sendOwner = owner;
        _ = CompleteSendAsync(owner, user, input, _activationEpoch);
        return owner.Task;
    }

    private async Task CompleteSendAsync(MessageRequestOwner owner, Richasy.BiliKernel.Models.User.UserProfile user, string input, long epoch)
    {
        var success = false;
        try
        {
#pragma warning disable VSTHRD003
            await owner.Previous;
#pragma warning restore VSTHRD003
            if (!IsCurrent(owner, _sendOwner)) return;
            IsSending = true;
            if (!IsCurrent(owner, _sendOwner)) return;
            var now = DateTimeOffset.Now;
            var msg = await _service.SendChatMessageAsync(input, user);
            if (!IsCurrent(owner, _sendOwner)) return;
            if (msg is not null)
            {
                Messages.Add(new ChatMessageItemViewModel(msg));
                if (!IsCurrent(owner, _sendOwner)) return;
                if (InputText == input) InputText = string.Empty;
                if (!IsCurrent(owner, _sendOwner)) return;
                LastMessage = msg.Content.Text;
                if (!IsCurrent(owner, _sendOwner)) return;
                LastMessageTime = now.Humanize(default, new System.Globalization.CultureInfo("zh-CN"));
                if (IsCurrent(owner, _sendOwner) && _active && epoch == _activationEpoch) RequestScrollToBottom?.Invoke(this, EventArgs.Empty);
            }
            success = true;
        }
        catch (Exception ex)
        {
            if (!IsCurrent(owner, _sendOwner)) return;
            _logger.LogError("发送消息时失败 ({ErrorType})", ex.GetType().Name);
            this.Get<AppViewModel>().ShowTipCommand.Execute((ResourceToolkit.GetLocalizedString(StringNames.FailedToSendMessage), InfoType.Error));
        }
        finally
        {
            if (IsCurrent(owner, _sendOwner)) IsSending = false;
            if (IsCurrent(owner, _sendOwner)) IsEmpty = Messages.Count == 0;
            owner.Complete(success);
        }
    }

    internal void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        _activationEpoch++;
        MarkAsReadCommand.NotifyCanExecuteChanged();
    }

    internal Task Retire()
    {
        _retired = true;
        _generation++;
        SetActive(false);
        return Task.WhenAll(_operation?.Task ?? Task.CompletedTask, _sendOwner?.Task ?? Task.CompletedTask, _readOwner?.Task ?? Task.CompletedTask);
    }

    internal Task InvalidateData()
    {
        _generation++;
        _needsRefresh = true;
        MarkAsReadCommand.NotifyCanExecuteChanged();
        return Task.WhenAll(_operation?.Task ?? Task.CompletedTask, _sendOwner?.Task ?? Task.CompletedTask, _readOwner?.Task ?? Task.CompletedTask);
    }

    private bool IsCurrent(MessageRequestOwner owner, MessageRequestOwner? slot) => !_retired
        && owner.IsCurrent(slot, _generation, _account.AccountGeneration, _account.AccountId);

    private Task StartLoad(bool refresh)
    {
        if (_retired || _account.AccountId is null) return Task.CompletedTask;
#pragma warning disable VSTHRD003
        if (_operation is { Task.IsCompleted: false } pending && IsCurrent(pending, _operation) && (!refresh || pending.Refresh)) return pending.Task;
#pragma warning restore VSTHRD003
        if (!refresh && _preventLoadMore) return Task.CompletedTask;
        var previous = Task.WhenAll(_operation?.Task ?? Task.CompletedTask, _sendOwner?.Task ?? Task.CompletedTask, _readOwner?.Task ?? Task.CompletedTask);
        if (refresh) _generation++;
        var owner = new MessageRequestOwner(_generation, _account.AccountGeneration, _account.AccountId, previous, refresh);
        var session = Data;
        _operation = owner;
        MarkAsReadCommand.NotifyCanExecuteChanged();
        _ = CompleteLoadAsync(owner, session, _activationEpoch);
        return owner.Task;
    }

    private async Task CompleteLoadAsync(MessageRequestOwner owner, ChatSession session, long epoch)
    {
        var success = false;
        try
        {
            // A replacement drains the uncancelable predecessor without admitting another request.
#pragma warning disable VSTHRD003
            await owner.Previous;
#pragma warning restore VSTHRD003
            if (!IsCurrent(owner, _operation)) return;
            IsLoading = true;
            if (!IsCurrent(owner, _operation)) return;
            var (messages, maxCount, hasMore) = await _service.GetChatMessagesAsync(session.User);
            if (!IsCurrent(owner, _operation)) return;
            _maxSequence = maxCount;
            if (owner.Refresh) Messages = new();
            if (!IsCurrent(owner, _operation)) return;
            if (messages is not null)
            {
                foreach (var item in messages)
                {
                    if (!IsCurrent(owner, _operation)) return;
                    Messages.Add(new ChatMessageItemViewModel(item, session.IsSystemSession));
                }

                if (!IsCurrent(owner, _operation)) return;
                var last = messages.LastOrDefault();
                var preview = session.IsSystemSession ? last?.PreviewText ?? last?.Content?.Text : last?.Content?.Text;
                if (last is not null && LastMessage != preview)
                {
                    LastMessage = preview ?? string.Empty;
                    if (!IsCurrent(owner, _operation)) return;
                    LastMessageTime = last.Time.Humanize(default, new System.Globalization.CultureInfo("zh-CN"));
                }

                if (IsCurrent(owner, _operation) && _active && epoch == _activationEpoch) RequestScrollToBottom?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _preventLoadMore = true;
            }
            if (!IsCurrent(owner, _operation)) return;
            IsInitialized = true;
            _needsRefresh = false;
            success = true;
        }
        catch (Exception ex)
        {
            if (!IsCurrent(owner, _operation)) return;
            _logger.LogError("加载聊天消息时失败 ({ErrorType})", ex.GetType().Name);
            this.Get<AppViewModel>().ShowTipCommand.Execute((ResourceToolkit.GetLocalizedString(StringNames.FailedToLoadChatMessages), InfoType.Error));
        }
        finally
        {
            if (IsCurrent(owner, _operation)) IsLoading = false;
            if (IsCurrent(owner, _operation)) IsEmpty = Messages.Count == 0;
            owner.Complete(success);
            if (IsCurrent(owner, _operation)) MarkAsReadCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanMarkAsRead))]
    private Task MarkAsReadAsync()
    {
#pragma warning disable VSTHRD003
        if (_readOwner is { Task.IsCompleted: false } pending && IsCurrent(pending, _readOwner)) return pending.Task;
#pragma warning restore VSTHRD003
        if (!CanMarkAsRead) return Task.CompletedTask;
        var owner = new MessageRequestOwner(_generation, _account.AccountGeneration, _account.AccountId, _readOwner?.Task ?? Task.CompletedTask);
        _readOwner = owner;
        MarkAsReadCommand.NotifyCanExecuteChanged();
        _ = CompleteMarkAsReadAsync(owner, _maxSequence, _activationEpoch);
        return owner.Task;
    }

    private async Task CompleteMarkAsReadAsync(MessageRequestOwner owner, long maxSequence, long epoch)
    {
        var success = false;
        try
        {
#pragma warning disable VSTHRD003
            await owner.Previous;
#pragma warning restore VSTHRD003
            if (!IsCurrent(owner, _readOwner) || !_active || epoch != _activationEpoch) return;
            IsMarkingRead = true;
            if (!IsCurrent(owner, _readOwner)) return;
            await _service.MarkChatMessagesAsReadAsync(Data.User, maxSequence);
            if (!IsCurrent(owner, _readOwner)) return;
            UnreadCount = 0;
            if (!IsCurrent(owner, _readOwner)) return;
            Data.UnreadCount = 0;
            if (_active && epoch == _activationEpoch) await _account.UpdateUnreadSnapshotAsync();
            success = true;
        }
        catch (Exception ex)
        {
            if (!IsCurrent(owner, _readOwner)) return;
            _logger.LogError("更新会话已读游标失败 ({ErrorType})", ex.GetType().Name);
            this.Get<AppViewModel>().ShowTipCommand.Execute((this.Get<IResourceToolkit>().GetLocalizedString("FailedToMarkChatRead"), InfoType.Error));
        }
        finally
        {
            if (IsCurrent(owner, _readOwner)) IsMarkingRead = false;
            owner.Complete(success);
            if (IsCurrent(owner, _readOwner)) MarkAsReadCommand.NotifyCanExecuteChanged();
        }
    }
}
