// Copyright (c) BiliFluent. All rights reserved.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Richasy.WinUIKernel.Share.Base;
using Windows.System;

namespace BiliCopilot.UI.Live.Controls;

/// <summary>What the composer wants the host to tell the user, and how serious it is.</summary>
public sealed record LiveComposerNotice(string Message, InfoType Type);

/// <summary>
/// The danmaku box of a live room. It edits the draft of the room connector, reports the input method
/// composition to it, and asks for one send on Enter or on the send button. While a send is on its way the
/// draft cannot be edited and nothing more is sent. The connector clears the draft once a send is accepted; a send
/// that fails keeps the draft and is reported through <see cref="Noticed"/>. Nothing sent is echoed here: the
/// message shows when the room delivers it.
/// </summary>
public sealed partial class LiveComposer : UserControl
{
    /// <summary><see cref="Connector"/> dependency property.</summary>
    public static readonly DependencyProperty ConnectorProperty =
        DependencyProperty.Register(nameof(Connector), typeof(LiveConnectorViewModel), typeof(LiveComposer), new PropertyMetadata(default(LiveConnectorViewModel), OnConnectorChanged));

    /// <summary><see cref="IsActive"/> dependency property.</summary>
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(LiveComposer), new PropertyMetadata(true, OnActiveChanged));

    /// <summary><see cref="SendButtonStyle"/> dependency property.</summary>
    public static readonly DependencyProperty SendButtonStyleProperty =
        DependencyProperty.Register(nameof(SendButtonStyle), typeof(Style), typeof(LiveComposer), new PropertyMetadata(default(Style), OnSendButtonStyleChanged));

    private LiveConnectorViewModel? _subscribed;
    private bool _loaded;
    private bool _synchronizing;
    private bool _composing;

    /// <summary>Initializes a new instance of the <see cref="LiveComposer"/> class.</summary>
    public LiveComposer()
    {
        InitializeComponent();
        var sendName = ResourceToolkit.GetLocalizedString(StringNames.SendDanmaku);
        AutomationProperties.SetName(DraftBox, sendName);
        AutomationProperties.SetName(SendButton, sendName);
        ToolTipService.SetToolTip(SendButton, sendName);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Synchronize();
    }

    /// <summary>A send did not go through, or cannot start. The host shows the message; the draft is kept.</summary>
    public event EventHandler<LiveComposerNotice>? Noticed;

    /// <summary>The connector of the room that plays. Null leaves the box empty and disabled.</summary>
    public LiveConnectorViewModel? Connector
    {
        get => (LiveConnectorViewModel?)GetValue(ConnectorProperty);
        set => SetValue(ConnectorProperty, value);
    }

    /// <summary>Whether the host bar takes input.</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>The button style of the host bar, for the send button.</summary>
    public Style? SendButtonStyle
    {
        get => (Style?)GetValue(SendButtonStyleProperty);
        set => SetValue(SendButtonStyleProperty, value);
    }

    private static void OnConnectorChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var composer = (LiveComposer)sender;
        composer.Subscribe(composer._loaded ? args.NewValue as LiveConnectorViewModel : null);
        composer.Synchronize();
    }

    private static void OnActiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var composer = (LiveComposer)sender;
        if (!(bool)args.NewValue) composer.EndComposition();
        composer.Synchronize();
    }

    private static void OnSendButtonStyleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is Style style) ((LiveComposer)sender).SendButton.Style = style;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        Subscribe(Connector);
        Synchronize();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Re-parenting can raise Loaded before the old Unloaded.
        if (!_loaded || IsLoaded) return;
        _loaded = false;
        Subscribe(null);
        Synchronize();
    }

    private void Subscribe(LiveConnectorViewModel? connector)
    {
        if (ReferenceEquals(_subscribed, connector)) return;

        // A composition belongs to the room it was typed in.
        EndComposition();
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnConnectorPropertyChanged;
        _subscribed = connector;
        if (connector is not null) connector.PropertyChanged += OnConnectorPropertyChanged;
    }

    private void OnConnectorPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _subscribed)) return;
        if (DispatcherQueue.HasThreadAccess) Synchronize();
        else DispatcherQueue.TryEnqueue(Synchronize);
    }

    private void Synchronize()
    {
        var connector = _subscribed;
        var usable = connector is not null && IsActive;
        DraftBox.IsEnabled = usable;

        // While the input method composes, the box is ahead of the draft; the draft follows on the next change.
        var draft = connector?.Draft ?? string.Empty;
        if (!_composing && !string.Equals(DraftBox.Text, draft, StringComparison.Ordinal))
        {
            _synchronizing = true;
            try { DraftBox.Text = draft; }
            finally { _synchronizing = false; }
        }

        var sending = connector?.IsSending == true;
        DraftBox.IsReadOnly = sending;
        var available = connector is null || (connector.IsActive && connector.Room.CanSend);
        var placeholder = ResourceToolkit.GetLocalizedString(available ? StringNames.LiveComposerPlaceholder : StringNames.LiveComposerUnavailable);
        if (DraftBox.PlaceholderText != placeholder) DraftBox.PlaceholderText = placeholder;
        SendButton.IsEnabled = usable && connector!.CanSend;
        SendingRing.IsActive = sending;
        SendingRing.Visibility = sending ? Visibility.Visible : Visibility.Collapsed;
        SendIcon.Visibility = sending ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnDraftTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_synchronizing || _subscribed is not { } connector) return;
        if (!string.Equals(connector.Draft, DraftBox.Text, StringComparison.Ordinal)) connector.Draft = DraftBox.Text;
    }

    private void OnCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
    {
        _composing = true;
        if (_subscribed is { } connector) connector.IsComposing = true;
    }

    private void OnCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
        => EndComposition();

    private void EndComposition()
    {
        if (!_composing) return;
        _composing = false;
        if (_subscribed is { } connector) connector.IsComposing = false;
    }

    private void OnDraftKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // One send per key press: a held key does not repeat it, and Enter inside a composition belongs to
        // the input method.
        if (e.Key != VirtualKey.Enter || _composing || e.KeyStatus.WasKeyDown) return;
        e.Handled = true;
        _ = SendAsync();
    }

    private void OnSendClick(object sender, RoutedEventArgs e) => _ = SendAsync();

    private async Task SendAsync()
    {
        if (_subscribed is not { IsSending: false } connector || !IsActive || _composing) return;
        var text = connector.Draft;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!connector.CanSend || connector.Room.Binding is not { } binding)
        {
            Notify(connector, ResourceToolkit.GetLocalizedString(StringNames.LiveSendUnavailable), InfoType.Warning);
            return;
        }

        LiveSendResult result;
        try
        {
            // The same single send as the connector's command, awaited here for its result.
            result = await connector.SendManuallyAsync(binding, text, connector.Room.CurrentScope?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Live danmaku send failed: {ex.Message}");
            return;
        }

        // A result of a room that is gone has nobody to tell.
        if (!connector.IsActive || connector.Room.Binding != binding) return;
        switch (result.Outcome)
        {
            case LiveSendOutcome.Accepted:
                break;
            case LiveSendOutcome.Rejected:
                Notify(
                    connector,
                    result.ApiCode is { } code
                        ? string.Format(CultureInfo.CurrentCulture, ResourceToolkit.GetLocalizedString(StringNames.LiveSendRejectedTemplate), code)
                        : ResourceToolkit.GetLocalizedString(StringNames.LiveSendRejected),
                    InfoType.Error);
                break;
            case LiveSendOutcome.Unknown:
                Notify(connector, ResourceToolkit.GetLocalizedString(StringNames.LiveSendUnknown), InfoType.Warning);
                break;
            default:
                Notify(connector, ResourceToolkit.GetLocalizedString(StringNames.LiveSendNotSent), InfoType.Error);
                break;
        }
    }

    private void Notify(LiveConnectorViewModel connector, string message, InfoType type)
    {
        if (_loaded && ReferenceEquals(_subscribed, connector)) Noticed?.Invoke(this, new LiveComposerNotice(message, type));
    }
}
