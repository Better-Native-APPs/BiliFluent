// Copyright (c) Bili Copilot. All rights reserved.

using System.Windows.Input;

namespace BiliCopilot.UI.Controls;

/// <summary>
/// 为内置 <see cref="ItemsView"/> 提供增量加载：滚动接近底部时执行加载命令.
/// </summary>
public static class ItemsViewLoadMore
{
    /// <summary>
    /// 距离底部多少像素以内时触发加载.
    /// </summary>
    private const double LoadMoreThreshold = 1600d;

    /// <summary>
    /// 加载更多命令.
    /// </summary>
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.RegisterAttached("Command", typeof(ICommand), typeof(ItemsViewLoadMore), new PropertyMetadata(default, OnCommandChanged));

    private static readonly DependencyProperty SubscriptionProperty =
        DependencyProperty.RegisterAttached("Subscription", typeof(object), typeof(ItemsViewLoadMore), new PropertyMetadata(default));

    /// <summary>
    /// 获取加载更多命令.
    /// </summary>
    /// <returns>命令.</returns>
    public static ICommand? GetCommand(ItemsView view) => (ICommand?)view.GetValue(CommandProperty);

    /// <summary>
    /// 设置加载更多命令.
    /// </summary>
    public static void SetCommand(ItemsView view, ICommand? value) => view.SetValue(CommandProperty, value);

    /// <summary>
    /// 列表数据更新后稍作等待，再检查是否需要继续加载（内容不足一屏时）.
    /// </summary>
    /// <returns><see cref="Task"/>.</returns>
    public static async Task DelayCheckItemsAsync(this ItemsView view)
    {
        await Task.Delay(500);
        (view.GetValue(SubscriptionProperty) as Subscription)?.Check(isFollowUp: true);

        // 布局较慢时新内容可能还没量出来，稍后再看一次；内容没有变化时这次检查不会发起加载.
        await Task.Delay(1500);
        (view.GetValue(SubscriptionProperty) as Subscription)?.Check(isFollowUp: true);
    }

    /// <summary>
    /// 回到列表顶部.
    /// </summary>
    public static void ResetScrollPosition(this ItemsView view)
        => view.ScrollView?.ScrollTo(0d, 0d, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsView view)
        {
            return;
        }

        if (e.NewValue is null)
        {
            (view.GetValue(SubscriptionProperty) as Subscription)?.Dispose();
            view.ClearValue(SubscriptionProperty);
            return;
        }

        if (view.GetValue(SubscriptionProperty) is null)
        {
            view.SetValue(SubscriptionProperty, new Subscription(view));
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly ItemsView _view;
        private readonly LoadMoreGate _gate = new();
        private ScrollView? _scroll;

        public Subscription(ItemsView view)
        {
            _view = view;
            _view.Loaded += OnLoaded;
            _view.Unloaded += OnUnloaded;
            if (_view.IsLoaded)
            {
                Attach();
            }
        }

        public void Dispose()
        {
            _view.Loaded -= OnLoaded;
            _view.Unloaded -= OnUnloaded;
            Detach();
        }

        public void Check(bool isFollowUp)
        {
            if (_scroll is null || !_view.IsLoaded)
            {
                return;
            }

            _view.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                // 排队期间可能已被卸载或隐藏，执行时重新判断.
                if (_scroll is { } scroll
                    && _gate.TryEnter(_view, scroll.Content as FrameworkElement, scroll.ExtentHeight, scroll.ViewportHeight, scroll.VerticalOffset, LoadMoreThreshold, isFollowUp))
                {
                    GetCommand(_view)?.Execute(default);
                }
            });
        }

        private void OnLoaded(object sender, RoutedEventArgs e) => Attach();

        private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

        private void Attach()
        {
            if (_scroll is not null || _view.ScrollView is not ScrollView scroll)
            {
                return;
            }

            _scroll = scroll;
            _scroll.ViewChanged += OnViewChanged;
            _scroll.ExtentChanged += OnExtentChanged;
            _view.SizeChanged += OnSizeChanged;
        }

        private void Detach()
        {
            if (_scroll is null)
            {
                return;
            }

            _scroll.ViewChanged -= OnViewChanged;
            _scroll.ExtentChanged -= OnExtentChanged;
            _view.SizeChanged -= OnSizeChanged;
            _scroll = null;
        }

        private void OnViewChanged(ScrollView sender, object args) => Check(isFollowUp: false);

        // 内容高度只在真实布局后变化：隐藏的页面不会触发，没有新内容的加载也不会触发.
        private void OnExtentChanged(ScrollView sender, object args) => Check(isFollowUp: false);

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width > 100)
            {
                Check(isFollowUp: false);
            }
        }
    }
}
