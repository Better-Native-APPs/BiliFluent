// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Forms;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media.Imaging;
using Richasy.BiliKernel.Models.Appearance;
using Richasy.WinUIKernel.Share.Base;
using System.Globalization;

namespace BiliCopilot.UI.Controls.Components;

/// <summary>
/// 表情富文本.
/// </summary>
public sealed partial class EmoteTextBlock : LayoutUserControlBase
{
    /// <summary>
    /// <see cref="MaxLines"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty MaxLinesProperty =
        DependencyProperty.Register(nameof(MaxLines), typeof(int), typeof(EmoteTextBlock), new PropertyMetadata(4));

    /// <summary>
    /// <see cref="Text"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(EmoteText), typeof(EmoteTextBlock), new PropertyMetadata(default, new PropertyChangedCallback(OnTextChanged)));

    /// <summary>
    /// Initializes a new instance of the <see cref="EmoteTextBlock"/> class.
    /// </summary>
    public EmoteTextBlock() => InitializeComponent();

    /// <summary>
    /// 最大行数.
    /// </summary>
    public int MaxLines
    {
        get => (int)GetValue(MaxLinesProperty);
        set => SetValue(MaxLinesProperty, value);
    }

    /// <summary>
    /// 输入的文本.
    /// </summary>
    public EmoteText Text
    {
        get => (EmoteText)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnControlUnloaded()
        => Gallery?.ItemsSource = default;

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var instance = d as EmoteTextBlock;
        if (e.NewValue != null)
        {
            instance.InitializeContent();
        }

        instance.FlyoutRichBlock?.Blocks?.Clear();
    }

    private void InitializeContent()
    {
        if (RichBlock != null)
        {
            RichBlock.Blocks.Clear();
            Paragraph para = null;
            if (Text != null)
            {
                para = ParseText();
            }

            if (para != null)
            {
                RichBlock.Visibility = Visibility.Visible;
                RichBlock.Blocks.Add(para);
            }
            else
            {
                RichBlock.Visibility = Visibility.Collapsed;
            }

            CheckTextTrim();
        }

        if (Text.Pictures?.Count > 0)
        {
            var gallery = FindName("Gallery") as ItemsRepeater;
            gallery.ItemsSource = Text.Pictures;
            gallery.Visibility = Visibility.Visible;
        }
        else if (Gallery is not null)
        {
            Gallery.ItemsSource = default;
            Gallery.Visibility = Visibility.Collapsed;
        }
    }

    private void OnTextTrimChanged(RichTextBlock sender, IsTextTrimmedChangedEventArgs args)
        => CheckTextTrim();

    private Paragraph? ParseText()
    {
        if (string.IsNullOrEmpty(Text.Text) && Text.Links is not { Count: > 0 })
        {
            return default;
        }

        // Without link information (moments, chat messages) only emotes are picked out, as before.
        var segments = TextLinkParser.Parse(Text.Text, Text.Emotes, Text.Links);
        if (segments.Count == 0)
        {
            return default;
        }

        var para = new Paragraph();
        foreach (var segment in segments)
        {
            switch (segment.Kind)
            {
                case TextSegmentKind.Emote:
                    para.Inlines.Add(CreateInlineImage(Text.Emotes![segment.Text].Uri, 20, new Thickness(2, 0, 2, -4)));
                    break;
                case TextSegmentKind.Link:
                    AddLink(para, segment);
                    break;
                default:
                    para.Inlines.Add(new Run { Text = segment.Text });
                    break;
            }
        }

        return para;
    }

    private static InlineUIContainer CreateInlineImage(Uri uri, double width, Thickness margin)
        => new()
        {
            Child = new Image
            {
                Width = width,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = margin,
                Source = new BitmapImage(uri) { DecodePixelWidth = (int)(width * 2) },
            },
        };

    // The small picture the server sends with a link. The link next to it already says everything.
    private static InlineUIContainer CreateLinkIcon(Uri uri)
    {
        var icon = CreateInlineImage(uri, 16, new Thickness(2, 0, 3, -3));
        AutomationProperties.SetAccessibilityView(icon.Child, AccessibilityView.Raw);
        return icon;
    }

    private InlineUIContainer CreateInlineMark(string templateKey)
        => new() { Child = (UIElement)((DataTemplate)Resources[templateKey]).LoadContent() };

    private static string DescribeDestination(TextSegment segment)
    {
        var destination = segment.Destination!;
        if (destination.IsExternal)
        {
            // Say which site the browser will show: the words of a link need not match where it leads.
            var template = segment.Link?.Kind == TextLinkKind.Goods ? StringNames.TextLinkGoodsTemplate : StringNames.TextLinkBrowserTemplate;
            return string.Format(CultureInfo.CurrentCulture, ResourceToolkit.GetLocalizedString(template), destination.WebUri!.IdnHost);
        }

        return ResourceToolkit.GetLocalizedString(destination.Kind switch
        {
            TextLinkDestinationKind.User => StringNames.TextLinkUser,
            TextLinkDestinationKind.Search => segment.Link?.Kind == TextLinkKind.Topic ? StringNames.TextLinkTopic : StringNames.TextLinkSearch,
            TextLinkDestinationKind.Video => StringNames.TextLinkVideo,
            TextLinkDestinationKind.Live => StringNames.TextLinkLive,
            TextLinkDestinationKind.Article => StringNames.TextLinkArticle,
            _ => StringNames.TextLinkPgc,
        });
    }

    // A link is a run of the sentence: accent text that wraps with the words around it. The system
    // Hyperlink brings the hover and pressed colours, the hand pointer, the tab stop and Enter / Space.
    private void AddLink(Paragraph para, TextSegment segment)
    {
        var destination = segment.Destination!;
        var isNote = segment.IsAttached;
        var isGoods = segment.Link?.Kind == TextLinkKind.Goods;
        var shown = isNote ? ResourceToolkit.GetLocalizedString(StringNames.TextLinkNote) : TextLinkParser.GetDisplayText(segment);
        var full = isNote ? null : TextLinkParser.GetFullText(segment);
        var action = DescribeDestination(segment);

        if (isNote && para.Inlines.Count > 0)
        {
            para.Inlines.Add(new LineBreak());
        }

        // Pictures cannot sit inside a Hyperlink, so they go right next to it.
        var icon = isGoods
            ? CreateInlineMark("GoodsLinkMark")
            : segment.Link?.Icon?.Uri is { } iconUri ? CreateLinkIcon(iconUri) : null;
        var isIconTrailing = !isGoods && segment.Link?.IsIconTrailing == true;
        if (icon is not null && !isIconTrailing)
        {
            para.Inlines.Add(icon);
        }

        var link = new Hyperlink { UnderlineStyle = UnderlineStyle.None };
        link.Inlines.Add(new Run { Text = shown });
        AutomationProperties.SetName(link, string.Format(
            CultureInfo.CurrentCulture,
            ResourceToolkit.GetLocalizedString(StringNames.TextLinkNameTemplate),
            full ?? shown,
            action));
        ToolTipService.SetToolTip(link, full is null ? action : action + "\n" + TextLinkParser.Shorten(full, 200));

        // The name a user space or an article opens under until its own data arrives.
        var name = segment.Link?.Kind == TextLinkKind.Mention ? segment.Text.TrimStart('@') : segment.Link?.Title;
        link.Click += (_, _) =>
        {
            // The link may sit in the "show all" flyout, which would otherwise stay open over the new page.
            OverflowButton.Flyout?.Hide();
            _ = TextLinkNavigator.OpenAsync(destination, name);
        };
        para.Inlines.Add(link);

        if (icon is not null && isIconTrailing)
        {
            para.Inlines.Add(icon);
        }

        if (destination.IsExternal)
        {
            // Marks every link that leaves the app, so it does not read like a mention or a video.
            para.Inlines.Add(CreateInlineMark("ExternalLinkMark"));
        }
    }

    private void CheckTextTrim()
    {
        OverflowButton.Visibility = RichBlock.IsTextTrimmed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnImageTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        var image = (sender as FrameworkElement).DataContext as BiliImage;
        if (image is not null)
        {
            new GalleryWindow(image, [.. Text.Pictures]).Activate();
        }
    }

    private void OnOverflowFlyoutOpened(object sender, object e)
    {
        if (FlyoutRichBlock.Blocks.Count > 0)
        {
            return;
        }

        var para = ParseText();
        if (para != null)
        {
            FlyoutRichBlock.Blocks.Add(para);
        }
    }
}
