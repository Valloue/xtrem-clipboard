using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using XtremClipboard.Models;
using XtremClipboard.Services;
using XtremClipboard.Services.Native;
using XtremClipboard.ViewModels;

namespace XtremClipboard;

/// <summary>
/// Fenêtre Réglages. La fermeture la cache ; seul Quitter (tray) arrête l'app.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly HotkeyCaptureListener _capture;
    private readonly IWinVSuppressService _winV;
    private readonly ILogger<MainWindow> _logger;

    public MainViewModel ViewModel { get; }

    /// <summary>Bandeau mise à jour (D-UPDATE) — InfoBar en haut de la fenêtre.</summary>
    public UpdateBannerViewModel Banner { get; }

    public bool AllowClose { get; set; }

    public MainWindow(
        MainViewModel viewModel,
        HotkeyCaptureListener capture,
        IWinVSuppressService winV,
        ILogger<MainWindow> logger,
        UpdateBannerViewModel updateBanner)
    {
        ViewModel = viewModel;
        Banner = updateBanner;
        _capture = capture;
        _winV = winV;
        _logger = logger;
        InitializeComponent();

        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 860));
        Title = "Xtrem Clipboard — Réglages";
        AppIcon.Apply(AppWindow);
        WindowTheme.Track(this);
        AppWindow.Closing += OnClosing;
    }

    private void OnClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        ReleaseCaptureHook();
        ViewModel.CancelHotkeyCapture();
        if (AllowClose)
        {
            _logger.LogInformation("Fenêtre Réglages fermée (quitter autorisé)");
            return;
        }

        _logger.LogInformation("Fermeture Réglages → fenêtre cachée (pas d'arrêt)");
        args.Cancel = true;
        var hwnd = WindowNative.GetWindowHandle(this);
        NativeMethods.ShowWindow(hwnd, 0);
    }

    private void EditHotkey_Click(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("Capture raccourci démarrée (Réglages)");
        ViewModel.BeginHotkeyCapture();
        _winV.Pause();
        _capture.Captured -= OnCaptured;
        _capture.Captured += OnCaptured;
        if (!_capture.IsListening)
            _capture.Start();
    }

    private void CancelHotkey_Click(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("Capture raccourci annulée (Réglages)");
        ReleaseCaptureHook();
        ViewModel.CancelHotkeyCapture();
    }

    private void OnCaptured(object? sender, HotkeyCaptureEventArgs e)
    {
        ReleaseCaptureHook();
        ViewModel.ApplyCapturedHotkey(e.Ctrl, e.Alt, e.Shift, e.Win, e.Key);
    }

    private void ReleaseCaptureHook()
    {
        _capture.Captured -= OnCaptured;
        _capture.Stop();
        _winV.Resume();
    }

    private void RemoveExclusion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ExclusionEntry entry })
            ViewModel.RemoveExclusion(entry);
    }

    // Bouton X du bandeau = Repousser (jusqu'au prochain démarrage, fichier en cache)
    private void UpdateBar_CloseClicked(InfoBar sender, object args)
    {
        Banner.PostponeCommand.Execute(null);
    }

    private void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        Banner.ApplyCommand.Execute(null);
    }

    private async void PickExclusion_Click(object sender, RoutedEventArgs e)
    {
        var apps = ViewModel.ListVisibleApps();
        if (apps.Count == 0)
            return;

        _logger.LogInformation("Dialogue exclusion ouvert ({Count} app(s) visibles)", apps.Count);
        var list = new ListView
        {
            ItemsSource = apps,
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 360,
            Width = 420
        };
        if (Application.Current.Resources.TryGetValue("SolidBackgroundFillColorBaseBrush", out var brush) && brush is Brush fill)
            list.Background = fill;
        list.SelectedIndex = 0;

        var dialog = new ContentDialog
        {
            Title = "Exclure une application",
            Content = list,
            PrimaryButtonText = "Exclure",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && list.SelectedItem is VisibleWindowApp app)
        {
            _logger.LogInformation("Dialogue exclusion : {Process} choisi", app.ProcessName);
            ViewModel.AddVisibleApp(app);
        }
        else
        {
            _logger.LogDebug("Dialogue exclusion annulé");
        }
    }
}
