using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Models.Danmaku;
using Richasy.WinUIKernel.Share.Toolkits;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class DanmakuRenderViewModel : ViewModelBase
{
    // A CJK UI font keeps Latin letters and digits in the same design as Chinese glyphs.
    internal const string DefaultDanmakuFontFamily = "Microsoft YaHei UI";
    private const string LegacyDefaultDanmakuFontFamily = "Segoe UI";
    private const string HoverActionsSettingKey = "IsDanmakuHoverActionsEnabled";

    internal int Position => _position;

    public DanmakuRenderViewModel()
    {
        // The old view model persisted its Win2D default on startup, so switch once to the mpv renderer.
        if (!SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DanmakuRendererMpvMigrated, false))
        {
            SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.DanmakuRenderer, DanmakuRendererType.Mpv);
            SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.DanmakuRendererMpvMigrated, true);
        }

        // The old default was persisted on first use, so a stored legacy default is moved once.
        // A later manual choice of that font is kept because the marker is already set.
        if (!SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DanmakuDefaultFontMigrated, false))
        {
            if (SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DanmakuFontFamily, DefaultDanmakuFontFamily) == LegacyDefaultDanmakuFontFamily)
            {
                SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.DanmakuFontFamily, DefaultDanmakuFontFamily);
            }

            SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.DanmakuDefaultFontMigrated, true);
        }

        Renderer = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DanmakuRenderer, DanmakuRendererType.Mpv);
        DanmakuFontFamily = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DanmakuFontFamily, DefaultDanmakuFontFamily);
        IsHoverActionsEnabled = this.Get<ISettingsToolkit>().ReadLocalSetting(HoverActionsSettingKey, true);
    }

    public void Initialize(IList<DanmakuInformation> danmakus)
    {
        var wasProjected = HasSponsorCategoryProjection;
        _cachedDanmakus = danmakus;
        CaptureSponsorDanmakuData();
        RefreshSponsorCategories(redraw: false);
        ResetData();
        if (wasProjected || HasSponsorCategoryProjection) RedrawSponsorCategories();
        else Redraw();
    }

    public IList<DanmakuInformation> GetCachedDanmakus()
        => GetSponsorCategoryDanmakus();

    /// <summary>
    /// 重置数据.
    /// </summary>
    public void ResetData()
    {
        ResetOptions();
    }

    /// <summary>
    /// 更新进度.
    /// </summary>
    public void UpdatePosition(int position)
    {
        if (_position == position) return;
        _position = position;
        ProgressChanged?.Invoke(this, position);
    }

    /// <summary>
    /// 暂停.
    /// </summary>
    public void Pause()
    {
        IsPaused = true;
        PauseDanmaku?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 恢复.
    /// </summary>
    public void Resume()
    {
        IsPaused = false;
        ResumeDanmaku?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 重新绘制.
    /// </summary>
    public void Redraw()
    {
        RequestRedrawDanmaku?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 清除数据.
    /// </summary>
    public void ClearAll()
    {
        ClearDanmaku();
    }

    /// <summary>
    /// 清理弹幕.
    /// </summary>
    public void ClearDanmaku()
    {
        _position = 0;
        _cachedDanmakus = default;
        ClearSponsorDanmakuData();
        RequestClearDanmaku?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 重置样式.
    /// </summary>
    public void ResetStyle()
        => RequestResetStyle?.Invoke(this, EventArgs.Empty);

    public void AddDanmaku(string text)
    {
        if (!IsSponsorCategoryHidden(_position)) RequestAddSingleDanmaku?.Invoke(this, text);
    }

    private void ResetOptions()
    {
        IsShowDanmaku = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.IsShowDanmaku, true);
        DanmakuOpacity = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DanmakuOpacity, 0.8);
        DanmakuFontSize = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DanmakuFontSize, 1.2d);
        DanmakuArea = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DanmakuArea, 1d);
        DanmakuSpeed = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DanmakuSpeed, 1.2d);
        DanmakuFontFamily = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DanmakuFontFamily, DefaultDanmakuFontFamily);
        IsDanmakuBold = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.IsDanmakuBold, true);
        OutlineSize = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DanmakuOutlineSize, 1d);
        IsRollingEnabled = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.IsRollingDanmakuEnabled, true);
        IsTopEnabled = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.IsTopDanmakuEnabled, true);
        IsBottomEnabled = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.IsBottomDanmakuEnabled, true);
        IsHoverActionsEnabled = this.Get<ISettingsToolkit>().ReadLocalSetting(HoverActionsSettingKey, true);
        IsDanmakuLimit = true;
        ResetStyle();
    }

    [RelayCommand]
    private async Task ReloadFontsAsync()
    {
        if (Fonts.Count > 0)
        {
            return;
        }

        IReadOnlyList<SystemFont> fonts;
        try
        {
            fonts = await DanmakuFontCatalog.LoadAsync();
        }
        catch (Exception ex)
        {
            this.Get<ILogger<DanmakuRenderViewModel>>().LogError(ex, "Failed to load danmaku fonts; the selected font is unchanged.");
            return;
        }

        // Resume on the UI context and publish only the completed snapshot.
        Fonts = fonts;
        if (DanmakuFontCatalog.Find(fonts, DanmakuFontFamily) is null)
        {
            DanmakuFontFamily = DefaultDanmakuFontFamily;
        }
    }

    partial void OnIsShowDanmakuChanged(bool value)
    {
        if (value)
        {
            Redraw();
        }

        SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.IsShowDanmaku, value);
    }

    partial void OnDanmakuOpacityChanged(double value)
        => SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.DanmakuOpacity, value);

    partial void OnDanmakuFontSizeChanged(double value)
        => SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.DanmakuFontSize, value);

    partial void OnIsDanmakuBoldChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.IsDanmakuBold, value);

    partial void OnDanmakuAreaChanged(double value)
        => SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.DanmakuArea, value);

    partial void OnDanmakuSpeedChanged(double value)
        => SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.DanmakuSpeed, value);

    partial void OnDanmakuFontFamilyChanged(string value)
    {
        try
        {
            SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.DanmakuFontFamily, value);
        }
        catch (Exception ex)
        {
            this.Get<ILogger<DanmakuRenderViewModel>>().LogError(ex, "Failed to save the danmaku font preference.");
        }
    }

    partial void OnIsTopEnabledChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.IsTopDanmakuEnabled, value);

    partial void OnIsBottomEnabledChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.IsBottomDanmakuEnabled, value);

    partial void OnIsRollingEnabledChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.IsRollingDanmakuEnabled, value);

    partial void OnExtraSpeedChanged(double value)
        => ExtraSpeedChanged?.Invoke(this, EventArgs.Empty);

    partial void OnOutlineSizeChanged(double value)
        => SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.DanmakuOutlineSize, value);

    partial void OnRendererChanged(DanmakuRendererType value)
        => SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.DanmakuRenderer, value);

    partial void OnIsHoverActionsEnabledChanged(bool value)
        => this.Get<ISettingsToolkit>().WriteLocalSetting(HoverActionsSettingKey, value);
}
