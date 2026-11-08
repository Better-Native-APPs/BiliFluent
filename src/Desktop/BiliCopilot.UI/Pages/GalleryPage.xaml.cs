// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Richasy.BiliKernel.Models.Appearance;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Pages;

/// <summary>
/// 图片库页面.
/// </summary>
public sealed partial class GalleryPage : GalleryPageBase
{
    private XamlRoot? _galleryXamlRoot;

    /// <summary>
    /// Initializes a new instance of the <see cref="GalleryPage"/> class.
    /// </summary>
    public GalleryPage()
    {
        InitializeComponent();
    }

    /// <inheritdoc/>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is (BiliImage image, List<BiliImage> list))
        {
            ViewModel = new GalleryPageViewModel(image, list);
        }
    }

    /// <inheritdoc/>
    protected override void OnPageLoaded()
    {
        _galleryXamlRoot = XamlRoot;
        if (_galleryXamlRoot is not null)
        {
            _galleryXamlRoot.Changed += OnGalleryXamlRootChanged;
        }

        UpdateGalleryDecodePixelWidth();
        FactoryBlock.Text = 1.ToString("p00");
        ImageScrollView.ZoomTo(0.8f, default);
        ImageView.ScrollView.VerticalScrollMode = ScrollingScrollMode.Disabled;
        ImageView.ScrollView.HorizontalScrollMode = ScrollingScrollMode.Auto;
        ImageView.ScrollView.HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Auto;
        var index = ViewModel.Images.ToList().IndexOf(ViewModel.SelectedImage);
        ImageView.Select(index);
    }

    protected override void OnPageUnloaded()
    {
        if (_galleryXamlRoot is not null)
        {
            _galleryXamlRoot.Changed -= OnGalleryXamlRootChanged;
            _galleryXamlRoot = null;
        }

        ImageView.ItemsSource = default;
    }

    private BitmapImage? CreateGalleryBitmap(Uri? sourceUri)
        => sourceUri is null ? null : new BitmapImage
        {
            DecodePixelWidth = GetGalleryDecodePixelWidth(),
            UriSource = sourceUri,
        };

    private int GetGalleryDecodePixelWidth()
    {
        // Keep headroom for the 1.5x zoom without decoding an unrestricted full-size image.
        var viewportPixels = ImageScrollView.ActualWidth * (XamlRoot?.RasterizationScale ?? 1);
        return viewportPixels > 0 ? (int)Math.Min(2560, Math.Ceiling(viewportPixels * 2)) : 2560;
    }

    private void OnGalleryViewportSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateGalleryDecodePixelWidth();

    private void OnGalleryXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
        => UpdateGalleryDecodePixelWidth();

    private void UpdateGalleryDecodePixelWidth()
    {
        if (Image.Source is BitmapImage bitmap)
        {
            var width = GetGalleryDecodePixelWidth();
            if (bitmap.DecodePixelWidth != width)
            {
                bitmap.DecodePixelWidth = width;
            }
        }
    }

    private void OnScrollViewTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        => ViewModel.IsMenuHide = !ViewModel.IsMenuHide;

    private void CheckButtonStatus()
    {
        ZoomOutButton.IsEnabled = ImageScrollView.ZoomFactor > 0.2;
        ZoomInButton.IsEnabled = ImageScrollView.ZoomFactor < 1.5;
    }

    private void OnScrollViewChanged(ScrollView sender, object args)
    {
        FactoryBlock.Text = ImageScrollView.ZoomFactor.ToString("p00");
        CheckButtonStatus();
    }

    private void OnZoomInButtonClick(object sender, RoutedEventArgs e)
        => ImageScrollView.ZoomTo(ImageScrollView.ZoomFactor + 0.1f, default);

    private void OnZoomOutButtonClick(object sender, RoutedEventArgs e)
        => ImageScrollView.ZoomTo(ImageScrollView.ZoomFactor - 0.1f, default);

    private void OnSettingToBackgroundClickAsync(object sender, RoutedEventArgs e)
        => ViewModel.SetWallPaperOrLockScreenCommand.Execute(true);

    private void OnSettingToLockScreenClickAsync(object sender, RoutedEventArgs e)
        => ViewModel.SetWallPaperOrLockScreenCommand.Execute(false);

    private void OnImageSelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        var image = sender.SelectedItem as BiliImage;
        ViewModel.ChangeImageCommand.Execute(image);
    }
}

/// <summary>
/// 图片库页面基类.
/// </summary>
public abstract class GalleryPageBase : LayoutPageBase<GalleryPageViewModel>
{
}
