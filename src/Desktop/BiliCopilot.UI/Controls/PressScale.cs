// Copyright (c) Bili Copilot. All rights reserved.

using System.Numerics;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace BiliCopilot.UI.Controls;

/// <summary>
/// Shrinks a button slightly while the pointer is over it and further while it is pressed.
/// </summary>
/// <remarks>
/// For buttons whose content covers the whole button, such as avatars, where the stock hover and pressed fills
/// cannot be seen. The button keeps its style and template; only its scale changes.
/// </remarks>
public static class PressScale
{
    /// <summary>
    /// Whether the button shrinks on hover and press.
    /// </summary>
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(PressScale), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty SubscriptionProperty =
        DependencyProperty.RegisterAttached("Subscription", typeof(object), typeof(PressScale), new PropertyMetadata(default));

    /// <summary>
    /// Gets whether the button shrinks on hover and press.
    /// </summary>
    /// <returns>Whether it does.</returns>
    public static bool GetIsEnabled(ButtonBase button) => (bool)button.GetValue(IsEnabledProperty);

    /// <summary>
    /// Sets whether the button shrinks on hover and press.
    /// </summary>
    public static void SetIsEnabled(ButtonBase button, bool value) => button.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button)
        {
            return;
        }

        (button.GetValue(SubscriptionProperty) as Subscription)?.Dispose();
        button.ClearValue(SubscriptionProperty);
        if (e.NewValue is true)
        {
            button.SetValue(SubscriptionProperty, new Subscription(button));
        }
    }

    private sealed class Subscription : IDisposable
    {
        private const float PointerOverScale = 0.96f;
        private const float PressedScale = 0.86f;

        private readonly ButtonBase _button;
        private readonly long _pressedToken;
        private readonly long _pointerOverToken;

        public Subscription(ButtonBase button)
        {
            _button = button;
            _button.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(240) };
            _pressedToken = _button.RegisterPropertyChangedCallback(ButtonBase.IsPressedProperty, OnStateChanged);
            _pointerOverToken = _button.RegisterPropertyChangedCallback(ButtonBase.IsPointerOverProperty, OnStateChanged);
            _button.SizeChanged += OnSizeChanged;
            UpdateCenter();
            Update();
        }

        public void Dispose()
        {
            _button.UnregisterPropertyChangedCallback(ButtonBase.IsPressedProperty, _pressedToken);
            _button.UnregisterPropertyChangedCallback(ButtonBase.IsPointerOverProperty, _pointerOverToken);
            _button.SizeChanged -= OnSizeChanged;
            _button.ScaleTransition = null;
            _button.Scale = Vector3.One;
        }

        private void OnStateChanged(DependencyObject sender, DependencyProperty dp) => Update();

        private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateCenter();

        private void UpdateCenter()
            => _button.CenterPoint = new Vector3((float)(_button.ActualWidth / 2), (float)(_button.ActualHeight / 2), 0);

        private void Update()
        {
            var scale = _button.IsPressed ? PressedScale : _button.IsPointerOver ? PointerOverScale : 1f;
            _button.Scale = new Vector3(scale, scale, 1);
        }
    }
}
