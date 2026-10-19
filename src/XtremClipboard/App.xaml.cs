using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Serilog;
using WinRT.Interop;
using XtremClipboard.Models;
using XtremClipboard.Services;
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
    private DateTimeOffset _sessionStarted;
    private string? _sessionMarkerPath;
    private bool _sessionEnded;
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

            // Intercepteurs de crash listelog §2.1 (hors Application.UnhandledException déjà branché)
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => EndSession("ProcessExit");

            _instance = Services.GetRequiredService<SingleInstanceGuard>();
            if (!_instance.IsPrimary)
            {
                _log.LogInformation("Seconde instance détectée — signal envoyé à la première, sortie");
                _instance.SignalPeer();
                EndSession("Seconde instance");
                Environment.Exit(0);
                return;
            }

            _instance.PeerActivated += (_, _) => Dispatch(OnPeerActivated);

            _uiDispatcher = DispatcherQueue.GetForCurrentThread();

            InitSessionMarker();
            _log.LogInformation("Xtrem Clipboard démarré (v{Version})",
                typeof(App).Assembly.GetName().Version?.ToString() ?? "?");
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
            if (!settings.Current.IsSetupCompleted)
                ShowFirstConfig();
            else
                StartConfiguredApp(settings);

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
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Échec fatal au lancement");
            EndSession("Échec lancement");
            throw;
        }
    }

    /// <summary>Marqueur de session : détecte une fin anormale au lancement suivant (listelog §2.10).</summary>
    private void InitSessionMarker()
    {
        try
        {
            _sessionMarkerPath = Path.Combine(AppDataPaths.Root, "logs", "session.marker");
            if (File.Exists(_sessionMarkerPath))
            {
                var previous = File.ReadAllText(_sessionMarkerPath).Trim();
                _log.LogWarning(
                    "Arrêt anormal détecté : la session précédente (démarrée {PreviousStart}) n'a pas fini proprement",
                    previous);
                File.Delete(_sessionMarkerPath);
            }

            _sessionStarted = DateTimeOffset.Now;
            File.WriteAllText(_sessionMarkerPath, _sessionStarted.ToString("O"));
            _log.LogInformation("Session ouverte — marqueur écrit, durée suivie jusqu'à l'arrêt");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Marqueur de session indisponible — détection d'arrêt anormal désactivée");
        }
    }

    /// <summary>Fin de session : retire le marqueur, trace la durée et vide le journal (listelog §3.2).</summary>
    private void EndSession(string reason)
    {
        if (_sessionEnded)
            return;

        _sessionEnded = true;
        try
        {
            if (_sessionMarkerPath is not null && File.Exists(_sessionMarkerPath))
                File.Delete(_sessionMarkerPath);
        }
        catch
        {
            // Le marqueur restant sera lu comme arrêt anormal au prochain lancement.
        }

        var duration = _sessionStarted == default
            ? 0
            : Math.Round((DateTimeOffset.Now - _sessionStarted).TotalSeconds);
        Log.Information("Session terminée ({Reason}, durée {Seconds} s)", reason, duration);
        Log.CloseAndFlush();
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        try
        {
            var exception = e.ExceptionObject as Exception
                ?? new Exception(e.ExceptionObject?.ToString() ?? "exception inconnue");
            Log.Fatal(exception, "Exception domaine non gérée (IsTerminating={Terminating})", e.IsTerminating);
            Log.CloseAndFlush();
        }
        catch
        {
            // Éviter une boucle fatale sur le logging
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            Log.Error(e.Exception, "Tâche non observée ({Count} exception(s))", e.Exception.InnerExceptions.Count);
            e.SetObserved();
        }
        catch
        {
            // Éviter une boucle fatale sur le logging
        }
    }

    /// <summary>Contexte système compact au démarrage (listelog §3.3) — aucune donnée personnelle.</summary>
    private void LogSystemContext()
    {
        try
        {
            var isAdmin = new System.Security.Principal.WindowsPrincipal(
                System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

            _log.LogInformation(
                "Système : {Os}, process {ProcArch}, .NET {NetVersion}, CPU {Cores} cœurs, RAM {RamMo} Mo, admin={Admin}, locale={Locale}",
                Environment.OSVersion.VersionString,
                Environment.Is64BitProcess ? "x64" : "x86",
                Environment.Version,
                Environment.ProcessorCount,
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024),
                isAdmin,
                System.Globalization.CultureInfo.CurrentCulture.Name);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Contexte système indisponible");
        }
    }

    private void StartConfiguredApp(ISettingsService settings)
    {
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
        _log.LogInformation("Overlay historique demandé (raccourci / tray / réveil)");
        Services.GetRequiredService<IHistoryOverlayService>().Toggle();
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is null)
            return;

        _log.LogInformation("Réglages affichés (tray / engrenage overlay)");
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

        EndSession("Quitter (" + reason + ")");
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
        else
            _uiDispatcher.TryEnqueue(() => action());
    }

    private void ShowFirstConfig()
    {
        _log.LogInformation("Ouverture configuration initiale (post-install)");
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
    }

    private void StartClipboardPipeline()
    {
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
            _log.LogError(ex, "Échec démarrage pipeline clipboard");
        }
    }

    private static ServiceProvider ConfigureServices()
    {
        AppDataPaths.EnsureMigrated();
        var logDir = Path.Combine(AppDataPaths.Root, "logs");
        Directory.CreateDirectory(logDir);

        // Session ID par lancement + App/Version en propriété globale (listelog §1.2, §1.3)
        var version = typeof(App).Assembly.GetName().Version?.ToString() ?? "?";
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("App", "XtremClipboard")
            .Enrich.WithProperty("Version", version)
            .Enrich.WithProperty("Session", Guid.NewGuid().ToString("N"))
            .WriteTo.File(
                Path.Combine(logDir, "xtrem-clipboard-.log"),
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext:l}: {Message:lj} {Properties:j}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

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
            Log.Fatal(e.Exception, "Exception non gérée UI");
        }
        catch
        {
            // Éviter une boucle fatale sur le logging
        }

        e.Handled = true;
    }
}
