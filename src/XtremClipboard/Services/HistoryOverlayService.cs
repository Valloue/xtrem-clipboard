using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
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
    private const int ShowGraceMs = 400;
    private const int DipWidth = 400;
    private const int DipHeight = 430;

    private readonly ILogger<HistoryOverlayService> _logger;
    private HistoryOverlayWindow? _window;
    private bool _closingFromUs;
    private bool _hideInProgress;
    private long _ignoreDeactivateUntil;
    private int _showGeneration;
    private int _outsideClosePosted;
    private DispatcherQueue? _uiDispatcher;
    private NativeMethods.LowLevelMouseProc? _mouseProc;
    private IntPtr _mouseHook;
    private IntPtr _overlayHwnd;

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
        Interlocked.Increment(ref _showGeneration);
        try
        {
            // Capturer la fenêtre cible AVANT d'activer l'overlay
            _paste.TargetHwnd = NativeMethods.GetForegroundWindow();

            EnsureWindow();
            PositionNearCursor();
            ApplyTopmostAndExcludeCapture();

            _window!.ViewModel.Refresh();
            // Relâcher Win désactive la fenêtre dans la même seconde : ne pas la fermer pour ça.
            _ignoreDeactivateUntil = Environment.TickCount64 + ShowGraceMs;
            IsVisible = true;
            // Posé avant Activate : un clic pendant la grâce est vu, mais ignoré jusqu'à l'échéance.
            ArmOutsideClickWatch();
            _window.Activate();

            var hwnd = WindowNative.GetWindowHandle(_window);
            if (!NativeMethods.SetForegroundWindow(hwnd))
            {
                // Tout de suite : un P/Invoke plus tard écraserait le code de cet appel.
                var code = Marshal.GetLastWin32Error();
                _logger.LogWarning(
                    "SetForegroundWindow a échoué ({Code}) {EventCode} {Category} {Result}",
                    code,
                    "CLIP-UI-FOREGROUND",
                    "erreur",
                    "degrade");
            }

            _logger.LogInformation("Overlay historique affiché");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Affichage overlay impossible {EventCode} {Category} {Result}",
                "CLIP-UI-SHOW",
                "erreur",
                "echec");
            CloseCreatedWindow();
        }
    }

    public void Hide()
    {
        if (_hideInProgress || _window is null || !IsVisible)
            return;

        _hideInProgress = true;
        _closingFromUs = true;
        // Avant Close : la pompe de fermeture ne doit plus voir le hook.
        DisarmOutsideClickWatch();
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
            _hideInProgress = false;
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
            DisarmOutsideClickWatch();
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
            if (Environment.TickCount64 < _ignoreDeactivateUntil)
            {
                _logger.LogInformation(
                    "fermeture ignorée (grâce affichage) {EventCode} {Category} {Result}",
                    "CLIP-UI-GRACE",
                    "usage",
                    "ok");
                return;
            }

            // Clic extérieur pendant que la fenêtre a encore le focus.
            Hide();
        }
    }

    /// <summary>
    /// La grâce avale le premier <c>Deactivated</c>. La fenêtre reste visible sans le focus,
    /// et le clic dehors suivant ne produit plus cet événement. Le hook ne consomme pas le clic.
    /// </summary>
    private void ArmOutsideClickWatch()
    {
        _uiDispatcher ??= DispatcherQueue.GetForCurrentThread();
        if (_window is null)
            return;

        _overlayHwnd = WindowNative.GetWindowHandle(_window);
        if (_uiDispatcher is null)
        {
            _logger.LogError(
                "Surveillance du clic dehors sans file UI {EventCode} {Category} {Result}",
                "CLIP-UI-ENQUEUE",
                "erreur",
                "echec");
        }

        if (_mouseHook != IntPtr.Zero)
            return;

        _mouseProc ??= OnOutsideClick;
        var module = ResolveHookModule();
        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhMouseLl,
            _mouseProc,
            module,
            0);
        if (_mouseHook != IntPtr.Zero)
            return;

        var code = Marshal.GetLastWin32Error();
        _logger.LogError(
            "Surveillance du clic dehors indisponible ({Code}) {EventCode} {Category} {Result}",
            code,
            "CLIP-UI-OUTSIDE",
            "erreur",
            "echec");
    }

    private void DisarmOutsideClickWatch()
    {
        _overlayHwnd = IntPtr.Zero;
        if (_mouseHook == IntPtr.Zero)
            return;

        var hook = _mouseHook;
        _mouseHook = IntPtr.Zero;
        if (NativeMethods.UnhookWindowsHookEx(hook))
            return;

        var code = Marshal.GetLastWin32Error();
        _logger.LogWarning(
            "Retrait du hook souris impossible ({Code}) {EventCode} {Category} {Result}",
            code,
            "CLIP-UI-OUTSIDE",
            "erreur",
            "degrade");
    }

    private IntPtr OnOutsideClick(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsVisible && IsMouseButtonDown(wParam))
        {
            try
            {
                NoteOutsideClick(lParam);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Clic dehors illisible {EventCode} {Category} {Result}",
                    "CLIP-UI-OUTSIDE",
                    "erreur",
                    "echec");
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void NoteOutsideClick(IntPtr lParam)
    {
        // Pendant la grâce : le relâchement de Win et le premier clic ne ferment pas.
        if (_overlayHwnd == IntPtr.Zero || Environment.TickCount64 < _ignoreDeactivateUntil)
            return;

        var info = Marshal.PtrToStructure<NativeMethods.MsLlHookStruct>(lParam);
        if (IsInsideOverlay(info.pt))
            return;

        var generation = _showGeneration;
        if (Interlocked.CompareExchange(ref _outsideClosePosted, 1, 0) != 0)
            return;

        var dispatcher = _uiDispatcher;
        if (dispatcher is not null && dispatcher.TryEnqueue(() => CloseForOutsideClick(generation)))
            return;

        Interlocked.Exchange(ref _outsideClosePosted, 0);
        _logger.LogError(
            "TryEnqueue fermeture clic dehors a échoué {EventCode} {Category} {Result}",
            "CLIP-UI-ENQUEUE",
            "erreur",
            "echec");
    }

    private void CloseForOutsideClick(int generation)
    {
        try
        {
            if (generation != _showGeneration || !IsVisible)
                return;
            if (Environment.TickCount64 < _ignoreDeactivateUntil)
                return;

            _logger.LogInformation(
                "fermeture clic dehors {EventCode} {Category} {Result}",
                "CLIP-UI-OUTSIDE",
                "usage",
                "ok");
            Hide();
        }
        finally
        {
            Interlocked.Exchange(ref _outsideClosePosted, 0);
        }
    }

    private bool IsInsideOverlay(NativeMethods.Point pt)
    {
        var hwnd = _overlayHwnd;
        if (hwnd == IntPtr.Zero)
            return true;

        if (NativeMethods.GetWindowRect(hwnd, out var rect)
            && pt.X >= rect.Left && pt.X < rect.Right
            && pt.Y >= rect.Top && pt.Y < rect.Bottom)
            return true;

        var hit = NativeMethods.WindowFromPoint(pt);
        if (hit == IntPtr.Zero)
            return false;
        if (hit == hwnd)
            return true;
        if (NativeMethods.GetAncestor(hit, NativeMethods.GaRoot) == hwnd)
            return true;
        return NativeMethods.GetAncestor(hit, NativeMethods.GaRootOwner) == hwnd;
    }

    private static bool IsMouseButtonDown(IntPtr wParam)
    {
        var msg = (uint)wParam.ToInt32();
        return msg is NativeMethods.WmLbuttonDown
            or NativeMethods.WmRbuttonDown
            or NativeMethods.WmMbuttonDown
            or NativeMethods.WmXbuttonDown;
    }

    private static IntPtr ResolveHookModule()
    {
        using var proc = Process.GetCurrentProcess();
        var main = proc.MainModule?.ModuleName;
        var module = NativeMethods.GetModuleHandle(main);
        if (module == IntPtr.Zero)
            module = NativeMethods.GetModuleHandle(null);
        if (module == IntPtr.Zero)
            module = NativeMethods.GetModuleHandle("user32.dll");
        return module;
    }

    private void PositionNearCursor()
    {
        if (_window is null)
            return;

        NativeMethods.GetCursorPos(out var pt);
        const uint nearest = 2;
        const uint primary = 1;
        var monitor = NativeMethods.MonitorFromPoint(pt, nearest);
        if (!TryGetWorkArea(monitor, out var work))
        {
            _logger.LogWarning(
                "GetMonitorInfo a échoué — repli écran du curseur puis écran principal {EventCode} {Category} {Result}",
                "CLIP-UI-MONITOR",
                "erreur",
                "degrade");
            monitor = NativeMethods.MonitorFromPoint(pt, primary);
            if (!TryGetWorkArea(monitor, out work))
            {
                var screenW = NativeMethods.GetSystemMetrics(0);
                var screenH = NativeMethods.GetSystemMetrics(1);
                if (screenW <= 0) screenW = 1920;
                if (screenH <= 0) screenH = 1080;
                work = new NativeMethods.Rect { Left = 0, Top = 0, Right = screenW, Bottom = screenH };
                monitor = NativeMethods.MonitorFromPoint(new NativeMethods.Point { X = 1, Y = 1 }, primary);
            }
        }

        var scale = 1.0;
        if (monitor != IntPtr.Zero
            && NativeMethods.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0
            && dpiX >= 96)
            scale = dpiX / 96.0;

        var width = (int)Math.Round(DipWidth * scale);
        var height = (int)Math.Round(DipHeight * scale);
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
        catch (Exception ex)
        {
            // SetWindowPos suffit
            _logger.LogDebug(
                ex,
                "Redimensionnement AppWindow ignoré — SetWindowPos a suffi {EventCode} {Category} {Result}",
                "CLIP-UI-RESIZE",
                "erreur",
                "degrade");
        }
    }

    private static bool TryGetWorkArea(IntPtr monitor, out NativeMethods.Rect work)
    {
        work = default;
        if (monitor == IntPtr.Zero)
            return false;

        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            return false;

        work = info.Work;
        return work.Right > work.Left && work.Bottom > work.Top;
    }

    /// <summary>Ferme une fenêtre créée si Show a jeté, pour que le Toggle suivant rouvre.</summary>
    private void CloseCreatedWindow()
    {
        var window = _window;
        _window = null;
        IsVisible = false;
        _ignoreDeactivateUntil = 0;
        DisarmOutsideClickWatch();
        if (window is null)
            return;

        _closingFromUs = true;
        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fermeture de l'overlay après échec d'affichage");
        }
        finally
        {
            _closingFromUs = false;
            _window = null;
            IsVisible = false;
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
