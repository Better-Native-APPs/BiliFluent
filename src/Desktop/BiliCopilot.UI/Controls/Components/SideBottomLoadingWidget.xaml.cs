// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI.Xaml.Media;

namespace BiliCopilot.UI.Controls.Components;

/// <summary>
/// 侧边底部加载控件.
/// </summary>
public sealed partial class SideBottomLoadingWidget : UserControl
{
    /// <summary>
    /// <see cref="Text"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(SideBottomLoadingWidget), new PropertyMetadata(default));

    private readonly List<(UIElement Element, long Token)> _visibilitySubscriptions = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="SideBottomLoadingWidget"/> class.
    /// </summary>
    public SideBottomLoadingWidget()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 文本.
    /// </summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Cached pages stay loaded when their ancestors are collapsed.
        for (DependencyObject? element = this; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is not UIElement visual)
            {
                continue;
            }

            _visibilitySubscriptions.Add((visual, visual.RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged)));
        }

        UpdateLoadingAnimation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        LoadingProgressBar.IsIndeterminate = false;
        foreach (var (element, token) in _visibilitySubscriptions)
        {
            element.UnregisterPropertyChangedCallback(VisibilityProperty, token);
        }

        _visibilitySubscriptions.Clear();
    }

    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty property)
        => UpdateLoadingAnimation();

    private void UpdateLoadingAnimation()
        => LoadingProgressBar.IsIndeterminate = IsLoaded
            && _visibilitySubscriptions.All(subscription => subscription.Element.Visibility == Visibility.Visible);
}
