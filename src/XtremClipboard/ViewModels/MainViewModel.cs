using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Windows.System;
using XtremClipboard.Models;
using XtremClipboard.Services;
using XtremClipboard.Services.Logging;

namespace XtremClipboard.ViewModels;

/// <summary>
/// Réglages : raccourci, rétention, démarrage, pause, exclusions, vidage.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IClipboardHistoryStore _history;
    private readonly IHotkeyService _hotkeys;
    private readonly IStartupService _startup;
    private readonly IAppExclusionService _exclusions;
    private readonly ILogger<MainViewModel> _logger;
    private bool _suppressPersist;

    [ObservableProperty]
    private string _statusText = "Réglages enregistrés automatiquement.";

    [ObservableProperty]
    private string _hotkeyDisplay = "Aucun";

    [ObservableProperty]
    private bool _isCapturingHotkey;

    [ObservableProperty]
    private int _retentionModeIndex;

    [ObservableProperty]
    private double _retentionDays = 30;

    [ObservableProperty]
    private double _retentionMaxMegabytes = 500;

    [ObservableProperty]
    private double _retentionMaxEntries = 500;

    [ObservableProperty]
    private bool _startWithWindows = true;

    [ObservableProperty]
    private bool _capturePaused;

    [ObservableProperty]
    private string _historyStatusText = string.Empty;

    [ObservableProperty]
    private string _newProcessName = string.Empty;

    [ObservableProperty]
    private string _newTitleContains = string.Empty;

    public ObservableCollection<ExclusionEntry> Exclusions { get; } = new();

    public Visibility NoExclusionsVisibility => Exclusions.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility DaysVisibility => RetentionModeIndex == (int)RetentionMode.ByTime
        ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MegabytesVisibility => RetentionModeIndex == (int)RetentionMode.ByDiskSize
        ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EntriesVisibility => RetentionModeIndex == (int)RetentionMode.ByEntryCount
        ? Visibility.Visible : Visibility.Collapsed;

    public MainViewModel(
        ISettingsService settings,
        IClipboardHistoryStore history,
        IHotkeyService hotkeys,
        IStartupService startup,
        IAppExclusionService exclusions,
        ILogger<MainViewModel> logger)
    {
        _settings = settings;
        _history = history;
        _hotkeys = hotkeys;
        _startup = startup;
        _exclusions = exclusions;
        _logger = logger;

        _suppressPersist = true;
        var s = _settings.Current;
        HotkeyDisplay = string.IsNullOrWhiteSpace(s.Hotkey) ? "Aucun" : s.Hotkey;
        RetentionModeIndex = (int)s.RetentionMode;
        RetentionDays = s.RetentionDays;
        RetentionMaxMegabytes = s.RetentionMaxMegabytes;
        RetentionMaxEntries = s.RetentionMaxEntries;
        StartWithWindows = s.StartWithWindows;
        CapturePaused = s.CapturePaused;
        LoadExclusions(s);
        _suppressPersist = false;

        RefreshHistoryStatus();
        _history.HistoryChanged += (_, _) => RefreshHistoryStatus();
    }

    public void BeginHotkeyCapture()
    {
        _logger.LogInformation("Modification du raccourci démarrée (Réglages)");
        IsCapturingHotkey = true;
        StatusText = "Appuyez sur la combinaison (Ctrl, Alt, Shift ou Win + touche).";
    }

    public void CancelHotkeyCapture()
    {
        if (!IsCapturingHotkey)
            return;
        _logger.LogInformation("Modification du raccourci annulée (Réglages)");
        IsCapturingHotkey = false;
        StatusText = "Modification du raccourci annulée.";
    }

    public void ApplyCapturedHotkey(bool ctrl, bool alt, bool shift, bool win, VirtualKey key)
    {
        if (!IsCapturingHotkey)
            return;

        if (key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
            or VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            return;
        }

        if (!(ctrl || alt || shift || win))
        {
            StatusText = "Ajoutez au moins un modificateur.";
            return;
        }

        var gesture = new HotkeyGesture
        {
            Ctrl = ctrl,
            Alt = alt,
            Shift = shift,
            Win = win,
            Key = key
        };

        IsCapturingHotkey = false;
        HotkeyDisplay = gesture.ToDisplayString();
        _settings.Current.Hotkey = HotkeyDisplay;

        if (gesture.IsWinV)
        {
            _hotkeys.Unregister();
            _logger.LogInformation("Raccourci appliqué : {Gesture} (WinVSuppress)", HotkeyDisplay);
            StatusText = "Raccourci Win+V enregistré.";
        }
        else if (!_hotkeys.TryRegister(gesture, out var error))
        {
            StatusText = error ?? "Impossible d'enregistrer ce raccourci.";
            _logger.LogWarning("Raccourci réglages refusé : {Gesture}", HotkeyDisplay);
        }
        else
        {
            _logger.LogInformation("Raccourci appliqué : {Gesture}", HotkeyDisplay);
            StatusText = $"Raccourci {HotkeyDisplay} enregistré.";
        }

        _ = _settings.SaveAsync();
    }

    partial void OnRetentionModeIndexChanged(int value)
    {
        if (!_suppressPersist)
            _logger.LogInformation("Mode de rétention modifié : {Mode}", (RetentionMode)Math.Clamp(value, 0, 2));
        OnPropertyChanged(nameof(DaysVisibility));
        OnPropertyChanged(nameof(MegabytesVisibility));
        OnPropertyChanged(nameof(EntriesVisibility));
        PersistLimits();
    }

    partial void OnRetentionDaysChanged(double value) => PersistLimits();

    partial void OnRetentionMaxMegabytesChanged(double value) => PersistLimits();

    partial void OnRetentionMaxEntriesChanged(double value) => PersistLimits();

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_suppressPersist)
            return;
        _logger.LogInformation("Réglage démarrage Windows : {Value}", value);
        _settings.Current.StartWithWindows = value;
        _startup.Apply(value);
        _settings.ScheduleSave();
        StatusText = value
            ? "Démarrage avec Windows activé."
            : "Démarrage avec Windows désactivé.";
    }

    partial void OnCapturePausedChanged(bool value)
    {
        if (_suppressPersist)
            return;
        _logger.LogInformation("Réglage pause capture : {Value}", value);
        _settings.Current.CapturePaused = value;
        _settings.ScheduleSave();
        StatusText = value
            ? "Capture en pause — les nouvelles copies ne sont pas enregistrées."
            : "Capture reprise.";
    }

    [RelayCommand]
    private void AddExclusion()
    {
        _logger.LogInformation(
            "Ajout d'exclusion demandé {EventCode} {Category} {Result}",
            "CLIP-EXCL-ADD",
            "usage",
            "demande");

        var name = _exclusions.NormalizeProcessName(NewProcessName);
        if (name is null)
        {
            StatusText = "Indiquez un nom de processus, par exemple Code.exe.";
            return;
        }

        var title = NewTitleContains.Trim();
        if (title.Length > 200)
            title = title[..200];

        if (!TryAdd(name, title))
            return;

        // Titre jamais journalisé (D-CLIP-PRIVACY) — présence seule
        _logger.LogInformation("Exclusion ajoutée : {Process} (filtre titre : {HasTitle})", name, title.Length > 0);
        Breadcrumbs.Add("exclusion ajoutée");
        NewProcessName = string.Empty;
        NewTitleContains = string.Empty;
    }

    public IReadOnlyList<VisibleWindowApp> ListVisibleApps()
    {
        var apps = _exclusions.ListVisibleApps();
        if (apps.Count == 0)
            StatusText = "Aucune fenêtre d'application détectée.";
        return apps;
    }

    public void AddVisibleApp(VisibleWindowApp app) => TryAdd(app.ProcessName, string.Empty);

    public void RemoveExclusion(ExclusionEntry entry)
    {
        if (!Exclusions.Remove(entry))
            return;

        _logger.LogInformation("Exclusion retirée : {Process}", entry.ProcessName);
        PersistExclusions();
        StatusText = $"{entry.ProcessName} retiré des exclusions.";
    }

    [RelayCommand]
    private void ClearUnpinned()
    {
        _logger.LogInformation(
            "Vidage de l'historique demandé {EventCode} {Category} {Result}",
            "CLIP-HIST-CLEAR",
            "usage",
            "demande");
        Breadcrumbs.Add("historique vidé");
        var removed = _history.ClearUnpinned();
        _logger.LogInformation("Vider l'historique demandé ({Removed} supprimée(s))", removed);
        StatusText = removed == 0
            ? "Rien à vider (ou seulement des entrées épinglées)."
            : $"{removed} entrée(s) supprimée(s). Les épinglées sont conservées.";
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        _logger.LogInformation(
            "Ouverture du dossier données demandée {EventCode} {Category} {Result}",
            "CLIP-SET-FOLDER",
            "usage",
            "demande");
        Breadcrumbs.Add("dossier données");
        try
        {
            Directory.CreateDirectory(_settings.DataDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _settings.DataDirectory,
                UseShellExecute = true
            });
            _logger.LogInformation("Dossier données ouvert (Explorer)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Impossible d'ouvrir le dossier données");
            StatusText = "Impossible d'ouvrir le dossier données.";
        }
    }

    private void PersistLimits()
    {
        if (_suppressPersist)
            return;

        _settings.Current.RetentionMode = (RetentionMode)Math.Clamp(RetentionModeIndex, 0, 2);
        _settings.Current.RetentionDays = Math.Max(1, (int)RetentionDays);
        _settings.Current.RetentionMaxMegabytes = Math.Max(1, (int)RetentionMaxMegabytes);
        _settings.Current.RetentionMaxEntries = Math.Max(1, (int)RetentionMaxEntries);
        _logger.LogDebug(
            "Rétention : mode={Mode}, jours={Days}, Mo={Mo}, entrées={Entries}",
            _settings.Current.RetentionMode,
            _settings.Current.RetentionDays,
            _settings.Current.RetentionMaxMegabytes,
            _settings.Current.RetentionMaxEntries);
        _settings.ScheduleSave();
        _history.ApplyRetention();
    }

    private void LoadExclusions(AppSettings settings)
    {
        Exclusions.Clear();
        foreach (var rule in settings.Exclusions)
        {
            if (rule is null)
                continue;
            var name = _exclusions.NormalizeProcessName(rule.ProcessName);
            if (name is null || _exclusions.IsSelfProcess(name))
                continue;

            var title = rule.TitleContains?.Trim() ?? string.Empty;
            if (title.Length > 200)
                title = title[..200];
            if (Exclusions.Any(e => SameRule(e, name, title)))
                continue;

            Exclusions.Add(new ExclusionEntry(name, title));
        }
    }

    private bool TryAdd(string processName, string title)
    {
        if (_exclusions.IsSelfProcess(processName))
        {
            StatusText = "Indiquez une autre application que Xtrem Clipboard.";
            return false;
        }

        if (_exclusions.IsBuiltInPasswordManager(processName))
        {
            StatusText = "Ce gestionnaire de mots de passe est déjà exclu en permanence.";
            return false;
        }

        if (Exclusions.Any(e => SameRule(e, processName, title)))
        {
            StatusText = string.IsNullOrEmpty(title)
                ? $"{processName} est déjà exclu."
                : $"{processName} est déjà exclu pour ce titre.";
            return false;
        }

        Exclusions.Add(new ExclusionEntry(processName, title));
        PersistExclusions();
        StatusText = string.IsNullOrEmpty(title)
            ? $"{processName} exclu. Les prochaines copies de cette application sont ignorées."
            : $"{processName} exclu lorsque le titre contient « {title} ».";
        return true;
    }

    private static bool SameRule(ExclusionEntry entry, string processName, string title) =>
        entry.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)
        && entry.TitleContains.Equals(title, StringComparison.OrdinalIgnoreCase);

    private void PersistExclusions()
    {
        if (_suppressPersist)
            return;

        _settings.Current.Exclusions = Exclusions
            .Select(e => new AppExclusionRule
            {
                ProcessName = e.ProcessName,
                TitleContains = e.TitleContains
            })
            .ToList();
        _ = _settings.SaveAsync();
        OnPropertyChanged(nameof(NoExclusionsVisibility));
    }

    private void RefreshHistoryStatus()
    {
        var count = _history.Entries.Count;
        var pinned = _history.Entries.Count(e => e.IsPinned);
        HistoryStatusText = count == 0
            ? "Historique vide."
            : $"{count} entrée(s), dont {pinned} épinglée(s).";
    }
}
