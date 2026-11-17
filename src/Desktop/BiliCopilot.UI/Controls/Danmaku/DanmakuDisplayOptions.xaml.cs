// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.View;
using Richasy.WinUIKernel.Share.Toolkits;

namespace BiliCopilot.UI.Controls.Danmaku;

/// <summary>
/// 弹幕显示选项.
/// </summary>
public sealed partial class DanmakuDisplayOptions : DanmakuControlBase
{
    private bool _isSynchronizingFont;

    /// <summary><see cref="IsCommandDanmakuSwitchVisible"/> dependency property.</summary>
    public static readonly DependencyProperty IsCommandDanmakuSwitchVisibleProperty =
        DependencyProperty.Register(nameof(IsCommandDanmakuSwitchVisible), typeof(bool), typeof(DanmakuDisplayOptions), new PropertyMetadata(true));

    public DanmakuDisplayOptions()
    {
        Settings = this.Get<SettingsPageViewModel>();
        InitializeComponent();
    }

    /// <summary>
    /// The shared settings view model that owns the interactive danmaku switch for every player.
    /// </summary>
    private SettingsPageViewModel Settings { get; }

    /// <summary>
    /// Whether the interactive danmaku switch shows. A host turns it off for a media that has no interactive danmaku.
    /// </summary>
    public bool IsCommandDanmakuSwitchVisible
    {
        get => (bool)GetValue(IsCommandDanmakuSwitchVisibleProperty);
        set => SetValue(IsCommandDanmakuSwitchVisibleProperty, value);
    }

    protected override void OnControlLoaded()
    {
        // The settings page may never have been opened: bring the shared value up to date first,
        // so that flipping the switch here is always seen as a change.
        Settings.IsCommandDanmakuEnabled = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.IsCommandDanmakuEnabled, true);
        var renderer = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DanmakuRenderer, DanmakuRendererType.Mpv);
        RendererComboBox.SelectedIndex = renderer == DanmakuRendererType.Win2D ? 0 : 1;
        SynchronizeFontSelection();
    }

    private void OnRendererChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = RendererComboBox.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        var renderer = index == 0 ? DanmakuRendererType.Win2D : DanmakuRendererType.Mpv;
        if (ViewModel.Renderer != renderer)
        {
            ViewModel.Renderer = renderer;
        }
    }

    private async void OnDanmakuFontDropDownOpened(object sender, object e)
    {
        if (ViewModel is not { } viewModel || viewModel.ReloadFontsCommand.IsRunning) return;
        await viewModel.ReloadFontsCommand.ExecuteAsync(default);
        if (IsLoaded && ReferenceEquals(ViewModel, viewModel)) SynchronizeFontSelection();
    }

    private void SynchronizeFontSelection()
    {
        _isSynchronizingFont = true;
        try
        {
            DanmakuFontComboBox.SelectedItem = DanmakuFontCatalog.Find(ViewModel.Fonts, ViewModel.DanmakuFontFamily);
        }
        finally
        {
            _isSynchronizingFont = false;
        }
    }

    private void OnDanmakuFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingFont) return;
        var item = DanmakuFontComboBox.SelectedItem as SystemFont;
        if (item != null && ViewModel.DanmakuFontFamily != item.LocalName)
        {
            ViewModel.DanmakuFontFamily = item.LocalName;
        }
    }
}
