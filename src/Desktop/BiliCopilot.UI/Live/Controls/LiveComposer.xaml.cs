// Copyright (c) BiliFluent. All rights reserved.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Richasy.WinUIKernel.Share.Base;
using Windows.System;

namespace BiliCopilot.UI.Live.Controls;

/// <summary>What the composer wants the host to tell the user, and how serious it is.</summary>
public sealed record LiveComposerNotice(string Message, InfoType Type);

/// <summary>
/// The danmaku box of a live room, in the form of the on-demand danmaku box. It edits the draft of the room
/// connector, reports the input method composition to it, and asks for one send on Enter or on the send icon.
/// While a send is on its way the box takes no input, as the on-demand box does. The connector clears the draft
/// once a send is accepted; a send that fails keeps the draft and is reported through <see cref="Noticed"/>.
/// Nothing sent is echoed here: the message shows when the room delivers it.
/// </summary>
public sealed partial class LiveComposer : UserControl
{
    /// <summary><see cref="Connector"/> dependency property.</summary>
    public static readonly DependencyProperty ConnectorProperty =
        DependencyProperty.Register(nameof(Connector), typeof(LiveConnectorViewModel), typeof(LiveComposer), new PropertyMetadata(default(LiveConnectorViewModel), OnConnectorChanged));

    /// <summary><see cref="IsActive"/> dependency property.</summary>
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(LiveComposer), new PropertyMetadata(true, OnActiveChanged));

    private LiveConnectorViewModel? _subscribed;
    private TextBox? _textBox;
    private bool _loaded;
    private bool _composing;
    private bool _enterBelongsElsewhere;

    /// <summary>Initializes a new instance of the <see cref="LiveComposer"/> class.</summary>
    public LiveComposer()
    {
        InitializeComponent();
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

    private static TextBox? FindTextBox(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox box) return box;
            if (FindTextBox(child) is { } found) return found;
        }

        return null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        HookTextBox();
        Subscribe(Connector);
        Synchronize();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Re-parenting can raise Loaded before the old Unloaded.
        if (!_loaded || IsLoaded) return;
        _loaded = false;
        UnhookTextBox();
        Subscribe(null);
        Synchronize();
    }

    // The composition of an input method is reported by the text box inside the box, once its template exists.
    private void HookTextBox()
    {
        if (_textBox is not null) return;
        DraftBox.ApplyTemplate();
        if (FindTextBox(DraftBox) is not { } box) return;
        _textBox = box;
        box.TextChanging += OnDraftTextChanging;
        box.TextCompositionStarted += OnCompositionStarted;
        box.TextCompositionEnded += OnCompositionEnded;
    }

    private void UnhookTextBox()
    {
        if (_textBox is not { } box) return;
        _textBox = null;
        box.TextChanging -= OnDraftTextChanging;
        box.TextCompositionStarted -= OnCompositionStarted;
        box.TextCompositionEnded -= OnCompositionEnded;
        EndComposition();
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

        // While the input method composes, the box is ahead of the draft; the draft follows on the next change.
        var draft = connector?.Draft ?? string.Empty;
        if (!_composing && !string.Equals(_textBox?.Text ?? DraftBox.Text, draft, StringComparison.Ordinal))
        {
            DraftBox.Text = draft;
            if (_textBox is { } box && !string.Equals(box.Text, draft, StringComparison.Ordinal)) box.Text = draft;
        }

        // A send on its way: the box takes no input until the room answers.
        DraftBox.IsEnabled = connector is not null && IsActive && !connector.IsSending;
        var available = connector is null || (connector.IsActive && connector.Room.CanSend);
        var placeholder = ResourceToolkit.GetLocalizedString(available ? StringNames.DanmakuPlaceholder : StringNames.LiveComposerUnavailable);
        if (DraftBox.PlaceholderText != placeholder) DraftBox.PlaceholderText = placeholder;
    }

    // The text box reports a change as it happens, so the draft is never behind what is on screen. Whoever made
    // the change, a text that equals the draft changes nothing.
    private void OnDraftTextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
        => UpdateDraft(sender.Text);

    // The box reports the same change again a moment later; it matters only if the text box was not found.
    private void OnDraftTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_textBox is null) UpdateDraft(sender.Text);
    }

    private void UpdateDraft(string text)
    {
        if (_subscribed is not { } connector) return;
        if (!string.Equals(connector.Draft, text, StringComparison.Ordinal)) connector.Draft = text;
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

    private void OnDraftPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;

        // One send per key press: a held key does not repeat it, and Enter inside a composition belongs to the
        // input method. The box may still report a submit for this key; it is dropped. The mark lasts for this
        // key event only, so the send icon is never held back by it.
        _enterBelongsElsewhere = _composing || e.KeyStatus.WasKeyDown;
        if (_enterBelongsElsewhere) DispatcherQueue.TryEnqueue(() => _enterBelongsElsewhere = false);
    }

    private void OnDraftSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (_enterBelongsElsewhere || _composing) return;
        _ = SendAsync();
    }

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

        // The box loses the keyboard while it takes no input; it gets it back for the next line.
        var hadFocus = _textBox is { FocusState: not FocusState.Unfocused };
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
        if (!_loaded || !ReferenceEquals(_subscribed, connector) || !connector.IsActive || connector.Room.Binding != binding) return;
        Synchronize();
        if (hadFocus && DraftBox.IsEnabled) DraftBox.Focus(FocusState.Programmatic);
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
