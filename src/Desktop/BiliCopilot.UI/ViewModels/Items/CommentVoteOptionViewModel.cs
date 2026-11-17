// Copyright (c) BiliFluent. All rights reserved.

using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;
using Symbol = FluentIcons.Common.Symbol;
using Richasy.BiliKernel.Models.Comment;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// One option of a poll in a comment: something to choose while voting is open, its result afterwards.
/// </summary>
public sealed partial class CommentVoteOptionViewModel : ObservableObject
{
    private readonly bool _isMultiChoice;

    internal CommentVoteOptionViewModel(CommentVoteOption option, bool isMultiChoice)
    {
        _isMultiChoice = isMultiChoice;
        Index = option.Index;
        Text = option.Text;
        Image = option.Image;
        Count = option.Count;
        Stats = string.Empty;
        ResultName = string.Empty;
    }

    /// <summary>Gets the index the server knows the option by.</summary>
    public int Index { get; }

    /// <summary>Gets the option text as sent by the server.</summary>
    public string Text { get; }

    /// <summary>Gets the picture of the option, for picture polls.</summary>
    public Uri? Image { get; }

    /// <summary>Gets a value indicating whether the option has a picture.</summary>
    public bool HasImage => Image is not null;

    /// <summary>Gets the mark in front of an option: a radio mark for one choice, a check box for several.</summary>
    public Symbol Glyph => _isMultiChoice
        ? (IsChecked ? Symbol.CheckboxChecked : Symbol.CheckboxUnchecked)
        : (IsChecked ? Symbol.RadioButton : Symbol.Circle);

    /// <summary>Gets the weight of <see cref="Glyph"/>: filled once the option is chosen.</summary>
    public IconVariant GlyphVariant => IsChecked ? IconVariant.Filled : IconVariant.Regular;

    /// <summary>Gets a value indicating whether the row shows the result of the option.</summary>
    public bool IsResult => !IsChoice;

    /// <summary>Gets or sets a value indicating whether the row is something to choose.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResult))]
    public partial bool IsChoice { get; set; }

    /// <summary>Gets or sets a value indicating whether the option can be chosen or given up right now.</summary>
    [ObservableProperty]
    public partial bool CanChoose { get; set; }

    /// <summary>Gets or sets a value indicating whether the user has picked the option for the vote being prepared.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph), nameof(GlyphVariant))]
    public partial bool IsChecked { get; set; }

    /// <summary>Gets or sets a value indicating whether this is an option the signed-in user voted for.</summary>
    [ObservableProperty]
    public partial bool IsMine { get; set; }

    /// <summary>Gets or sets the number of votes.</summary>
    [ObservableProperty]
    public partial long Count { get; set; }

    /// <summary>Gets or sets the share of all votes, 0-100.</summary>
    [ObservableProperty]
    public partial double Percent { get; set; }

    /// <summary>Gets or sets the vote count and share, ready to show.</summary>
    [ObservableProperty]
    public partial string Stats { get; set; }

    /// <summary>Gets or sets the sentence a screen reader says for the result of this option.</summary>
    [ObservableProperty]
    public partial string ResultName { get; set; }
}
