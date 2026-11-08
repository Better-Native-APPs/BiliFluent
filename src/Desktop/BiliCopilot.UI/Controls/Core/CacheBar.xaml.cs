// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Richasy.MpvKernel.Core.Models;
using System.Diagnostics;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace BiliCopilot.UI.Controls.Core;

public sealed partial class CacheBar : PlayerControlBase
{
    private static readonly TimeSpan _layoutInterval = TimeSpan.FromMilliseconds(500);
    private readonly DispatcherQueueTimer _layoutTimer;
    private readonly List<(UIElement Element, long VisibilityToken, long OpacityToken)> _visibilitySubscriptions = [];
    private readonly List<(double Start, double End)> _drawnRanges = [];
    private PlayerViewModel? _subscribedViewModel;
    private MpvCacheStateEventArgs? _latestCacheState;
    private bool _loaded;
    private bool _layoutPending;
    private bool _hasDrawnState;
    private long _lastLayoutTimestamp;
    private double _drawnDuration;
    private double _drawnWidth;

    public CacheBar()
    {
        _layoutTimer = DispatcherQueue.CreateTimer();
        _layoutTimer.IsRepeating = false;
        _layoutTimer.Tick += OnLayoutTimerTick;
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    protected override void OnViewModelChanged(PlayerViewModel? oldValue, PlayerViewModel? newValue)
        => SubscribeViewModel(_loaded ? newValue : null);

    protected override void OnControlLoaded()
    {
        _loaded = true;
        SubscribeViewModel(ViewModel);
        for (DependencyObject? element = this; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is not UIElement visual) continue;
            _visibilitySubscriptions.Add((visual,
                visual.RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged),
                visual.RegisterPropertyChangedCallback(OpacityProperty, OnVisibilityChanged)));
        }
        ScheduleLayout();
    }

    protected override void OnControlUnloaded()
    {
        _loaded = false;
        _layoutTimer.Stop();
        SubscribeViewModel(null);
        foreach (var (element, visibilityToken, opacityToken) in _visibilitySubscriptions)
        {
            element.UnregisterPropertyChangedCallback(VisibilityProperty, visibilityToken);
            element.UnregisterPropertyChangedCallback(OpacityProperty, opacityToken);
        }
        _visibilitySubscriptions.Clear();
    }

    private void SubscribeViewModel(PlayerViewModel? viewModel)
    {
        var oldValue = _subscribedViewModel;
        if (ReferenceEquals(oldValue, viewModel)) return;
        _subscribedViewModel = viewModel;
        _latestCacheState = null;
        _layoutPending = false;
        if (oldValue is not null) oldValue.CacheStateChanged -= OnCacheStateChanged;
        if (viewModel is not null) viewModel.CacheStateChanged += OnCacheStateChanged;
        _layoutTimer.Stop();
        RootGrid.Children.Clear();
        _drawnRanges.Clear();
        _hasDrawnState = false;
    }

    private void OnCacheStateChanged(object? sender, MpvCacheStateEventArgs e)
    {
        if (!_loaded || !ReferenceEquals(sender, _subscribedViewModel)) return;
        _latestCacheState = e;
        _layoutPending = true;
        ScheduleLayout();
    }

    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty property)
        => ScheduleLayout();

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        _layoutPending = _latestCacheState is not null;
        ScheduleLayout();
    }

    private void OnLayoutTimerTick(DispatcherQueueTimer sender, object args)
    {
        _layoutTimer.Stop();
        ScheduleLayout();
    }

    private void ScheduleLayout()
    {
        if (!_loaded || _visibilitySubscriptions.Any(subscription =>
            subscription.Element.Visibility != Visibility.Visible || subscription.Element.Opacity <= 0))
        {
            _layoutTimer.Stop();
            return;
        }

        if (!_layoutPending) return;
        var elapsed = Stopwatch.GetElapsedTime(_lastLayoutTimestamp);
        if (_lastLayoutTimestamp != 0 && elapsed < _layoutInterval)
        {
            if (!_layoutTimer.IsRunning)
            {
                _layoutTimer.Interval = _layoutInterval - elapsed;
                _layoutTimer.Start();
            }
            return;
        }
        var state = _latestCacheState;
        _layoutPending = false;

        _layoutTimer.Stop();
        _lastLayoutTimestamp = Stopwatch.GetTimestamp();
        var duration = ViewModel?.Player?.Duration ?? 0;
        var actualWidth = RootGrid.ActualWidth;
        if (HasSameLayoutInput(state, duration, actualWidth)) return;

        var rangeIndex = 0;
        var canDraw = double.IsFinite(duration) && duration > 0;
        _drawnRanges.Clear();
        if (state is not null)
        {
            foreach (var range in state.SeekableRanges)
            {
                _drawnRanges.Add((range.Start, range.End));
                if (!canDraw) continue;
                var width = actualWidth * (range.End - range.Start) / duration;
                var leftMargin = actualWidth * range.Start / duration;
                if (!double.IsFinite(width) || !double.IsFinite(leftMargin) || width < 1) continue;
                if (rangeIndex >= RootGrid.Children.Count)
                    RootGrid.Children.Add((Rectangle)CacheBarItemTemplate.LoadContent());
                var rangeEle = (Rectangle)RootGrid.Children[rangeIndex];
                if (rangeEle.Width != width) rangeEle.Width = width;
                var margin = new Thickness(leftMargin, 0, 0, 0);
                if (rangeEle.Margin != margin) rangeEle.Margin = margin;
                rangeIndex++;
            }
        }
        for (var i = RootGrid.Children.Count - 1; i >= rangeIndex; i--) RootGrid.Children.RemoveAt(i);
        _drawnDuration = duration;
        _drawnWidth = actualWidth;
        _hasDrawnState = true;
    }

    private bool HasSameLayoutInput(MpvCacheStateEventArgs? state, double duration, double width)
    {
        if (!_hasDrawnState || !_drawnDuration.Equals(duration) || !_drawnWidth.Equals(width)) return false;

        var rangeIndex = 0;
        if (state is not null)
        {
            foreach (var range in state.SeekableRanges)
            {
                if (rangeIndex >= _drawnRanges.Count) return false;
                var drawnRange = _drawnRanges[rangeIndex];
                if (!drawnRange.Start.Equals(range.Start) || !drawnRange.End.Equals(range.End)) return false;
                rangeIndex++;
            }
        }

        return rangeIndex == _drawnRanges.Count;
    }
}
