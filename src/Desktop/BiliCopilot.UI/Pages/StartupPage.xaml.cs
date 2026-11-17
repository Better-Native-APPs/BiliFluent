// Copyright (c) Bili Copilot. All rights reserved.

using System.ComponentModel;
using BiliCopilot.UI.Forms;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

    // Title bar zone (32), space above (40), the content at its tallest resting height (364), space below (16)
    // and the footer (69), rounded up.
    private const double RegularMinHeight = 522;

    // Digits typed on the country list within this time of each other are read as one dialing code.
    private const long CountrySearchTimeout = 1000;

    // In the single-column layout the QR code takes the place of the form while this is set.
    private bool _isQRViewOpen;

    private string _countrySearch = string.Empty;
    private long _countrySearchTime;

    // The field an input method is putting text together in. Its text is left alone until that is done.
    private TextBox? _composingBox;

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

    // Full-width digits are what an input method in full-width mode types; they mean the plain ones.
    private static char ToDigit(char ch)
        => ch switch
        {
            >= '0' and <= '9' => ch,
            >= '\uFF10' and <= '\uFF19' => (char)(ch - '\uFF10' + '0'),
            _ => '\0',
        };

    // Runs on every change of the text, typed, pasted or dropped, before it is shown. Whatever does not belong
    // in the box is taken out again, which leaves the caret where the user had it.
    private static void RestrictText(TextBox box, Func<char, char> convert, int maxLength, string? prefix = null)
    {
        var text = box.Text;
        var caret = Math.Min(box.SelectionStart, text.Length);
        var kept = new System.Text.StringBuilder(text.Length);
        var keptCaret = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (i == caret)
            {
                keptCaret = kept.Length;
            }

            var ch = convert(text[i]);
            if (ch != '\0')
            {
                kept.Append(ch);
            }
        }

        if (caret == text.Length)
        {
            keptCaret = kept.Length;
        }

        var result = kept.ToString();

        // A number copied in its international form starts with the code that the region box already shows.
        if (prefix is { Length: > 0 } && text.TrimStart().StartsWith('+') && result.StartsWith(prefix, StringComparison.Ordinal))
        {
            result = result[prefix.Length..];
            keptCaret = Math.Max(0, keptCaret - prefix.Length);
        }

        if (result.Length > maxLength)
        {
            // What was just put in ends at the caret. Too much of it goes from there, so what the box held
            // before stays as it was: a full box takes nothing more, and a long paste loses its end.
            var cut = Math.Min(result.Length - maxLength, keptCaret);
            result = result.Remove(keptCaret - cut, cut);
            keptCaret -= cut;
            if (result.Length > maxLength)
            {
                result = result[..maxLength];
            }
        }

        if (!string.Equals(result, text, StringComparison.Ordinal))
        {
            box.Text = result;
            box.SelectionStart = keptCaret;
        }
    }

    private void DetachViewModel()
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.WebSignInRequested -= OnWebSignInRequested;
        ViewModel.SignInFocusRequested -= OnSignInFocusRequested;
        ViewModel.CaptchaPresenter = null;
    }

    private Task<BiliLoginCaptchaResult?> ShowCaptchaAsync(BiliLoginCaptchaParameters parameters, Func<Task<BiliLoginCaptchaParameters?>> refreshParameters, CancellationToken cancellationToken)
        => CaptchaWindow.RequestAsync(
            this.Get<AppViewModel>().Windows.FirstOrDefault(p => p is StartupWindow),
            parameters,
            refreshParameters,
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
        else
        {
            // What is wrong with a field appears under it without the focus going there.
            var fieldError = e.PropertyName switch
            {
                nameof(ViewModel.AccountError) => ViewModel.AccountError,
                nameof(ViewModel.PasswordError) => ViewModel.PasswordError,
                nameof(ViewModel.PhoneNumberError) => ViewModel.PhoneNumberError,
                nameof(ViewModel.SmsCodeError) => ViewModel.SmsCodeError,
                nameof(ViewModel.VerificationCodeError) => ViewModel.VerificationCodeError,
                _ => string.Empty,
            };
            Announce(AppNameBlock, fieldError, isImportant: true);
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

    // The box shows the same template for its closed state and for the entries of its list. Only an entry of
    // the list sits inside a ComboBoxItem, which is how the two are told apart.
    private void OnCountryEntryLoaded(object sender, RoutedEventArgs e)
    {
        var entry = (FrameworkElement)sender;
        ComboBoxItem? container = null;
        for (var parent = VisualTreeHelper.GetParent(entry); parent is not null && container is null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (ReferenceEquals(parent, CountryBox))
            {
                break;
            }

            container = parent as ComboBoxItem;
        }

        var isInList = container is not null;
        if (entry.FindName("CountryEntryDetail") is UIElement detail)
        {
            detail.Visibility = isInList ? Visibility.Visible : Visibility.Collapsed;
        }

        if (entry.FindName("CountryEntryCode") is UIElement code)
        {
            code.Visibility = isInList ? Visibility.Collapsed : Visibility.Visible;
        }

        // While the list is open the keys go to its entries, and from there they do not reach the box.
        if (container is not null)
        {
            container.CharacterReceived -= OnCountryCharacterReceived;
            container.CharacterReceived += OnCountryCharacterReceived;
        }
    }

    // Typing the digits of a dialing code selects the first entry that has it. The box's own search goes by
    // the text a screen reader is given, which starts with the name, so it cannot do this.
    private void OnCountryCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (!char.IsAsciiDigit(args.Character))
        {
            return;
        }

        args.Handled = true;
        var now = Environment.TickCount64;
        var digit = args.Character.ToString();
        var search = now - _countrySearchTime > CountrySearchTimeout ? digit : _countrySearch + digit;
        var match = FindCountry(search);
        if (match is null && search.Length > 1)
        {
            // Not the next digit of the same code, so it starts another one.
            search = digit;
            match = FindCountry(search);
        }

        _countrySearch = search;
        _countrySearchTime = now;
        if (match is not null)
        {
            CountryBox.SelectedItem = match;
        }
    }

    private StartupCountryItem? FindCountry(string dialingDigits)
        => ViewModel.Countries.FirstOrDefault(p => p.DialingDigits.StartsWith(dialingDigits, StringComparison.Ordinal));

    // Keep a field to what it can hold: no spaces in an account (the official page takes them out as well),
    // digits only in a phone number and in a code.
    private void RestrictField(TextBox box)
    {
        // Changing the text under an input method breaks what it is putting together.
        if (ReferenceEquals(box, _composingBox) && box.FocusState != FocusState.Unfocused)
        {
            return;
        }

        if (ReferenceEquals(box, AccountBox))
        {
            RestrictText(box, ch => char.IsWhiteSpace(ch) ? '\0' : ch, box.MaxLength);
        }
        else if (ReferenceEquals(box, PhoneBox))
        {
            RestrictText(box, ToDigit, StartupPageViewModel.PhoneNumberMaxLength, ViewModel.SelectedCountry?.DialingDigits);
        }
        else
        {
            RestrictText(box, ToDigit, StartupPageViewModel.SmsCodeMaxLength);
        }
    }

    private void OnFieldTextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
        => RestrictField(sender);

    private void OnFieldCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
        => _composingBox = sender;

    private void OnFieldCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
    {
        _composingBox = null;

        // What was put together may only reach the text after this.
        DispatcherQueue.TryEnqueue(() => RestrictField(sender));
    }

    private void OnAccountBoxLostFocus(object sender, RoutedEventArgs e)
        => ViewModel.CheckFieldOnLeave(StartupSignInField.Account);

    private void OnPhoneBoxLostFocus(object sender, RoutedEventArgs e)
        => ViewModel.CheckFieldOnLeave(StartupSignInField.PhoneNumber);

    private void OnSmsCodeBoxLostFocus(object sender, RoutedEventArgs e)
        => ViewModel.CheckFieldOnLeave(StartupSignInField.SmsCode);

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
