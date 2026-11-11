// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Controls;

public sealed partial class BasicCoverImage : UserControl
{
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(Uri), typeof(BasicCoverImage), new PropertyMetadata(null, OnImagePropertyChanged));

    public static readonly DependencyProperty DecodeWidthProperty =
        DependencyProperty.Register(nameof(DecodeWidth), typeof(double), typeof(BasicCoverImage), new PropertyMetadata(1d, OnImagePropertyChanged));

    public static readonly DependencyProperty IsShimmerEnabledProperty =
        DependencyProperty.Register(nameof(IsShimmerEnabled), typeof(bool), typeof(BasicCoverImage), new PropertyMetadata(true, OnLoadingPropertyChanged));

    public static readonly DependencyProperty IsImageLoadingProperty =
        DependencyProperty.Register(nameof(IsImageLoading), typeof(bool), typeof(BasicCoverImage), new PropertyMetadata(false, OnLoadingPropertyChanged));

    private readonly Image _image = new() { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
    private readonly Shimmer _shimmer = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Border _border = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private CancellationTokenSource? _loadCancellation;
    private Uri? _loadedSource;
    private int _loadedDecodeWidth;

    public BasicCoverImage()
    {
        IsTabStop = false;
        var content = new Grid();
        content.Children.Add(_image);
        content.Children.Add(_shimmer);
        _border.Child = content;
        Content = _border;
        RegisterPropertyChangedCallback(CornerRadiusProperty, (_, _) => _border.CornerRadius = CornerRadius);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _image.ImageOpened += OnImageOpened;
        _image.ImageFailed += OnImageFailed;
    }

    public Uri? Source
    {
        get => (Uri?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double DecodeWidth
    {
        get => (double)GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    public bool IsShimmerEnabled
    {
        get => (bool)GetValue(IsShimmerEnabledProperty);
        set => SetValue(IsShimmerEnabledProperty, value);
    }

    public bool IsImageLoading
    {
        get => (bool)GetValue(IsImageLoadingProperty);
        private set => SetValue(IsImageLoadingProperty, value);
    }

    private static void OnImagePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (BasicCoverImage)d;
        control.CancelLoading();
        control._image.Source = null;
        control._loadedSource = null;
        control.StartLoading();
    }

    private static void OnLoadingPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (BasicCoverImage)d;
        control._shimmer.Visibility = control.IsImageLoading && control.IsShimmerEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
        => StartLoading();

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => CancelLoading();

    private void OnImageOpened(object sender, RoutedEventArgs e)
    {
        _loadedSource = Source;
        _loadedDecodeWidth = (int)Math.Round(DecodeWidth);
        IsImageLoading = false;
    }

    private void OnImageFailed(object sender, ExceptionRoutedEventArgs e)
        => IsImageLoading = false;

    private void StartLoading()
    {
        if (!IsLoaded || Source is not { } source)
        {
            return;
        }

        var decodeWidth = (int)Math.Round(DecodeWidth);
        if (_image.Source is not null && source == _loadedSource && decodeWidth == _loadedDecodeWidth)
        {
            return;
        }

        CancelLoading();
        IsImageLoading = true;
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        _ = LoadImageAsync(source, decodeWidth, cancellation);
    }

    private async Task LoadImageAsync(Uri source, int decodeWidth, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = decodeWidth, AutoPlay = false };
            if (source.Scheme is not ("http" or "https"))
            {
                bitmap.UriSource = source;
                _image.Source = bitmap;
                return;
            }

            var bytes = await NativeCoverImageCache.GetAsync(source, token);
            using var stream = new MemoryStream(bytes, writable: false).AsRandomAccessStream();
            await bitmap.SetSourceAsync(stream).AsTask(token);
            if (token.IsCancellationRequested || !IsLoaded)
            {
                return;
            }

            _image.Source = bitmap;
            _loadedSource = source;
            _loadedDecodeWidth = decodeWidth;
            IsImageLoading = false;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // A failed image stays empty, like the native Image failure path.
            if (!token.IsCancellationRequested && IsLoaded)
            {
                IsImageLoading = false;
            }
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
            {
                _loadCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private void CancelLoading()
    {
        var cancellation = _loadCancellation;
        _loadCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        IsImageLoading = false;
    }
}
