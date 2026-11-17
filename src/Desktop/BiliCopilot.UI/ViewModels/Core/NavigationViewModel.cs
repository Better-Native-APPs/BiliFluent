// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls;
using BiliCopilot.UI.Forms;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Pages;
using BiliCopilot.UI.Pages.Overlay;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.View;
using CommunityToolkit.Mvvm.ComponentModel;
using Richasy.WinUIKernel.Share.Toolkits;
using Richasy.WinUIKernel.Share.ViewModels;
using System.Collections.ObjectModel;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 导航视图模型.
/// </summary>
public sealed partial class NavigationViewModel : ViewModelBase, INavServiceViewModel
{
    private NavigationView _navigationView;
    private MainFrame? _navFrame;
    private OverlayFrame? _overFrame;
    private long _navigationSequence;
    private Func<Task>? _closeEmbeddedLive;
    private readonly NavigationOperationQueue _navigationOperations = new();

    /// <summary>Observable completion/failure for the legacy void navigation interface.</summary>
#pragma warning disable VSTHRD003 // Exposes an independent producer, never a prerequisite of that producer.
    public Task LastNavigationTask => _navigationOperations.LastTask;
#pragma warning restore VSTHRD003
    public Exception? LastNavigationError { get; private set; }

    /// <summary>Start the optional selected command and snapshot the actual initial navigation barrier.</summary>
    internal Task NavigateInitialAsync(Action? selectAndNavigate)
    {
        if (selectAndNavigate is not null)
        {
            var previous = _navigationSequence;
            selectAndNavigate();
            if (previous == _navigationSequence)
                throw new InvalidOperationException("Initial navigation did not enqueue an operation.");
        }
        // No completion wrapper: the producer's real close/navigation failure reaches the root.
        var navigation = LastNavigationTask;
        return navigation;
    }

    public void NavigateTo(Type pageType, object? parameter = null)
        => _ = QueueNavigation(() => { NavigateToCore(pageType, parameter); RecordCurrentPage(); });
    public void NavigateToOver(Type pageType, object? parameter = null)
        => _ = QueueNavigation(() => { NavigateToOverCore(pageType, parameter); RecordCurrentPage(); }, allowBackgroundLiveClose: true);
    public void Search(string keyword) => _ = QueueNavigation(() => { SearchCore(keyword); RecordCurrentPage(); });
    public void SearchInRegion(string keyword) => _ = QueueNavigation(() => SearchInRegionCore(keyword));
    public void Back() => _ = GoBackAsync();
    public void Forward() => _ = GoForwardAsync();

    private Task QueueNavigation(Action navigate, bool allowBackgroundLiveClose = false)
        => QueueNavigation(_ => { navigate(); return Task.CompletedTask; }, allowBackgroundLiveClose);

    private Task QueueNavigation(Func<long, Task> navigate, bool allowBackgroundLiveClose = false)
    {
        var app = this.Get<AppViewModel>();
        if (app.IsPlaybackStopping) return Task.CompletedTask;
        SaveCurrentPlayback(); // Capture progress/part before CloseAsync resets the native state.
        var backgroundLiveClose = allowBackgroundLiveClose && !app.HasEmbeddedWork;
        Task liveClose = Task.CompletedTask;
        Task StartCloseAsync()
        {
            // The close starts immediately, including while an earlier navigation/open awaits.
            var playerClose = app.CloseEmbeddedPlayerAsync();
            try { liveClose = _closeEmbeddedLive?.Invoke() ?? Task.CompletedTask; }
            catch (Exception ex)
            {
                liveClose = Task.FromException(ex);
                backgroundLiveClose = false;
            }
            return backgroundLiveClose ? playerClose : Task.WhenAll(playerClose, liveClose);
        }

        var sequence = ++_navigationSequence;
        var navigation = _navigationOperations.Enqueue(StartCloseAsync, () => navigate(sequence));
        OnPropertyChanged(nameof(LastNavigationTask));
        NotifyHistoryAvailability();
        // Navigation may finish first, but its close failure still reaches the existing observer/tip.
        _ = ObserveNavigationAsync(backgroundLiveClose ? Task.WhenAll(navigation, liveClose) : navigation);
        return navigation;
    }

    private async Task ObserveNavigationAsync(Task navigation)
    {
        try
        {
            // This observer owns no gate and is not a prerequisite of the navigation it observes.
#pragma warning disable VSTHRD003
            await navigation;
#pragma warning restore VSTHRD003
            LastNavigationError = null;
        }
        catch (Exception ex)
        {
            LastNavigationError = ex;
            this.Get<AppViewModel>().ShowTipCommand.Execute((ex.Message, Richasy.WinUIKernel.Share.Base.InfoType.Error));
        }
        OnPropertyChanged(nameof(LastNavigationError));
        NotifyHistoryAvailability();
    }

    [ObservableProperty]
    private bool _isOverlayOpen;

    /// <summary>
    /// 导航条目列表.
    /// </summary>
    [ObservableProperty]
    private List<AppNavigationItemViewModel> _menuItems;

    [ObservableProperty]
    private bool _isTopNavBarShown;

    [ObservableProperty]
    private bool _isFavoritesPage;

    [ObservableProperty]
    private bool _isHistoryPage;

    [ObservableProperty]
    private bool _isViewLaterPage;

    [ObservableProperty]
    public partial bool IsTemporaryOverlayClosed { get; set; }

    /// <summary>
    /// 底部条目列表.
    /// </summary>
    public ObservableCollection<AppNavigationItemViewModel> FooterItems { get; } = new();

    /// <inheritdoc/>
    private void NavigateToCore(Type pageType, object? parameter = null)
    {
        if (_navFrame is null)
        {
            throw new InvalidOperationException("导航框架未初始化.");
        }

        ActiveMainWindow();
        var currentContent = _navFrame.GetCurrentContent();
        _mainPage = new(pageType, parameter);
        _overlayPage = null;
        if (IsOverlayOpen)
        {
            IsOverlayOpen = false;
            IsTemporaryOverlayClosed = true;
            CheckSubPageStatus();
        }

        SelectNavigationItem(pageType);
        if (currentContent?.GetType() == pageType)
        {
            if (currentContent is ICancelPageViewModel cancelPage)
            {
                cancelPage.CancelLoading();
            }

            return;
        }

        SettingsToolkit.WriteLocalSetting(SettingNames.LastSelectedFeaturePage, pageType.FullName);
        _navFrame.NavigateTo(pageType, parameter);
    }

    private void SelectNavigationItem(Type pageType)
    {
        var item = MenuItems.Concat(FooterItems).FirstOrDefault(p => p.PageKey == pageType.FullName);
        if (item is not null && _navigationView.SelectedItem != item) _navigationView.SelectedItem = item;
    }

    public void TryClearOverlayPages()
    {
        if (_overFrame?.GetCurrentContent() is ICancelPageViewModel cancelPage)
        {
            cancelPage.CancelLoading();
        }

        IsOverlayOpen = false;
        IsTemporaryOverlayClosed = false;
        _overlayPage = null;
        _overFrame?.ClearBackStack();
    }

    /// <inheritdoc/>
    private void NavigateToOverCore(Type pageType, object? parameter = null)
    {
        if (_overFrame is null)
        {
            throw new InvalidOperationException("导航框架未初始化.");
        }

        if (IsTemporaryOverlayClosed)
        {
            TryClearOverlayPages();
            IsTemporaryOverlayClosed = false;
        }

        ActiveMainWindow();
        _overFrame.NavigateTo(pageType, parameter);
        _overlayPage = new(pageType, parameter);
        IsOverlayOpen = true;
        CheckSubPageStatus();

        var searchBox = this.Get<SearchBoxViewModel>();
        if (pageType == typeof(UserSpacePage))
        {
            searchBox.SetExtraRegion("space", ResourceToolkit.GetLocalizedString(StringNames.UserSpace));
        }
        else if (pageType == typeof(HistoryPage))
        {
            searchBox.SetExtraRegion("history", ResourceToolkit.GetLocalizedString(StringNames.ViewHistory));
        }
        else
        {
            searchBox.SetExtraRegion(string.Empty, string.Empty);
            searchBox.Keyword = string.Empty;
        }
    }

    /// <summary>
    /// 执行搜索.
    /// </summary>
    private void SearchCore(string keyword)
    {
        if (_overFrame is not null && _overFrame.GetCurrentContent() is SearchPage page)
        {
            IsOverlayOpen = true;
            _overlayPage = new(typeof(SearchPage), keyword);
            page.ViewModel.SearchCommand.Execute(keyword);
            return;
        }

        NavigateToOverCore(typeof(SearchPage), keyword);
    }

    /// <summary>
    /// 设置导航条目可见性.
    /// </summary>
    public void SetNavItemVisibility(Type pageType, bool isVisible)
    {
        var item = MenuItems.Find(p => p.PageKey == pageType.FullName);
        item?.IsVisible = isVisible;
    }

    /// <summary>
    /// 在指定区域执行搜索.
    /// </summary>
    private void SearchInRegionCore(string keyword)
    {
        if (_overFrame.GetCurrentContent() is UserSpacePage usp)
        {
            usp.ViewModel.SearchCommand.Execute(keyword);
        }
        else if (_overFrame.GetCurrentContent() is HistoryPage hp)
        {
            hp.ViewModel.SearchCommand.Execute(keyword);
        }
    }

    /// <summary>
    /// 初始化导航视图模型.
    /// </summary>
    public void Initialize(NavigationView view, MainFrame navFrame, OverlayFrame overFrame,
        Func<Task>? closeEmbeddedLive = null)
    {
        if (_navFrame is not null && _overFrame is not null)
        {
            return;
        }

        _navFrame = navFrame;
        _overFrame = overFrame;
        _navigationView = view;
        _closeEmbeddedLive = closeEmbeddedLive;
        _history.Navigate(new(null, null, null));
        this.Get<AppViewModel>().BindMainNavigation(this);
        IsTopNavBarShown = SettingsToolkit.ReadLocalSetting(SettingNames.IsTopNavBarShown, true);

        MenuItems = [.. GetMenuItems()];

        foreach (var item in GetFooterItems())
        {
            FooterItems.Add(item);
        }
    }

    private IReadOnlyList<AppNavigationItemViewModel> GetMenuItems()
    {
        var lastSelectedPage = SettingsToolkit.ReadLocalSetting(SettingNames.LastSelectedFeaturePage, typeof(PopularPage).FullName);
        var list = new List<AppNavigationItemViewModel>
        {
            GetItem<PopularPage>(StringNames.PopularSlim, FluentIcons.Common.Symbol.Rocket),
            GetItem<MomentPage>(StringNames.DynamicFeed, FluentIcons.Common.Symbol.DesignIdeas),
            GetItem<VideoPartitionPage>(StringNames.Video, FluentIcons.Common.Symbol.VideoClip),
            GetItem<LivePartitionPage>(StringNames.Live, FluentIcons.Common.Symbol.VideoChat),
            GetItem<AnimePage>(StringNames.Anime, FluentIcons.Common.Symbol.Dust),
            GetItem<CinemaPage>(StringNames.Cinema, FluentIcons.Common.Symbol.FilmstripPlay),
            GetItem<ArticlePartitionPage>(StringNames.Article, FluentIcons.Common.Symbol.DocumentBulletList),
        };

        foreach (var item in list)
        {
            item.IsSelected = item.PageKey == lastSelectedPage && item.IsVisible;
        }

        if (!list.Any(p => p.IsSelected) && list.Any(p => p.IsVisible))
        {
            list.First(p => p.IsVisible).IsSelected = true;
        }

        return list;
    }

    private IReadOnlyList<AppNavigationItemViewModel> GetFooterItems()
    {
        var list = new List<AppNavigationItemViewModel>
        {
            GetItem<MessagePage>(StringNames.Message, FluentIcons.Common.Symbol.Chat),
            GetItem<SettingsPage>(StringNames.Settings, FluentIcons.Common.Symbol.Settings),
        };

        return list;
    }

    private AppNavigationItemViewModel GetItem<TPage>(StringNames title, FluentIcons.Common.Symbol symbol, bool isSelected = false)
        where TPage : Page
    {
        var isVisible = this.Get<ISettingsToolkit>().ReadLocalSetting($"Is{typeof(TPage).Name}Visible", true);
        return new AppNavigationItemViewModel(this, typeof(TPage), ResourceToolkit.GetLocalizedString(title), symbol, isSelected, isVisible: isVisible);
    }

    private void ActiveMainWindow()
        => this.Get<AppViewModel>().Windows.Find(p => p is MainWindow)?.Activate();

    private void CheckSubPageStatus()
    {
        var overPageType = _overFrame.GetCurrentContent()?.GetType();
        IsFavoritesPage = IsOverlayOpen && overPageType == typeof(FavoritesPage);
        IsHistoryPage = IsOverlayOpen && overPageType == typeof(HistoryPage);
        IsViewLaterPage = IsOverlayOpen && overPageType == typeof(ViewLaterPage);
    }
}
