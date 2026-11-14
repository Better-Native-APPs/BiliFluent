// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace BiliCopilot.UI.Controls;

/// <summary>
/// Keeps a control invisible until the pointer is over its host or the control has keyboard focus.
/// </summary>
/// <remarks>
/// The host is the nearest ancestor marked with <see cref="IsHostProperty"/>, or the direct parent when no ancestor
/// is marked. Only the opacity changes: the control keeps its place in the layout, the tab order and the
/// automation tree, and it still takes clicks while it is invisible.
/// </remarks>
public static class HoverReveal
{
    /// <summary>
    /// Whether the control is revealed on demand.
    /// </summary>
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(HoverReveal), new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>
    /// Marks the element whose area reveals the controls inside it.
    /// </summary>
    public static readonly DependencyProperty IsHostProperty =
        DependencyProperty.RegisterAttached("IsHost", typeof(bool), typeof(HoverReveal), new PropertyMetadata(false));

    private static readonly DependencyProperty SubscriptionProperty =
        DependencyProperty.RegisterAttached("Subscription", typeof(object), typeof(HoverReveal), new PropertyMetadata(default));

    /// <summary>
    /// Gets whether the control is revealed on demand.
    /// </summary>
    /// <returns>Whether it is.</returns>
    public static bool GetIsEnabled(Control control) => (bool)control.GetValue(IsEnabledProperty);

    /// <summary>
    /// Sets whether the control is revealed on demand.
    /// </summary>
    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);

    /// <summary>
    /// Gets whether the element is a reveal host.
    /// </summary>
    /// <returns>Whether it is.</returns>
    public static bool GetIsHost(FrameworkElement element) => (bool)element.GetValue(IsHostProperty);

    /// <summary>
    /// Sets whether the element is a reveal host.
    /// </summary>
    public static void SetIsHost(FrameworkElement element, bool value) => element.SetValue(IsHostProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control control)
        {
            return;
        }

        (control.GetValue(SubscriptionProperty) as Subscription)?.Dispose();
        control.ClearValue(SubscriptionProperty);
        if (e.NewValue is true)
        {
            control.SetValue(SubscriptionProperty, new Subscription(control));
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Control _target;
        private readonly PointerEventHandler _entered;
        private readonly PointerEventHandler _exited;
        private FrameworkElement? _host;
        private bool _isPointerOver;

        public Subscription(Control target)
        {
            _target = target;
            _entered = OnHostPointerEntered;
            _exited = OnHostPointerExited;

            // Hide first, then add the transition, so the control does not fade out when it first appears.
            _target.Opacity = 0;
            _target.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(167) };
            _target.Loaded += OnLoaded;
            _target.Unloaded += OnUnloaded;
            _target.GotFocus += OnFocusChanged;
            _target.LostFocus += OnFocusChanged;
            if (_target.IsLoaded)
            {
                Attach();
            }
        }

        public void Dispose()
        {
            _target.Loaded -= OnLoaded;
            _target.Unloaded -= OnUnloaded;
            _target.GotFocus -= OnFocusChanged;
            _target.LostFocus -= OnFocusChanged;
            Detach();
            _target.OpacityTransition = null;
            _target.Opacity = 1;
        }

        private void OnLoaded(object sender, RoutedEventArgs e) => Attach();

        private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

        private void OnFocusChanged(object sender, RoutedEventArgs e) => Update();

        private void OnHostPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOver = true;
            Update();
        }

        private void OnHostPointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOver = false;
            Update();
        }

        private void Attach()
        {
            if (_host is not null || FindHost() is not FrameworkElement host)
            {
                return;
            }

            _host = host;
            _host.AddHandler(UIElement.PointerEnteredEvent, _entered, true);
            _host.AddHandler(UIElement.PointerExitedEvent, _exited, true);
            Update();
        }

        private void Detach()
        {
            if (_host is null)
            {
                return;
            }

            _host.RemoveHandler(UIElement.PointerEnteredEvent, _entered);
            _host.RemoveHandler(UIElement.PointerExitedEvent, _exited);
            _host = null;

            // No exit event arrives for a host that left the tree with the pointer still over it.
            _isPointerOver = false;
            Update();
        }

        private FrameworkElement? FindHost()
        {
            var parent = VisualTreeHelper.GetParent(_target) as FrameworkElement;
            for (var node = parent; node is not null; node = VisualTreeHelper.GetParent(node) as FrameworkElement)
            {
                if (GetIsHost(node))
                {
                    return node;
                }
            }

            return parent;
        }

        private void Update()
            => _target.Opacity = _isPointerOver || _target.FocusState == FocusState.Keyboard ? 1 : 0;
    }
}
