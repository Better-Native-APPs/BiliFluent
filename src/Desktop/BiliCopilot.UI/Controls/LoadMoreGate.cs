// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI.Xaml.Media;

namespace BiliCopilot.UI.Controls;

/// <summary>
/// 判断一次“接近底部”的检查是否可以真正发起加载.
/// 列表不在实时布局中（页面被隐藏、窗口最小化、视口为零）时内容高度不会变化，
/// 此时放行会让每次加载完成后立刻再触发下一次加载.
/// </summary>
internal sealed class LoadMoreGate
{
    /// <summary>
    /// 内容没有变化时，用户滚动最多每隔这么久重试一次加载.
    /// </summary>
    private const long RetryIntervalMilliseconds = 1000;

    private bool _hasRequested;
    private double _lastRequestContentSize;
    private long _lastRequestTick;

    /// <summary>
    /// 元素是否处于实时布局中：宿主窗口可见，且自身与所有祖先都未折叠.
    /// </summary>
    /// <returns>是否处于实时布局.</returns>
    public static bool IsInLiveLayout(FrameworkElement element)
    {
        if (!element.IsLoaded || element.XamlRoot is not { IsHostVisible: true })
        {
            return false;
        }

        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: Visibility.Collapsed })
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 是否允许发起一次加载.
    /// </summary>
    /// <param name="host">滚动宿主.</param>
    /// <param name="content">滚动内容，用它测量出的高度判断上一次加载是否带来了新内容.</param>
    /// <param name="extent">内容高度.</param>
    /// <param name="viewport">视口高度.</param>
    /// <param name="offset">当前纵向偏移.</param>
    /// <param name="threshold">距离底部多少像素以内算接近底部.</param>
    /// <param name="isFollowUp">是否为数据更新后的跟进检查（不是用户滚动或尺寸变化）.</param>
    /// <returns>允许则为 <c>true</c>.</returns>
    public bool TryEnter(FrameworkElement host, FrameworkElement? content, double extent, double viewport, double offset, double threshold, bool isFollowUp)
    {
        var contentSize = content?.DesiredSize.Height ?? extent;
        if (!host.IsLoaded || viewport <= 0 || host.ActualWidth <= 0 || host.ActualHeight <= 0)
        {
            return false;
        }

        if (extent - viewport - offset > threshold)
        {
            return false;
        }

        if (!IsInLiveLayout(host))
        {
            return false;
        }

        // 上一次请求之后内容没有变化（加载没有带来新内容，或还没完成）：
        // 跟进检查不再连锁，用户滚动也只能间隔重试.
        if (_hasRequested
            && Math.Abs(contentSize - _lastRequestContentSize) < 0.5
            && (isFollowUp || Environment.TickCount64 - _lastRequestTick < RetryIntervalMilliseconds))
        {
            return false;
        }

        _hasRequested = true;
        _lastRequestContentSize = contentSize;
        _lastRequestTick = Environment.TickCount64;
        return true;
    }
}
