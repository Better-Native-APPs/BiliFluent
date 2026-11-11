// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI;
using Microsoft.UI.Windowing;

namespace BiliCopilot.UI.Forms;

/// <summary>
/// Shared title-bar setup for the app's WinUIEx windows: content drawn into the title bar, no system icon or
/// menu, and see-through caption buttons.
/// </summary>
internal static class WindowChrome
{
    /// <summary>
    /// Extend the window content into the title bar. When <paramref name="titleBar"/> is given, it becomes the
    /// drag region through <see cref="Window.SetTitleBar(UIElement)"/> and the caption buttons follow the theme
    /// of the window content. Without it, the caller owns the non-client regions and the caption-button colors
    /// (the main window does this in RootLayout).
    /// </summary>
    public static void ExtendIntoTitleBar(this Window window, UIElement? titleBar)
    {
        var bar = window.AppWindow.TitleBar;
        bar.ExtendsContentIntoTitleBar = true;
        bar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        if (titleBar is null) return;

        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(titleBar);
        if (window.Content is FrameworkElement root)
        {
            root.ActualThemeChanged += (sender, _) => SyncCaptionTheme(window.AppWindow, sender.ActualTheme);
            SyncCaptionTheme(window.AppWindow, root.ActualTheme);
        }
    }

    /// <summary>
    /// Let the system caption buttons use the theme of the window content (the app theme can differ from Windows').
    /// </summary>
    private static void SyncCaptionTheme(AppWindow appWindow, ElementTheme theme)
        => appWindow.TitleBar.PreferredTheme = theme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
}
