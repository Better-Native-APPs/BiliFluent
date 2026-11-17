// Copyright (c) BiliFluent. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.ComponentModel;

namespace BiliCopilot.UI.Controls.Comment;

/// <summary>
/// The poll of a comment: title, options to choose from and vote, or the results.
/// </summary>
public sealed partial class CommentVotePanel : UserControl
{
    /// <summary>
    /// Identifies the <see cref="ViewModel"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(CommentVoteViewModel), typeof(CommentVotePanel), new PropertyMetadata(default, OnViewModelChanged));

    private CommentVoteViewModel? _observed;
    private FocusState _submitFocus;
    private bool _isLoaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommentVotePanel"/> class.
    /// </summary>
    public CommentVotePanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Gets or sets the poll to show.
    /// </summary>
    public CommentVoteViewModel? ViewModel
    {
        get => (CommentVoteViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (CommentVotePanel)d;
        panel.Observe(panel._isLoaded ? e.NewValue as CommentVoteViewModel : null);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        Observe(ViewModel);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        Observe(null);
    }

    private void Observe(CommentVoteViewModel? viewModel)
    {
        if (!ReferenceEquals(_observed, viewModel))
        {
            if (_observed is not null)
            {
                _observed.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _observed = viewModel;
            if (_observed is not null)
            {
                _observed.PropertyChanged += OnViewModelPropertyChanged;
            }
        }

        UpdateOptionsFocus();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CommentVoteViewModel.Status):
                // Sending, the outcome and what can still be chosen are worth saying out loud.
                if (_observed is { HasStatus: true })
                {
                    Announce(StatusBlock);
                }

                break;
            case nameof(CommentVoteViewModel.Phase):
                if (_observed is { IsFailed: true })
                {
                    Announce(FailedBlock);
                }
                else if (_observed is { IsUnavailable: true })
                {
                    Announce(UnavailableBlock);
                }

                break;
            case nameof(CommentVoteViewModel.IsChoosing):
            case nameof(CommentVoteViewModel.IsSubmitting):
                UpdateOptionsFocus();
                break;
            default:
                break;
        }
    }

    // The list itself takes focus only when its rows cannot: while sending and when showing results.
    // That keeps the results reachable by keyboard, and gives focus a place to stay when the vote
    // button and the toggles go away after a vote.
    private void UpdateOptionsFocus()
    {
        var rowsTakeFocus = _observed is { IsChoosing: true, IsSubmitting: false };
        OptionsScroller.IsTabStop = !rowsTakeFocus;
        if (_submitFocus == FocusState.Unfocused)
        {
            return;
        }

        if (_observed is { IsSubmitting: true })
        {
            OptionsScroller.Focus(_submitFocus);
            return;
        }

        var focus = _submitFocus;
        _submitFocus = FocusState.Unfocused;
        if (rowsTakeFocus)
        {
            // The vote did not go through: back to the button, so Enter tries again.
            // Queued, because the button is enabled again only after this notification.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_observed is { IsChoosing: true, IsSubmitting: false } && SubmitButton.IsEnabled)
                {
                    SubmitButton.Focus(focus);
                }
            });
        }
    }

    // Queued: the text and its visibility are bound to properties that are still being notified.
    private void Announce(TextBlock text)
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (text.Visibility == Visibility.Visible)
            {
                FrameworkElementAutomationPeer.FromElement(text)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
        });

    private void OnSubmitClick(object sender, RoutedEventArgs e)
        => _submitFocus = SubmitButton.FocusState == FocusState.Unfocused ? FocusState.Programmatic : SubmitButton.FocusState;

    private void OnOptionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: CommentVoteOptionViewModel option } button)
        {
            return;
        }

        ViewModel?.ToggleOption(option);

        // The button has already flipped itself; show what the poll actually accepted.
        button.IsChecked = option.IsChecked;
    }
}
