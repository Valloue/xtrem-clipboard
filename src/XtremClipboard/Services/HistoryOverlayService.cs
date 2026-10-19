using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;
using XtremClipboard.Services.Native;
using XtremClipboard.ViewModels;
using XtremClipboard.Views;

namespace XtremClipboard.Services;

/// <summary>
/// Popup historique topmost style Win+V (**D-UI-OVERLAY-TRAY**).
/// </summary>
public sealed class HistoryOverlayService : IHistoryOverlayService
{
    private readonly IServiceProvider _services;
    private readonly IPasteService _paste;
    private readonly ILogger<HistoryOverlayService> _logger;
    private HistoryOverlayWindow? _window;
    private bool _closingFromUs;

    public bool IsVisible { get; private set; }

    public HistoryOverlayService(
        IServiceProvider services,
        IPasteService paste,
        ILogger<HistoryOverlayService> logger)
    {
        _services = services;
        _paste = paste;
        _logger = logger;
    }

    public void Show()
    {
        // Capturer la fenêtre cible AVANT d'activer l'overlay
        _paste.TargetHwnd = NativeMethods.GetForegroundWindow();

        EnsureWindow();
        PositionNearCursor();
        ApplyTopmostAndExcludeCapture();

        _window!.ViewModel.Refresh();
        _window.Activate();
        IsVisible = true;
        _logger.LogInformation("Overlay historique affiché");
    }

    public void Hide()
    {
        if (_window is null || !IsVisible)
            return;

        _closingFromUs = true;
        try
        {
            // Fermer la fenêtre (et non la masquer par SW_HIDE) : réafficher une fenêtre cachée
            // peut rendre son contenu entièrement noir après veille / UAC / changement d'écran
            // (surface de composition DWM perdue — seule la recréation réparait, d'où les
            // kill + relance). Une popup se recrée en quelques ms : surface neuve garantie.
            _window.Close();
            _window = null;
            _logger.LogInformation("Overlay historique fermé — fenêtre recréée au prochain affichage");
        }
        finally
        {
            _closingFromUs = false;
            IsVisible = false;
        }
    }

    public void Toggle()
    {
        if (IsVisible)
            Hide();
        else
            Show();
    }

    private void EnsureWindow()
    {
        if (_window is not null)
            return;

        var vm = _services.GetRequiredService<HistoryViewModel>();
        vm.RequestClose = Hide;
        _window = new HistoryOverlayWindow(vm);
        _window.AppWindow.IsShownInSwitchers = false;
        _window.AppWindow.TitleBar.ExtendsContentIntoTitleBar = false;

        _window.Activated += OnActivated;
        _window.Closed += (_, _) =>
        {
            _window = null;
            IsVisible = false;
        };

        // Esc
        if (_window.Content is UIElement root)
        {
            var esc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
            esc.Invoked += (_, args) =>
            {
                Hide();
                args.Handled = true;
            };
            root.KeyboardAccelerators.Add(esc);
            root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        }

        // Esc aussi via KeyDown sur le contenu
        if (_window.Content is FrameworkElement fe)
        {
            fe.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    Hide();
                    e.Handled = true;
                }
            };
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated
            && !_closingFromUs
            && IsVisible)
        {
            // Clic extérieur → fermer (comportement style Win+V)
            Hide();
        }
    }

    private void PositionNearCursor()
    {
        if (_window is null)
            return;

        NativeMethods.GetCursorPos(out var pt);
        var monitor = NativeMethods.MonitorFromPoint(pt, 2); // MONITOR_DEFAULTTONEAREST
        var info = new NativeMethods.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            return;

        const int dipWidth = 400;
        const int dipHeight = 430;
        var scale = 1.0;
        if (NativeMethods.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX >= 96)
            scale = dpiX / 96.0;

        var width = (int)Math.Round(dipWidth * scale);
        var height = (int)Math.Round(dipHeight * scale);
        var work = info.Work;
        var maxX = Math.Max(work.Left + 8, work.Right - width - 8);
        var maxY = Math.Max(work.Top + 8, work.Bottom - height - 8);
        var x = Math.Clamp(pt.X - width / 2, work.Left + 8, maxX);
        var y = Math.Clamp(pt.Y - height - 16, work.Top + 8, maxY);

        var hwnd = WindowNative.GetWindowHandle(_window);
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HwndTopmost,
            x, y, width, height,
            NativeMethods.SwpShowwindow);

        try
        {
            _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
            _window.AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
        }
        catch
        {
            // SetWindowPos suffit
            _logger.LogDebug("Redimensionnement AppWindow ignoré — SetWindowPos a suffi");
        }
    }

    private void ApplyTopmostAndExcludeCapture()
    {
        if (_window is null)
            return;

        var hwnd = WindowNative.GetWindowHandle(_window);
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HwndTopmost,
            0, 0, 0, 0,
            NativeMethods.SwpNomove | NativeMethods.SwpNosize | NativeMethods.SwpShowwindow);

        try
        {
            NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WdaExcludeFromCapture);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WDA_EXCLUDEFROMCAPTURE non disponible");
        }

        // Compact presenter si possible
        try
        {
            if (_window.AppWindow.Presenter is OverlappedPresenter overlapped)
            {
                overlapped.IsResizable = true;
                overlapped.IsMaximizable = false;
                overlapped.IsMinimizable = false;
                overlapped.SetBorderAndTitleBar(true, true);
            }
        }
        catch (Exception ex)
        {
            // ignore
            _logger.LogDebug(ex, "Réglages presenter overlay ignorés");
        }
    }
}
