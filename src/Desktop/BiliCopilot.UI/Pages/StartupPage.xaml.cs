// Copyright (c) Bili Copilot. All rights reserved.

using System.ComponentModel;
using BiliCopilot.UI.Forms;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Input;
using Richasy.BiliKernel.Models.Authorization;
using Richasy.WinUIKernel.Share.Base;
using Windows.System;

namespace BiliCopilot.UI.Pages;

/// <summary>
/// 初始登录页面.
/// </summary>
public sealed partial class StartupPage : StartupPageBase
{
    private const string NoticeActivity = "SignInNotice";

    // The form at its full width (344) with a 32 gutter on each side, the divider and the QR column (300).
    private const double TwoColumnMinWidth = 712;

    // Title bar zone (32), space above (40), the content at its tallest resting height (348), space below (16)
    // and the footer (69), rounded up.
    private const double RegularMinHeight = 506;

    // In the single-column layout the QR code takes the place of the form while this is set.
    private bool _isQRViewOpen;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartupPage"/> class.
    /// </summary>
    public StartupPage()
    {
        InitializeComponent();
        SizeChanged += OnPageSizeChanged;
    }

    /// <inheritdoc/>
    protected override void OnPageLoaded()
    {
        DetachViewModel();
        ViewModel.CaptchaPresenter = ShowCaptchaAsync;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.WebSignInRequested += OnWebSignInRequested;
        ViewModel.SignInFocusRequested += OnSignInFocusRequested;
        ViewModel.InitializeCommand.Execute(QRCodeImage);
        AccountBox.Focus(FocusState.Programmatic);
    }

    /// <inheritdoc/>
    protected override void OnPageUnloaded()
    {
        DetachViewModel();

        // Nothing typed here outlives the page.
        ViewModel.ResetSignInForm();
    }

    private static bool IsEnter(KeyRoutedEventArgs e)
        => e.Key == VirtualKey.Enter && !e.KeyStatus.WasKeyDown;

    // Tell a screen reader about text that changes without the focus moving to it. The source only has to be
    // an element that is always on screen; the text that changed may not be visible yet.
    private static void Announce(UIElement source, string text, bool isImportant)
    {
        if (string.IsNullOrEmpty(text) || FrameworkElementAutomationPeer.FromElement(source) is not { } peer)
        {
            return;
        }

        peer.RaiseNotificationEvent(
            isImportant ? AutomationNotificationKind.ActionAborted : AutomationNotificationKind.Other,
            isImportant ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent,
            text,
            NoticeActivity);
    }

    private void DetachViewModel()
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.WebSignInRequested -= OnWebSignInRequested;
        ViewModel.SignInFocusRequested -= OnSignInFocusRequested;
        ViewModel.CaptchaPresenter = null;
    }

    private Task<BiliLoginCaptchaResult?> ShowCaptchaAsync(BiliLoginCaptchaParameters parameters, CancellationToken cancellationToken)
        => CaptchaWindow.RequestAsync(
            this.Get<AppViewModel>().Windows.FirstOrDefault(p => p is StartupWindow),
            parameters,
            cancellationToken);

    private void ShowWebSignIn()
    {
        var window = this.Get<AppViewModel>().Windows.FirstOrDefault(p => p is WebSignInWindow);
        if (window is not null)
        {
            window.Activate();
        }
        else
        {
            new WebSignInWindow().Activate();
        }
    }

    // Two columns while both fit at full size; below that the form stays and the QR code moves behind a button.
    private void UpdateLayoutState()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var isNarrow = ActualWidth < TwoColumnMinWidth;
        if (!isNarrow)
        {
            _isQRViewOpen = false;
        }

        var columnState = !isNarrow
            ? nameof(WideState)
            : _isQRViewOpen ? nameof(NarrowQRState) : nameof(NarrowFormState);
        VisualStateManager.GoToState(this, columnState, false);
        VisualStateManager.GoToState(this, ActualHeight < RegularMinHeight ? nameof(ShortState) : nameof(RegularState), false);
    }

    private void SetQRViewOpen(bool isOpen)
    {
        if (_isQRViewOpen == isOpen)
        {
            return;
        }

        _isQRViewOpen = isOpen;
        UpdateLayoutState();
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateLayoutState();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The message bar announces itself when it opens; these two have no such behavior.
        if (e.PropertyName == nameof(ViewModel.SignInStatus))
        {
            Announce(AppNameBlock, ViewModel.SignInStatus, isImportant: false);
        }
        else if (e.PropertyName == nameof(ViewModel.IsSignInMessageOpen) && ViewModel.IsSignInMessageOpen)
        {
            // A long message can push the end of the form below the fold. The links come right after the message,
            // so showing them shows all of it; nothing scrolls when they are already in view.
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => SignInLinksPanel.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true }));
        }
        else if (e.PropertyName == nameof(ViewModel.ErrorTip) && !string.IsNullOrEmpty(ViewModel.ErrorTip))
        {
            Announce(AppNameBlock, QRTipBlock.Text, isImportant: true);
        }
    }

    private void OnWebSignInRequested(object? sender, EventArgs e)
        => ShowWebSignIn();

    private void OnSignInFocusRequested(object? sender, StartupSignInField field)
    {
        Control target = field switch
        {
            StartupSignInField.Account => AccountBox,
            StartupSignInField.Password => PasswordInput,
            StartupSignInField.PhoneNumber => PhoneBox,
            StartupSignInField.SmsCode => SmsCodeBox,
            StartupSignInField.VerificationSend => SendVerificationCodeButton,
            _ => VerificationCodeBox,
        };

        // The form has to be on screen for any of these to take the focus.
        SetQRViewOpen(false);

        // The request often arrives while the form is still disabled or switching steps; wait for that to settle.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => target.Focus(FocusState.Programmatic));
    }

    private void OnModeSelectorSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        => ViewModel.IsSmsMode = sender.SelectedItem is not null && ReferenceEquals(sender.SelectedItem, SmsModeItem);

    private void OnCountryBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The list is filled after the box exists, which briefly reports "nothing selected"; ignore that.
        if (CountryBox.SelectedItem is StartupCountryItem country)
        {
            ViewModel.SelectedCountry = country;
        }
    }

    private void OnCountryBoxDropDownOpened(object sender, object e)
        => ViewModel.LoadCountriesCommand.Execute(default);

    private void OnAccountBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsEnter(e))
        {
            return;
        }

        e.Handled = true;
        if (string.IsNullOrEmpty(PasswordInput.Password))
        {
            PasswordInput.Focus(FocusState.Keyboard);
        }
        else
        {
            ViewModel.SignInWithPasswordCommand.Execute(default);
        }
    }

    private void OnPasswordInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsEnter(e))
        {
            e.Handled = true;
            ViewModel.SignInWithPasswordCommand.Execute(default);
        }
    }

    private void OnPhoneBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsEnter(e))
        {
            return;
        }

        // Without a code yet, the step at hand is asking for one (the challenge window comes first).
        e.Handled = true;
        if (string.IsNullOrWhiteSpace(SmsCodeBox.Text))
        {
            if (ViewModel.CanSendSmsCode)
            {
                ViewModel.SendSmsCodeCommand.Execute(default);
            }
            else
            {
                SmsCodeBox.Focus(FocusState.Keyboard);
            }
        }
        else
        {
            ViewModel.SignInWithSmsCommand.Execute(default);
        }
    }

    private void OnSmsCodeBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsEnter(e))
        {
            e.Handled = true;
            ViewModel.SignInWithSmsCommand.Execute(default);
        }
    }

    private void OnVerificationCodeBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsEnter(e))
        {
            e.Handled = true;
            ViewModel.CompleteVerificationCommand.Execute(default);
        }
    }

    private void OnPasswordSignInButtonClick(object sender, RoutedEventArgs e)
        => ViewModel.SignInWithPasswordCommand.Execute(default);

    private void OnSmsSignInButtonClick(object sender, RoutedEventArgs e)
        => ViewModel.SignInWithSmsCommand.Execute(default);

    private void OnSendSmsCodeButtonClick(object sender, RoutedEventArgs e)
        => ViewModel.SendSmsCodeCommand.Execute(default);

    private void OnSendVerificationCodeButtonClick(object sender, RoutedEventArgs e)
        => ViewModel.SendVerificationCodeCommand.Execute(default);

    private void OnVerificationSubmitButtonClick(object sender, RoutedEventArgs e)
        => ViewModel.CompleteVerificationCommand.Execute(default);

    private void OnCancelSignInButtonClick(object sender, RoutedEventArgs e)
        => ViewModel.CancelSignInCommand.Execute(default);

    private void OnLeaveVerificationButtonClick(object sender, RoutedEventArgs e)
        => ViewModel.LeaveVerificationCommand.Execute(default);

    private void OnSignInMessageActionClick(object sender, RoutedEventArgs e)
        => ViewModel.RunSignInMessageActionCommand.Execute(default);

    private void OnWebSignInButtonClick(object sender, RoutedEventArgs e)
        => ShowWebSignIn();

    private async void OnRepoButtonClickAsync(object sender, RoutedEventArgs e)
        => await Launcher.LaunchUriAsync(new Uri("https://github.com/Better-Native-APPs/BiliFluent"));

    private async void OnBiliButtonClickAsync(object sender, RoutedEventArgs e)
        => await Launcher.LaunchUriAsync(new Uri("https://space.bilibili.com/5992670"));

    private void OnRefreshQRButtonClick(object sender, RoutedEventArgs e)
        => ViewModel.ReloadQRCodeCommand.Execute(default);

    private void OnQREntryButtonClick(object sender, RoutedEventArgs e)
    {
        SetQRViewOpen(true);

        // The button that had the focus is gone now; hand the focus to its counterpart once that one is laid out.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => QRBackButton.Focus(FocusState.Programmatic));
    }

    private void OnQRBackButtonClick(object sender, RoutedEventArgs e)
    {
        SetQRViewOpen(false);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => QREntryButton.Focus(FocusState.Programmatic));
    }
}

/// <summary>
/// 初始登录页面基类.
/// </summary>
public abstract class StartupPageBase : LayoutPageBase<StartupPageViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StartupPageBase"/> class.
    /// </summary>
    public StartupPageBase() => ViewModel = this.Get<StartupPageViewModel>();
}
