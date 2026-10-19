using Microsoft.UI.Xaml;
using WinRT.Interop;
using Windows.UI;
using XtremClipboard.Services.Native;

namespace XtremClipboard.Services;

/// <summary>
/// Barre de titre native alignée sur le thème clair ou sombre du contenu WinUI.
/// </summary>
public static class WindowTheme
{
    public static void Track(Window window)
    {
        if (window.Content is FrameworkElement root)
        {
            root.ActualThemeChanged += (_, _) => ApplyTitleBar(window);
            root.Loaded += (_, _) => ApplyTitleBar(window);
        }

        window.Activated += (_, _) => ApplyTitleBar(window);
        ApplyTitleBar(window);
    }

    public static void ApplyTitleBar(Window window)
    {
        if (window.Content is not FrameworkElement root)
            return;

        var dark = root.ActualTheme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => !SystemUsesLightTheme()
        };
        var hwnd = WindowNative.GetWindowHandle(window);
        var flag = dark ? 1 : 0;
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmwaUseImmersiveDarkMode, ref flag, sizeof(int));

        var title = window.AppWindow.TitleBar;
        var background = dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
        var foreground = dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 28, 28, 28);
        var hover = dark ? Color.FromArgb(255, 58, 58, 58) : Color.FromArgb(255, 229, 229, 229);
        var pressed = dark ? Color.FromArgb(255, 80, 80, 80) : Color.FromArgb(255, 204, 204, 204);
        var inactiveForeground = dark ? Color.FromArgb(255, 160, 160, 160) : Color.FromArgb(255, 110, 110, 110);

        title.BackgroundColor = background;
        title.ForegroundColor = foreground;
        title.InactiveBackgroundColor = background;
        title.InactiveForegroundColor = inactiveForeground;
        title.ButtonBackgroundColor = background;
        title.ButtonForegroundColor = foreground;
        title.ButtonHoverBackgroundColor = hover;
        title.ButtonHoverForegroundColor = foreground;
        title.ButtonPressedBackgroundColor = pressed;
        title.ButtonPressedForegroundColor = foreground;
        title.ButtonInactiveBackgroundColor = background;
        title.ButtonInactiveForegroundColor = inactiveForeground;
    }

    private static bool SystemUsesLightTheme()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }
}
