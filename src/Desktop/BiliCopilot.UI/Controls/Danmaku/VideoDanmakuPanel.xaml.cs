// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using Richasy.BiliKernel.Models;
using Richasy.Danmaku;
using Richasy.Danmaku.Enums;
using Richasy.Danmaku.Legacy;
using Richasy.Danmaku.Models;
using Richasy.MpvKernel.Core.Enums;
using Richasy.MpvKernel.Player;
using System.ComponentModel;

namespace BiliCopilot.UI.Controls.Danmaku;

/// <summary>
/// 视频弹幕面板.
/// </summary>
public sealed partial class VideoDanmakuPanel : DanmakuControlBase
{
    private const int DefaultRollingDensity = 2; // Package density level 2 limits rolling danmaku to 20.
    private int _lastProgress;
    private int _redrawVersion;
    private bool _redrawPending;
    private bool _hasDanmakuList;
    private bool _isRendererRunning;
    private DanmakuFrostMaster? _danmakuMaster;
    private DanmakuRenderer? _danmakuRenderer;
    private MpvAssDanmakuRenderer? _mpvRenderer;
    private DanmakuRendererType _rendererType;
    private CanvasAnimatedControl? _canvas;
    private CancellationTokenSource? _redrawCancellation;
    private DispatcherQueueTimer? _redrawTimer;
    private DanmakuRenderViewModel? _subscribedViewModel;
    private PlayerViewModel? _playerViewModel;
    private MpvPlayer? _observedPlayer;
    private AppWindow? _appWindow;
    private XamlRoot? _observedRoot;

    public VideoDanmakuPanel() => InitializeComponent();

    /// <inheritdoc/>
    protected override void OnControlLoaded()
    {
        if (ViewModel is null)
        {
            return;
        }

        SubscribeViewModel(ViewModel);
        AttachPlaybackHost();
        SizeChanged += OnPanelSizeChanged;
        ReloadRenderer();
        ResetDanmakuStyle();
        Redraw();
    }

    private void ReloadRenderer()
    {
        var type = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DanmakuRenderer, DanmakuRendererType.Mpv);
        _rendererType = type;
        if (type == DanmakuRendererType.DirectX && _danmakuRenderer == null)
        {
            _danmakuRenderer = new DanmakuRenderer(RootGrid, this.Get<ILogger<DanmakuRenderer>>());
        }
        else if (type == DanmakuRendererType.Win2D && _danmakuMaster == null)
        {
            _danmakuMaster = new DanmakuFrostMaster(RootGrid);
            _canvas = RootGrid.Children.OfType<CanvasAnimatedControl>().FirstOrDefault();
        }
        else if (type == DanmakuRendererType.Mpv && _mpvRenderer is null)
        {
            _mpvRenderer = _playerViewModel?.CreateMpvDanmakuRenderer();
        }

        PauseRendering();
    }

    /// <inheritdoc/>
    protected override void OnControlUnloaded()
    {
        SubscribeViewModel(null);
        SizeChanged -= OnPanelSizeChanged;
        CancelRedraw();
        if (_redrawTimer is not null)
        {
            _redrawTimer.Tick -= OnRedrawTimerTick;
            _redrawTimer = null;
        }

        DetachPlaybackHost();
        _danmakuMaster?.Close();
        _danmakuRenderer?.Dispose();
        if (_mpvRenderer is not null) _ = _mpvRenderer.DisposeAsync().AsTask();
        _mpvRenderer = null;
        _danmakuMaster = null;
        _danmakuRenderer = null;
        _canvas = null;
        _hasDanmakuList = false;
        _isRendererRunning = false;
        RootGrid.Children.Clear();
    }

    /// <inheritdoc/>
    protected override void OnViewModelChanged(DanmakuRenderViewModel? oldValue, DanmakuRenderViewModel? newValue)
    {
        CancelRedraw();
        _hasDanmakuList = false;
        _redrawPending = false;
        _danmakuMaster?.Clear();
        _danmakuRenderer?.Clear();
        _mpvRenderer?.Clear();
        PauseRendering();
        SubscribeViewModel(IsLoaded ? newValue : null);
        if (newValue is null)
        {
            return;
        }

        // 延迟执行样式重置,避免在 Layout 期间触发重入.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (IsLoaded && ViewModel == newValue)
            {
                AttachPlaybackHost();
                if (_rendererType == DanmakuRendererType.Mpv && _mpvRenderer is null) ReloadRenderer();
                ResetDanmakuStyle();
                Redraw();
            }
        });
    }

    private void SubscribeViewModel(DanmakuRenderViewModel? viewModel)
    {
        if (ReferenceEquals(_subscribedViewModel, viewModel)) return;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.RequestClearDanmaku -= OnRequestClearDanmaku;
            _subscribedViewModel.ProgressChanged -= OnProgressChanged;
            _subscribedViewModel.PauseDanmaku -= OnPauseDanmaku;
            _subscribedViewModel.ResumeDanmaku -= OnResumeDanmaku;
            _subscribedViewModel.RequestResetStyle -= OnRequestResetStyle;
            _subscribedViewModel.ExtraSpeedChanged -= OnExtraSpeedChanged;
            _subscribedViewModel.RequestRedrawDanmaku -= OnRedrawDanmaku;
            _subscribedViewModel.RequestAddSingleDanmaku -= OnRequestAddSingleDanmaku;
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _subscribedViewModel = viewModel;
        if (viewModel is null) return;
        _lastProgress = viewModel.Position;
        viewModel.RequestClearDanmaku += OnRequestClearDanmaku;
        viewModel.ProgressChanged += OnProgressChanged;
        viewModel.PauseDanmaku += OnPauseDanmaku;
        viewModel.ResumeDanmaku += OnResumeDanmaku;
        viewModel.RequestResetStyle += OnRequestResetStyle;
        viewModel.ExtraSpeedChanged += OnExtraSpeedChanged;
        viewModel.RequestRedrawDanmaku += OnRedrawDanmaku;
        viewModel.RequestAddSingleDanmaku += OnRequestAddSingleDanmaku;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.Renderer))
        {
            // 在 UI 线程上异步执行渲染器切换,避免重入问题
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!IsLoaded || ViewModel is null)
                {
                    return;
                }

                if (_rendererType != ViewModel.Renderer)
                {
                    CancelRedraw();
                    _danmakuMaster?.Close();
                    _danmakuRenderer?.Dispose();
                    if (_mpvRenderer is not null) _ = _mpvRenderer.DisposeAsync().AsTask();
                    _mpvRenderer = null;
                    _danmakuMaster = null;
                    _danmakuRenderer = null;
                    _canvas = null;
                    _hasDanmakuList = false;
                    _isRendererRunning = false;
                    RootGrid.Children.Clear();
                    ReloadRenderer();
                    ResetDanmakuStyle();
                    Redraw();
                }
            });
        }
        else if (e.PropertyName == nameof(DanmakuRenderViewModel.IsShowDanmaku))
        {
            if (!ViewModel.IsShowDanmaku)
            {
                CancelRedraw();
                _redrawPending = true;
            }
            else if (_redrawPending)
            {
                Redraw();
            }

            UpdateRendererPlaybackState();
        }
        else if (e.PropertyName == nameof(DanmakuRenderViewModel.IsPaused))
        {
            UpdateRendererPlaybackState();
        }
        else if (e.PropertyName == nameof(DanmakuRenderViewModel.IsDanmakuLimit))
        {
            ResetDanmakuStyle();
        }
        else if (_mpvRenderer is not null && e.PropertyName is nameof(DanmakuRenderViewModel.DanmakuOpacity)
            or nameof(DanmakuRenderViewModel.DanmakuFontSize) or nameof(DanmakuRenderViewModel.DanmakuArea)
            or nameof(DanmakuRenderViewModel.DanmakuSpeed) or nameof(DanmakuRenderViewModel.DanmakuFontFamily)
            or nameof(DanmakuRenderViewModel.IsDanmakuBold) or nameof(DanmakuRenderViewModel.OutlineSize)
            or nameof(DanmakuRenderViewModel.IsRollingEnabled) or nameof(DanmakuRenderViewModel.IsTopEnabled)
            or nameof(DanmakuRenderViewModel.IsBottomEnabled))
        {
            UpdateMpvStyle();
        }
    }

    private void OnRequestAddSingleDanmaku(object? sender, string e)
    {
        var location = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DanmakuLocation, DanmakuLocation.Scroll);
        var color = AppToolkit.HexToColor(SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DanmakuColor, Microsoft.UI.Colors.White.ToString()));
        var isStandardSize = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.IsDanmakuStandardSize, true);
        var model = new DanmakuItem
        {
            StartMs = Convert.ToUInt32(_lastProgress * 1000),
            Mode = (DanmakuMode)((int)location),
            TextColor = color,
            BaseFontSize = isStandardSize ? 20 : 24,
            Text = e,
            HasOutline = true,
            HasBorder = true
        };

        _danmakuMaster?.AddRealtimeDanmaku(model, true);
        _danmakuRenderer?.AddRealtimeDanmaku(model, true);
        if (_mpvRenderer is not null)
        {
            var position = _observedPlayer?.Position ?? _lastProgress;
            _mpvRenderer.AddRealtime(ToMpvItem(model) with { Start = Math.Max(0, position) + 0.3 });
        }
        _hasDanmakuList = true;
        UpdateRendererPlaybackState();
    }

    private void OnRequestClearDanmaku(object? sender, EventArgs e)
    {
        CancelRedraw();
        _redrawPending = false;
        _hasDanmakuList = false;
        _lastProgress = 0;

        _danmakuMaster?.Clear();
        _danmakuRenderer?.Clear();
        _mpvRenderer?.Clear();
        PauseRendering();
    }

    private void OnRequestResetStyle(object? sender, EventArgs e)
        => ResetDanmakuStyle();

    private void OnExtraSpeedChanged(object? sender, EventArgs e)
        => ResetSpeed();

    private void OnProgressChanged(object? sender, int e)
    {
        _lastProgress = e;
        if (_isRendererRunning) UpdateDanmakuTime(e);
    }

    private void OnResumeDanmaku(object? sender, EventArgs e)
        => UpdateRendererPlaybackState();

    private void OnPauseDanmaku(object? sender, EventArgs e)
        => PauseRendering();

    private void OnRedrawDanmaku(object? sender, EventArgs e)
        => Redraw();

    private void ResetDanmakuStyle()
    {
        if (ViewModel is null)
        {
            return;
        }

        if (_danmakuMaster != null)
        {
            _danmakuMaster.SetRollingDensity(ViewModel.IsDanmakuLimit ? DefaultRollingDensity : -1);
            _danmakuMaster.SetAutoControlDensity(ViewModel.IsDanmakuLimit);
            _danmakuMaster.SetOpacity(ViewModel.DanmakuOpacity);
            _danmakuMaster.SetBorderColor(Colors.Gray);
            _danmakuMaster.SetRollingAreaRatio(Convert.ToInt32(ViewModel.DanmakuArea * 10));
            _danmakuMaster.SetDanmakuFontSizeOffset(ViewModel.DanmakuFontSize);
            _danmakuMaster.SetFontFamilyName(ViewModel.DanmakuFontFamily);
            _danmakuMaster.SetIsTextBold(ViewModel.IsDanmakuBold);
            _danmakuMaster.SetDanmakuEnabledType(ViewModel.IsRollingEnabled, ViewModel.IsTopEnabled, ViewModel.IsBottomEnabled);
            _danmakuMaster.SetOutlineSize(ViewModel.OutlineSize);
        }

        if (_danmakuRenderer != null)
        {
            var config = DanmakuConfiguration.CreateDefault();
            config.RollingDensity = ViewModel.IsDanmakuLimit ? DefaultRollingDensity : -1;
            config.AutoControlDensity = ViewModel.IsDanmakuLimit;
            config.TextOpacity = ViewModel.DanmakuOpacity;
            config.FontSizeScale = ViewModel.DanmakuFontSize;
            config.RollingAreaRatio = (float)ViewModel.DanmakuArea;
            config.FontFamilyName = ViewModel.DanmakuFontFamily ?? "Segoe UI";
            config.IsTextBold = ViewModel.IsDanmakuBold;
            config.OutlineSize = ViewModel.OutlineSize;

            _danmakuRenderer.SetDanmakuEnabledType(ViewModel.IsRollingEnabled, ViewModel.IsTopEnabled, ViewModel.IsBottomEnabled);
            _danmakuRenderer.UpdateConfiguration(config);
        }

        ResetSpeed();
        UpdateMpvStyle();
        UpdateRendererPlaybackState();
    }

    private void UpdateMpvStyle(int delay = 300)
    {
        if (_mpvRenderer is null || ViewModel is not { } vm) return;
        _mpvRenderer.SetStyle(new MpvAssDanmakuStyle(vm.DanmakuFontFamily ?? "Segoe UI", vm.DanmakuFontSize,
            vm.IsDanmakuBold, vm.DanmakuOpacity, vm.OutlineSize, vm.DanmakuArea, vm.DanmakuSpeed,
            vm.IsRollingEnabled, vm.IsTopEnabled, vm.IsBottomEnabled, vm.IsDanmakuLimit, ActualWidth, ActualHeight,
            XamlRoot?.RasterizationScale ?? 1), delay);
    }

    private static MpvAssDanmakuItem ToMpvItem(DanmakuItem item)
        => new(item.StartMs / 1000d, (int)item.Mode, item.BaseFontSize, item.Text,
            ((uint)item.TextColor.R << 16) | ((uint)item.TextColor.G << 8) | item.TextColor.B);

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateMpvStyle(500);

    private void ResetSpeed()
    {
        var finalSpeed = ViewModel.DanmakuSpeed * 5 * ViewModel.ExtraSpeed;
        _danmakuMaster?.SetRollingSpeed(finalSpeed);
        _danmakuRenderer?.UpdateConfiguration(new DanmakuConfiguration { RollingSpeedBase = (float)finalSpeed });
    }

    private void Redraw()
    {
        CancelRedraw();
        _mpvRenderer?.Invalidate();
        _redrawPending = true;
        if (!CanShowDanmaku()) return;
        if (_redrawTimer is null)
        {
            _redrawTimer = DispatcherQueue.CreateTimer();
            _redrawTimer.IsRepeating = false;
            _redrawTimer.Tick += OnRedrawTimerTick;
        }

        _redrawTimer.Interval = TimeSpan.FromMilliseconds(_rendererType == DanmakuRendererType.Mpv ? 800 : 150);
        _redrawTimer.Start();
    }

    private async void OnRedrawTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        var viewModel = ViewModel;
        if (!CanShowDanmaku() || viewModel is null) return;
        var version = _redrawVersion;
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _redrawCancellation = cancellation;
        try
        {
            // Snapshot the UI-owned pool; parsing, duplicate merging and sorting create only data objects.
            var snapshot = viewModel.GetCachedDanmakus().ToArray();
            var useMpv = _rendererType == DanmakuRendererType.Mpv;
            var parsed = await Task.Run(() =>
            {
                var items = BilibiliDanmakuParser.GetDanmakuList(snapshot, true, token);
                var assItems = useMpv ? items.Select(ToMpvItem).ToArray() : null;
                return (Danmakus: items, AssItems: assItems);
            }, token);
            var danmakus = parsed.Danmakus;
            token.ThrowIfCancellationRequested();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (version != _redrawVersion || ViewModel != viewModel || !CanShowDanmaku()) return;

                _danmakuMaster?.SetDanmakuList(danmakus);
                _danmakuRenderer?.SetDanmakuList(danmakus);
                if (parsed.AssItems is { } assItems) _mpvRenderer?.SetItems(assItems);
                _hasDanmakuList = danmakus.Count > 0;
                _redrawPending = false;
                if (_lastProgress >= 0)
                {
                    var position = Convert.ToUInt32(_lastProgress * 1000d);
                    _danmakuMaster?.Seek(position);
                    _danmakuRenderer?.Seek(position);
                }

                ResetDanmakuStyle();
                UpdateRendererPlaybackState(force: true);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            this.Get<ILogger<VideoDanmakuPanel>>().LogError(ex, "Failed to redraw danmaku.");
        }
        finally
        {
            if (ReferenceEquals(_redrawCancellation, cancellation)) _redrawCancellation = null;
        }
    }

    private void CancelRedraw()
    {
        _redrawVersion++;
        _redrawTimer?.Stop();
        _redrawCancellation?.Cancel();
    }

    private void AttachPlaybackHost()
    {
        var previousHost = _playerViewModel;
        DetachPlaybackHost();
        for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is PlayerOverlay overlay)
            {
                _playerViewModel = overlay.ViewModel;
                break;
            }
        }

        _observedPlayer = _playerViewModel?.Player;
        if (_playerViewModel is not null)
        {
            _playerViewModel.PropertyChanged += OnPlayerViewModelPropertyChanged;
            _playerViewModel.MpvDanmakuHostReady += OnMpvDanmakuHostReady;
        }
        if (_observedPlayer is not null) _observedPlayer.PropertyChanged += OnObservedPlayerPropertyChanged;
        _appWindow = _playerViewModel?.Window?.GetWindow();
        if (_appWindow is not null) _appWindow.Changed += OnAppWindowChanged;
        _observedRoot = XamlRoot;
        if (_observedRoot is not null) _observedRoot.Changed += OnXamlRootChanged;
        if (_mpvRenderer is not null && !ReferenceEquals(previousHost, _playerViewModel))
        {
            _ = _mpvRenderer.DisposeAsync().AsTask();
            _mpvRenderer = null;
        }
    }

    private void DetachPlaybackHost()
    {
        if (_observedPlayer is not null) _observedPlayer.PropertyChanged -= OnObservedPlayerPropertyChanged;
        if (_playerViewModel is not null)
        {
            _playerViewModel.PropertyChanged -= OnPlayerViewModelPropertyChanged;
            _playerViewModel.MpvDanmakuHostReady -= OnMpvDanmakuHostReady;
        }
        if (_appWindow is not null) _appWindow.Changed -= OnAppWindowChanged;
        if (_observedRoot is not null) _observedRoot.Changed -= OnXamlRootChanged;
        _observedPlayer = null;
        _appWindow = null;
        _observedRoot = null;
        _playerViewModel = null;
    }

    private void OnObservedPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MpvPlayer.PlaybackState) or nameof(MpvPlayer.IsPlaybackInitialized))) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsLoaded || !ReferenceEquals(sender, _observedPlayer)) return;
            if (_observedPlayer?.IsPlaybackInitialized == true && _playerViewModel?.IsConnecting == false)
                _mpvRenderer?.NotifyReady(refreshGeometry: e.PropertyName == nameof(MpvPlayer.IsPlaybackInitialized));
            UpdateRendererPlaybackState();
        });
    }

    private void OnPlayerViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PlayerViewModel.Player) or nameof(PlayerViewModel.IsDanmakuEnabled)
            or nameof(PlayerViewModel.IsConnecting) or nameof(PlayerViewModel.MediaGeneration))) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsLoaded || !ReferenceEquals(sender, _playerViewModel)) return;
            if (e.PropertyName == nameof(PlayerViewModel.Player))
            {
                if (_mpvRenderer is not null) _ = _mpvRenderer.DisposeAsync().AsTask();
                _mpvRenderer = null;
                AttachPlaybackHost();
            }

            if (_rendererType == DanmakuRendererType.Mpv && _mpvRenderer is null)
            {
                ReloadRenderer();
                UpdateMpvStyle();
                Redraw();
            }

            if (_observedPlayer?.IsPlaybackInitialized == true && _playerViewModel?.IsConnecting == false)
                _mpvRenderer?.NotifyReady(refreshGeometry: e.PropertyName == nameof(PlayerViewModel.IsConnecting));

            OnHostVisibilityChanged();
        });
    }

    private void OnMpvDanmakuHostReady(object? sender, EventArgs args)
    {
        if (!IsLoaded || !ReferenceEquals(sender, _playerViewModel)
            || _rendererType != DanmakuRendererType.Mpv || _mpvRenderer is not null) return;
        ReloadRenderer();
        ResetDanmakuStyle();
        Redraw();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        => OnHostVisibilityChanged();

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
        => OnHostVisibilityChanged();

    private void OnHostVisibilityChanged()
    {
        if (!CanShowDanmaku()) CancelRedraw();
        else if (_redrawPending) Redraw();
        // Package renderers resize themselves; ASS geometry is rebuilt without reparsing the pool.
        UpdateMpvStyle(500);
        UpdateRendererPlaybackState();
    }

    private bool CanShowDanmaku()
        => IsLoaded && ViewModel is { IsShowDanmaku: true } && Visibility == Visibility.Visible
            && (_playerViewModel is null || (_playerViewModel.IsDanmakuEnabled && !_playerViewModel.IsPlaybackClosing))
            && XamlRoot?.IsHostVisible == true
            && (_appWindow is null || (_appWindow.IsVisible
                && _appWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized }));

    private void UpdateRendererPlaybackState(bool force = false)
    {
        if (_mpvRenderer is not null)
        {
            _mpvRenderer.SetVisible(CanShowDanmaku());
            return;
        }

        var isPlaying = _observedPlayer is not null ? _observedPlayer.PlaybackState == MpvPlayerState.Playing : ViewModel?.IsPaused == false;
        var shouldRun = CanShowDanmaku() && _hasDanmakuList && isPlaying;
        if (!force && shouldRun == _isRendererRunning) return;
        if (!shouldRun)
        {
            PauseRendering();
            return;
        }

        _isRendererRunning = true;
        UpdateDanmakuTime(_lastProgress);
        _danmakuRenderer?.Resume();
        _danmakuMaster?.Resume();
        if (_canvas is not null) _canvas.Paused = false;
    }

    private void PauseRendering()
    {
        _isRendererRunning = false;
        _danmakuRenderer?.Pause();
        _danmakuMaster?.Pause();
        // Legacy Pause freezes items but does not stop CanvasAnimatedControl's per-frame Draw callbacks.
        if (_canvas is not null) _canvas.Paused = true;
    }

    private void UpdateDanmakuTime(double pos)
    {
        if (pos < 0 || double.IsNaN(pos))
        {
            return;
        }

        _danmakuRenderer?.UpdateTime(Convert.ToUInt32(pos * 1000));
        _danmakuMaster?.UpdateTime(Convert.ToUInt32(pos * 1000));
    }
}
