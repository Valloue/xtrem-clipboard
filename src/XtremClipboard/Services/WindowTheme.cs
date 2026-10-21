using Microsoft.UI.Xaml;
using Serilog;
using WinRT.Interop;
using Windows.Foundation;
using Windows.UI;
using XtremClipboard.Services.Native;

namespace XtremClipboard.Services;

/// <summary>
/// Barre de titre native alignée sur le thème clair ou sombre du contenu WinUI.
/// </summary>
public static class WindowTheme
{
    /// <summary>The WinUI Desktop Window object has already been closed.</summary>
    private const int ClosedWindowHResult = unchecked((int)0x800710DD);

    private sealed class Subscription
    {
        public bool Closed;
        public bool Detached;
        public FrameworkElement? Root;
        public TypedEventHandler<FrameworkElement, object>? ThemeChanged;
        public RoutedEventHandler? Loaded;
        public TypedEventHandler<object, WindowActivatedEventArgs>? Activated;
        public TypedEventHandler<object, WindowEventArgs>? ClosedHandler;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, Subscription> Subscriptions = new();

    public static void Track(Window window)
    {
        var sub = new Subscription();
        Subscriptions.Add(window, sub);

        if (window.Content is FrameworkElement root)
        {
            sub.Root = root;
            sub.ThemeChanged = (_, _) => ApplyTitleBar(window);
            sub.Loaded = (_, _) => ApplyTitleBar(window);
            root.ActualThemeChanged += sub.ThemeChanged;
            root.Loaded += sub.Loaded;
        }

        sub.Activated = (_, _) => ApplyTitleBar(window);
        window.Activated += sub.Activated;

        sub.ClosedHandler = (_, _) =>
        {
            sub.Closed = true;
            Detach(window, sub);
        };
        window.Closed += sub.ClosedHandler;

        ApplyTitleBar(window);
    }

    public static void ApplyTitleBar(Window window)
    {
        // Activated peut partir avant Closed pendant Hide() → Close() : le flag ne suffit pas.
        if (Subscriptions.TryGetValue(window, out var sub) && sub.Closed)
            return;

        try
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
        catch (Exception ex) when (ex.HResult == ClosedWindowHResult)
        {
            if (sub is not null)
            {
                sub.Closed = true;
                Detach(window, sub);
            }

            Log.Debug("Barre de titre ignorée — fenêtre déjà fermée {HResult}", ex.HResult);
        }
    }

    private static void Detach(Window window, Subscription sub)
    {
        if (sub.Detached)
            return;

        sub.Detached = true;
        try
        {
            if (sub.Root is not null)
            {
                if (sub.ThemeChanged is not null)
                    sub.Root.ActualThemeChanged -= sub.ThemeChanged;
                if (sub.Loaded is not null)
                    sub.Root.Loaded -= sub.Loaded;
            }

            if (sub.Activated is not null)
                window.Activated -= sub.Activated;
            if (sub.ClosedHandler is not null)
                window.Closed -= sub.ClosedHandler;
        }
        catch (Exception ex) when (ex.HResult == ClosedWindowHResult)
        {
            // Fenêtre déjà morte : le détachement n'est plus possible. ApplyTitleBar a journalisé.
        }
    }

    private static bool SystemUsesLightTheme()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }
}
