using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Serilog;
using WinRT.Interop;
using XtremClipboard.Models;
using XtremClipboard.Services;
using XtremClipboard.Services.Logging;
using XtremClipboard.Services.Native;
using XtremClipboard.ViewModels;
using XtremClipboard.Views;

namespace XtremClipboard;

/// <summary>
/// Point d'entrée WinUI — DI, config post-install, overlay + tray, Win+V.
/// </summary>
public partial class App : Application
{
    private Window? _settingsWindow;
    private DispatcherQueue? _uiDispatcher;
    private SingleInstanceGuard? _instance;
    private bool _hotkeyRouted;
    private ILogger<App> _log = null!;
    private SessionTracker? _session;
    private HostShutdownHook? _shutdownHook;
    private IUpdateService? _updates;
    private UpdateBannerViewModel? _updateBanner;

    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Services = ConfigureServices();
            _log = Services.GetRequiredService<ILogger<App>>();

            _instance = Services.GetRequiredService<SingleInstanceGuard>();
            if (!_instance.IsPrimary)
            {
                // Pas d'Open : le marqueur et le drapeau restent ceux de la première.
                // Close écrit la raison et le code, puis vide le journal de cette instance.
                _instance.SignalPeer();
                var tracker = _session ?? throw new InvalidOperationException("Journal non initialisé.");
                tracker.Close("Seconde instance", 0);
                Environment.Exit(0);
                return;
            }

            _instance.PeerActivated += (_, _) => Dispatch(OnPeerActivated);

            _uiDispatcher = DispatcherQueue.GetForCurrentThread();

            // Après le rejet de la seconde instance, pour ne pas poser son drapeau crash
            // sur le marqueur de la première.
            var session = _session ?? throw new InvalidOperationException("Journal non initialisé.");
            CrashHandlers.Install(Log.Logger, session);
            session.Open();
            Breadcrumbs.Add("démarrage");
            _log.LogInformation(
                "Xtrem Clipboard démarré (v{Version}+{Commit}) {EventCode} {Category} {Result}",
                session.Version,
                session.Commit,
                "CLIP-LIFE-START",
                "cycle",
                "ok");
            LogSystemContext();

            if (AppDataPaths.MigrationError is not null)
                _log.LogWarning(AppDataPaths.MigrationError,
                    "Migration depuis l'ancien dossier incomplète ({Moved} élément(s) déplacé(s)) — dossier neuf utilisé",
                    AppDataPaths.MovedItems);

            var winHist = Services.GetRequiredService<IWindowsClipboardHistoryService>();
            winHist.TryDisableWindowsClipboardHistory(out var histMsg);
            _log.LogInformation("Disable Win clipboard: {Msg}", histMsg);

            winHist.TryReleaseWinVFromExplorer(out var winVMsg);
            _log.LogInformation("Release Win+V: {Msg}", winVMsg);

            var settings = Services.GetRequiredService<ISettingsService>();
            if (settings.Current.IsSetupCompleted)
            {
                // BUG-5 testeur : armer store + écoute presse-papiers LE PLUS TÔT possible,
                // avant les opérations lourdes (fenêtre, tray, hotkeys) — toute copie faite
                // pendant l'init est alors capturée (ou rattrapée en fin d'init).
                StartClipboardPipeline();
                StartConfiguredApp(settings);
            }
            else
            {
                ShowFirstConfig();
            }

            try
            {
                winHist.RestartClipboardUiHosts();
                var winV = Services.GetRequiredService<IWinVSuppressService>();
                winV.WinVPressed += (_, _) => Dispatch(ShowHistoryOverlay);
                winV.Start();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Impossible d'activer l'interception Win+V");
            }

            // Rattrapage unique (BUG-5) : une copie faite pendant l'initialisation peut
            // avoir échappé à la capture — relecture du presse-papiers courant, sans
            // doublon (dédup par hash côté store).
            CaptureStartupBacklog();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Échec fatal au lancement");
            if (_session is { IsOpen: true })
                _session.Close("Échec lancement", 1);
            else
                CrashHandlers.OnProcessExit();
            throw;
        }
    }

    /// <summary>Une seule ligne système : disque, écrans, GPU. Pas de chemin ni de nom de machine.</summary>
    private void LogSystemContext()
    {
        try
        {
            var gpu = GpuProbe.TryDescribe(Log.Logger);
            var displays = DisplaySummary.Describe(Log.Logger);
            SystemContext.LogAtStartup(Log.Logger, AppDataPaths.Root, displays, gpu);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Contexte système indisponible {EventCode} {Category} {Result}",
                "CLIP-SYS-002",
                "system",
                "degrade");
        }
    }

    private void AttachShutdownHook(IntPtr hwnd)
    {
        _shutdownHook?.Dispose();
        _shutdownHook = HostShutdownHook.TryAttach(hwnd, OnWindowsShutdown);
        if (_shutdownHook is null)
        {
            _log.LogWarning(
                "Accroche arrêt Windows indisponible {EventCode} {Category} {Result}",
                "CLIP-LIFE-WIN",
                "cycle",
                "degrade");
        }
    }

    private void OnWindowsShutdown()
    {
        try
        {
            Breadcrumbs.Add("arrêt Windows");
            _log.LogInformation(
                "Arrêt Windows {EventCode} {Category} {Result}",
                "CLIP-LIFE-WIN",
                "cycle",
                "arret");
            if (_session is { IsOpen: true })
                _session.Close("Arrêt Windows", 0);
        }
        catch
        {
            // Filet anti-boucle pendant l'arrêt de la session Windows.
        }
    }

    private void StartConfiguredApp(ISettingsService settings)
    {
        // Idempotent : déjà armé tôt dans OnLaunched pour le chemin configuré ;
        // appel effectif ici pour la transition FirstConfig → app configurée.
        StartClipboardPipeline();
        var history = Services.GetRequiredService<HistoryViewModel>();
        history.RequestOpenSettings = () => Dispatch(ShowSettingsWindow);
        CreateHiddenSettingsHost();
        StartTray();
        WireHotkeyToOverlay();
        RestoreRegisteredHotkey(settings);
        Services.GetRequiredService<IStartupService>().Apply(settings.Current.StartWithWindows);
        _log.LogInformation("App configurée : tray + overlay + écoute");

        // Auto-update (D-UPDATE) : check versions.json au démarrage, bandeau + download en fond.
        _updates = Services.GetRequiredService<IUpdateService>();
        _updates.CleanExitRequested = () => Dispatch(() => ExitApp("mise à jour lancée"));
        _updateBanner = Services.GetRequiredService<UpdateBannerViewModel>();
        _ = Task.Run(() => _updateBanner.PrepareAsync());

        // Chemin FirstConfig : l'init s'achève ici → rattrapage presse-papiers
        // (le chemin configuré le fait en fin de OnLaunched, après tout le setup).
        CaptureStartupBacklog();
    }

    /// <summary>
    /// Rattrapage post-initialisation (BUG-5 testeur) : relit une fois le presse-papiers
    /// courant pour ne rater aucune copie faite pendant l'init. Sans doublon : la dédup
    /// par hash du store ignore le contenu déjà présent en tête d'historique.
    /// </summary>
    private void CaptureStartupBacklog()
    {
        try
        {
            var listener = Services.GetRequiredService<IClipboardListener>();
            if (!listener.IsListening)
                return;

            _log.LogInformation("Rattrapage presse-papiers post-initialisation");
            listener.CaptureOnce();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Rattrapage presse-papiers impossible");
        }
    }

    private void OnPeerActivated()
    {
        _log.LogInformation("Réveil demandé par une seconde instance");
        var settings = Services.GetRequiredService<ISettingsService>();
        if (settings.Current.IsSetupCompleted)
            ShowHistoryOverlay();
        else if (_settingsWindow is not null)
            ShowSettingsWindow();
    }

    private void CreateHiddenSettingsHost()
    {
        var window = new MainWindow(
            Services.GetRequiredService<MainViewModel>(),
            Services.GetRequiredService<HotkeyCaptureListener>(),
            Services.GetRequiredService<IWinVSuppressService>(),
            Services.GetRequiredService<ILogger<MainWindow>>(),
            Services.GetRequiredService<UpdateBannerViewModel>());
        _settingsWindow = window;
        window.Activate();

        // Host pour tray / process — pas le shell historique
        var hwnd = WindowNative.GetWindowHandle(window);
        NativeMethods.ShowWindow(hwnd, 0);
        window.AppWindow.IsShownInSwitchers = true;
        AttachShutdownHook(hwnd);
    }

    private void StartTray()
    {
        if (_settingsWindow is null)
            return;

        var hwnd = WindowNative.GetWindowHandle(_settingsWindow);
        var tray = Services.GetRequiredService<ITrayIconService>();
        tray.OpenHistoryRequested += (_, _) => Dispatch(ShowHistoryOverlay);
        tray.OpenSettingsRequested += (_, _) => Dispatch(ShowSettingsWindow);
        tray.ExitRequested += (_, _) => Dispatch(() => ExitApp("menu tray"));
        tray.Start(hwnd);
    }

    private void ShowHistoryOverlay()
    {
        try
        {
            _log.LogInformation("Overlay historique demandé (raccourci / tray / réveil)");
            Breadcrumbs.Add("overlay ouvert");
            Services.GetRequiredService<IHistoryOverlayService>().Toggle();
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Ouverture de l'overlay impossible {EventCode} {Category} {Result}",
                "CLIP-UI-OVERLAY",
                "erreur",
                "echec");
        }
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is null)
            return;

        _log.LogInformation("Réglages affichés (tray / engrenage overlay)");
        Breadcrumbs.Add("réglages affichés");
        var hwnd = WindowNative.GetWindowHandle(_settingsWindow);
        NativeMethods.ShowWindow(hwnd, 9);
        NativeMethods.SetForegroundWindow(hwnd);
        _settingsWindow.Activate();
        ForceRepaint(_settingsWindow);
    }

    /// <summary>
    /// Contournement du bug WinUI « fenêtre entièrement noire » : la surface de composition
    /// DWM peut être perdue (veille, UAC/bureau sécurisé, changement d'écran, pilote GPU).
    /// Un léger aller-retour de taille force WM_SIZE et une nouvelle présentation XAML —
    /// sans tuer ni relancer le process. MainWindow est conservée (hôte du tray), on ne
    /// peut pas la recréer comme l'overlay.
    /// </summary>
    private void ForceRepaint(Window window)
    {
        try
        {
            var size = window.AppWindow.Size;
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(size.Width + 1, size.Height));
            window.AppWindow.Resize(size);
            _log.LogDebug("Repaint forcé sur ré-affichage (anti-écran noir)");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Forçage de repaint ignoré");
        }
    }

    private void ExitApp(string reason = "menu tray")
    {
        _log.LogInformation("Fermeture demandée ({Reason})", reason);
        Breadcrumbs.Add("fermeture");
        try
        {
            if (_settingsWindow is MainWindow main)
                main.AllowClose = true;
            Services.GetRequiredService<ITrayIconService>().Dispose();
            Services.GetRequiredService<IHistoryOverlayService>().Hide();
            _instance?.Dispose();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Nettoyage incomplet à la fermeture — sortie quand même");
        }

        if (_session is { IsOpen: true })
            _session.Close("Quitter (" + reason + ")", 0);
        else
            CrashHandlers.OnProcessExit();
        Exit();
    }

    private void WireHotkeyToOverlay()
    {
        if (_hotkeyRouted)
            return;
        _hotkeyRouted = true;
        var hotkeys = Services.GetRequiredService<IHotkeyService>();
        hotkeys.HotkeyPressed += (_, _) => Dispatch(ShowHistoryOverlay);
    }

    private void RestoreRegisteredHotkey(ISettingsService settings)
    {
        if (!HotkeyGesture.TryParse(settings.Current.Hotkey, out var gesture) || gesture is null)
            return;

        if (gesture.IsWinV)
        {
            _log.LogInformation("Hotkey settings = Win+V (WinVSuppress → overlay)");
            return;
        }

        var hotkeys = Services.GetRequiredService<IHotkeyService>();
        if (!hotkeys.TryRegister(gesture, out var error))
            _log.LogWarning("Impossible de restaurer le raccourci {Hotkey} : {Error}", gesture, error);
        else
            _log.LogInformation("Raccourci restauré : {Hotkey}", gesture);
    }

    private void Dispatch(Action action)
    {
        if (_uiDispatcher is null || _uiDispatcher.HasThreadAccess)
            action();
        else if (!_uiDispatcher.TryEnqueue(() => action()))
            _log.LogError(
                "TryEnqueue a échoué {EventCode} {Category} {Result}",
                "CLIP-UI-ENQUEUE",
                "erreur",
                "echec");
    }

    private void ShowFirstConfig()
    {
        _log.LogInformation("Ouverture configuration initiale (post-install)");
        Breadcrumbs.Add("configuration initiale");
        var vm = Services.GetRequiredService<FirstConfigViewModel>();
        var window = new FirstConfigWindow(
            vm,
            Services.GetRequiredService<HotkeyCaptureListener>(),
            Services.GetRequiredService<IWinVSuppressService>(),
            Services.GetRequiredService<ILogger<FirstConfigWindow>>());
        vm.SetupCompleted += (_, _) =>
        {
            try
            {
                var settings = Services.GetRequiredService<ISettingsService>();
                StartConfiguredApp(settings);
                _log.LogInformation("Shell tray/overlay après activation");
                window.Close();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Échec bascule FirstConfig → app configurée");
            }
        };
        _settingsWindow = window;
        window.Activate();
        AttachShutdownHook(WindowNative.GetWindowHandle(window));
    }

    private bool _pipelineStarted;

    /// <summary>
    /// Démarre store + écoute presse-papiers (idempotent). Appelé le plus tôt possible
    /// au lancement (BUG-5 testeur : armer l'écoute avant l'init lourde).
    /// </summary>
    private void StartClipboardPipeline()
    {
        if (_pipelineStarted)
            return;
        _pipelineStarted = true;

        try
        {
            var store = Services.GetRequiredService<IClipboardHistoryStore>();
            store.Load();
            store.ApplyRetention();

            var listener = Services.GetRequiredService<IClipboardListener>();
            if (!listener.IsListening)
                listener.Start();

            _log.LogInformation("Pipeline clipboard démarré (store + listener)");
        }
        catch (Exception ex)
        {
            _pipelineStarted = false; // nouvelle tentative possible (fin de FirstConfig)
            _log.LogError(ex, "Échec démarrage pipeline clipboard");
        }
    }

    private ServiceProvider ConfigureServices()
    {
        AppDataPaths.EnsureMigrated();
        var logging = LogBootstrap.Configure(Path.Combine(AppDataPaths.Root, "logs"));
        _session = logging.Session;
        Log.Logger = logging.Logger;

        Log.Information("Journal initialisé — rotation quotidienne, 14 fichiers conservés");

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(Log.Logger, dispose: true);
        });

        services.AddSingleton<SingleInstanceGuard>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<UpdateBannerViewModel>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IOcrService, OcrService>();
        services.AddSingleton<IHotkeyService, HotkeyService>();
        services.AddSingleton<IWindowsClipboardHistoryService, WindowsClipboardHistoryService>();
        services.AddSingleton<IWinVSuppressService, WinVSuppressService>();
        services.AddSingleton<IClipboardHistoryStore, ClipboardHistoryStore>();
        services.AddSingleton<IAppExclusionService, AppExclusionService>();
        services.AddSingleton<IClipboardListener, ClipboardListener>();
        services.AddSingleton<IPasteService, PasteService>();
        services.AddSingleton<IHistoryOverlayService, HistoryOverlayService>();
        services.AddSingleton<ITrayIconService, TrayIconService>();
        services.AddSingleton<IStartupService, StartupService>();
        services.AddSingleton<HotkeyCaptureListener>();
        services.AddSingleton<HistoryViewModel>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<FirstConfigViewModel>();

        return services.BuildServiceProvider();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            CrashHandlers.HandleUiFault(_session, e.Exception, Log.Logger);
        }
        catch
        {
            // Filet anti-boucle : le journal ne doit pas relancer l'exception UI.
        }

        // Handled reste true : pas de chemin de sortie dédié, le tray ne meurt pas
        // sur un défaut XAML localisé (y compris 0x800710DD).
        e.Handled = true;
    }
}
