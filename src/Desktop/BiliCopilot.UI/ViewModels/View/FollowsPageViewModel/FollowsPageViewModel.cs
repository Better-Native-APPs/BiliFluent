// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Pages.Overlay;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models.User;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// 关注页面视图模型.
/// </summary>
public sealed partial class FollowsPageViewModel : LayoutPageViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FollowsPageViewModel"/> class.
    /// </summary>
    public FollowsPageViewModel(
        IRelationshipService service,
        ILogger<FollowsPageViewModel> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override string GetPageKey()
        => nameof(FollowsPage);

    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (Groups.Count > 0)
        {
            return;
        }

        await LoadGroupsAsync();
        await Task.Delay(200);
        var lastSelectedGroupId = SettingsToolkit.ReadLocalSetting(SettingNames.FollowsPageLastSelectedGroupId, string.Empty);
        var group = Groups.FirstOrDefault(p => p.Id == lastSelectedGroupId) ?? Groups.FirstOrDefault(p => p.TotalCount > 0) ?? Groups.First();
        SelectGroupCommand.Execute(group);
        GroupInitialized?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void SelectGroup(UserGroup group)
    {
        if (group == null || group == SelectedGroup)
        {
            return;
        }

        SelectedGroup = group;
        SettingsToolkit.WriteLocalSetting(SettingNames.FollowsPageLastSelectedGroupId, group.Id);

        if (!_userCache.TryGetValue(group, out var list))
        {
            list = [];
            _userCache.Add(group, list);
        }

        _recentGroups.Remove(group);
        _recentGroups.AddFirst(group);
        if (_recentGroups.Count > 4)
        {
            var evictedGroup = _recentGroups.Last!.Value;
            _recentGroups.RemoveLast();
            _userCache.Remove(evictedGroup);
            _offsetCache.Remove(evictedGroup);
        }

        Users.Clear();
        foreach (var user in list)
        {
            Users.Add(user);
        }

        IsEmpty = Users.Count == 0;
        UserListUpdated?.Invoke(this, EventArgs.Empty);
        if (list.Count == 0)
        {
            this.Get<DispatcherQueue>().TryEnqueue(DispatcherQueuePriority.Low, async () =>
            {
                if (group == SelectedGroup)
                {
                    await LoadUsersAsync();
                }

                IsEmpty = Users.Count == 0;
            });
        }
    }

    [RelayCommand]
    private async Task LoadUsersAsync()
    {
        if (IsUserLoading || SelectedGroup is null)
        {
            return;
        }

        if (Users.Count >= SelectedGroup.TotalCount)
        {
            return;
        }

        var group = SelectedGroup;
        var list = _userCache[group];
        IsUserLoading = true;
        try
        {
            var pn = 0;
            if (_offsetCache.TryGetValue(group, out var value))
            {
                pn = value;
            }

            var (users, nextOffset) = await _service.GetMyFollowUserGroupDetailAsync(group.Id, pn);
            if (!_userCache.TryGetValue(group, out var cachedUsers) || !ReferenceEquals(list, cachedUsers))
            {
                return;
            }

            _offsetCache[group] = nextOffset;
            if (users != null)
            {
                foreach (var item in users)
                {
                    var user = new UserItemViewModel(item);
                    list.Add(user);
                    if (group == SelectedGroup)
                    {
                        Users.Add(user);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "尝试加载关注用户时出错.");
        }
        finally
        {
            IsUserLoading = false;
            IsEmpty = Users.Count == 0;
            if (SelectedGroup is not null
                && _userCache.TryGetValue(SelectedGroup, out var selectedUsers)
                && !ReferenceEquals(list, selectedUsers)
                && selectedUsers.Count == 0)
            {
                await LoadUsersAsync();
            }
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        this.Get<DispatcherQueue>().TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            Users.Clear();
            Groups.Clear();
            _userCache.Clear();
            _offsetCache.Clear();
            _recentGroups.Clear();
            SelectedGroup = default;
            await InitializeAsync();
        });
    }

    private async Task LoadGroupsAsync()
    {
        IsGroupLoading = true;
        try
        {
            var groups = await _service.GetMyFollowUserGroupsAsync();
            if (groups != null)
            {
                foreach (var item in groups)
                {
                    Groups.Add(item);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "尝试加载关注分组时出错.");
        }
        finally
        {
            IsGroupLoading = false;
        }
    }
}
