using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;
using XtremClipboard.Services;
using XtremClipboard.Services.Native;
using XtremClipboard.ViewModels;

namespace XtremClipboard.Views;

/// <summary>
/// Fenêtre de configuration initiale (post-install).
/// </summary>
public sealed partial class FirstConfigWindow : Window
{
    private readonly HotkeyCaptureListener _capture;
    private readonly IWinVSuppressService _winV;
    private readonly ILogger<FirstConfigWindow> _logger;
    private bool _fitted;

    public FirstConfigViewModel ViewModel { get; }

    public FirstConfigWindow(
        FirstConfigViewModel viewModel,
        HotkeyCaptureListener capture,
        IWinVSuppressService winV,
        ILogger<FirstConfigWindow> logger)
    {
        ViewModel = viewModel;
        _capture = capture;
        _winV = winV;
        _logger = logger;
        InitializeComponent();
        Title = "Xtrem Clipboard — Configuration";
        AppIcon.Apply(AppWindow);
        WindowTheme.Track(this);
        // Taille haute tout de suite : le calage DPI (FitToWorkArea) arrive à l'activation.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 1200));

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _capture.Captured += OnCaptured;
        Activated += OnActivated;
        Closed += OnClosed;
        _logger.LogInformation("Fenêtre configuration initiale ouverte");

        StartCaptureMode();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!_fitted)
        {
            _fitted = true;
            try
            {
                FitToWorkArea();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Calage DPI impossible — taille par défaut conservée");
                AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 1200));
            }
        }

        if (ViewModel.Step == 1 && !ViewModel.IsTestingHotkey)
            CaptureBorder.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Taille en DIP convertie selon le DPI de l'écran, plafonnée à la zone de travail.
    /// 640×620 px bruts coupait l'étape 2 dès 125 % d'échelle.
    /// </summary>
    private void FitToWorkArea()
    {
        const int dipWidth = 680;
        const int dipHeight = 780;

        var hwnd = WindowNative.GetWindowHandle(this);
        var monitor = NativeMethods.MonitorFromWindow(hwnd, 2);
        var scale = 1.0;
        if (monitor != IntPtr.Zero
            && NativeMethods.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0
            && dpiX >= 96)
        {
            scale = dpiX / 96.0;
        }

        var width = (int)Math.Round(dipWidth * scale);
        var height = (int)Math.Round(dipHeight * scale);

        var info = new NativeMethods.MonitorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };

        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            var workW = info.Work.Right - info.Work.Left;
            var workH = info.Work.Bottom - info.Work.Top;
            width = Math.Min(width, Math.Max(520, workW - 32));
            height = Math.Min(height, Math.Max(520, workH - 32));
            var x = info.Work.Left + Math.Max(0, (workW - width) / 2);
            var y = info.Work.Top + Math.Max(0, (workH - height) / 2);
            AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
        }

        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // Assistant abandonné en cours = re-joué au prochain démarrage (listelog §3.6)
        _logger.LogInformation(
            "Fenêtre configuration fermée (setup terminé : {Completed})",
            ViewModel.IsSetupCompleted);
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _capture.Captured -= OnCaptured;
        StopCaptureMode();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FirstConfigViewModel.Step)
            or nameof(FirstConfigViewModel.IsTestingHotkey))
        {
            if (e.PropertyName == nameof(FirstConfigViewModel.Step))
                StepScroller.ChangeView(null, 0, null, true);

            if (ViewModel.Step == 1 && !ViewModel.IsTestingHotkey)
                StartCaptureMode();
            else
                StopCaptureMode();
        }
    }

    private void StartCaptureMode()
    {
        _winV.Pause();
        if (!_capture.IsListening)
            _capture.Start();
        CaptureBorder.Focus(FocusState.Programmatic);
    }

    private void StopCaptureMode()
    {
        _capture.Stop();
        _winV.Resume();
    }

    private void OnCaptured(object? sender, HotkeyCaptureEventArgs e)
    {
        ViewModel.TryCaptureKey(e.Ctrl, e.Alt, e.Shift, e.Win, e.Key);
    }

    private void CaptureBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        CaptureBorder.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void CaptureBorder_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Fallback UI : GetAsyncKeyState pour Win (GetKeyStateForCurrentThread le rate souvent)
        var ctrl = IsDown(0x11) || IsDown(0xA2) || IsDown(0xA3);
        var alt = IsDown(0x12) || IsDown(0xA4) || IsDown(0xA5);
        var shift = IsDown(0x10) || IsDown(0xA0) || IsDown(0xA1);
        var win = IsDown(NativeMethods.VkLWin) || IsDown(NativeMethods.VkRWin);

        if (ViewModel.TryCaptureKey(ctrl, alt, shift, win, e.Key))
            e.Handled = true;
    }

    private static bool IsDown(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
}
