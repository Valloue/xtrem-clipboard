using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using XtremClipboard.Models;
using XtremClipboard.Services;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;

namespace XtremClipboard.ViewModels;

/// <summary>
/// Configuration initiale post-install : étape 1 hotkey+test, étape 2 rétention, activation.
/// </summary>
public partial class FirstConfigViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IHotkeyService _hotkeys;
    private readonly IWinVSuppressService _winV;
    private readonly IWindowsClipboardHistoryService _windowsHistory;
    private readonly ILogger<FirstConfigViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;

    private HotkeyGesture? _gesture;
    private bool _awaitingHotkeyTest;
    private bool _suppressPersist;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Step1Visibility))]
    [NotifyPropertyChangedFor(nameof(Step2Visibility))]
    private int _step = 1;

    [ObservableProperty]
    private string _hotkeyDisplay = "Aucun raccourci";

    [ObservableProperty]
    private bool _isHotkeyCaptured;

    [ObservableProperty]
    private bool _isHotkeyTested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestingVisibility))]
    private bool _isTestingHotkey;

    [ObservableProperty]
    private string _statusMessage = "Choisissez un raccourci (ex. Ctrl+Shift+V ou Win+V). Vous devrez le refaire une fois pour le tester.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusSeverity))]
    private bool _isStatusError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DaysVisibility))]
    [NotifyPropertyChangedFor(nameof(MegabytesVisibility))]
    [NotifyPropertyChangedFor(nameof(EntriesVisibility))]
    private int _retentionModeIndex;

    [ObservableProperty]
    private double _retentionDays = 30;

    [ObservableProperty]
    private double _retentionMaxMegabytes = 500;

    [ObservableProperty]
    private double _retentionMaxEntries = 500;

    public Visibility Step1Visibility => Step == 1 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility Step2Visibility => Step == 2 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Vrai quand la config a été activée — pour le log d'abandon de l'assistant.</summary>
    public bool IsSetupCompleted => _settings.Current.IsSetupCompleted;

    public Visibility DaysVisibility => RetentionModeIndex == (int)RetentionMode.ByTime
        ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MegabytesVisibility => RetentionModeIndex == (int)RetentionMode.ByDiskSize
        ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EntriesVisibility => RetentionModeIndex == (int)RetentionMode.ByEntryCount
        ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TestingVisibility => IsTestingHotkey ? Visibility.Visible : Visibility.Collapsed;

    public InfoBarSeverity StatusSeverity =>
        IsStatusError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;

    public event EventHandler? SetupCompleted;

    public FirstConfigViewModel(
        ISettingsService settings,
        IHotkeyService hotkeys,
        IWinVSuppressService winV,
        IWindowsClipboardHistoryService windowsHistory,
        ILogger<FirstConfigViewModel> logger)
    {
        _settings = settings;
        _hotkeys = hotkeys;
        _winV = winV;
        _windowsHistory = windowsHistory;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _suppressPersist = true;
        RetentionDays = _settings.Current.RetentionDays;
        RetentionMaxMegabytes = _settings.Current.RetentionMaxMegabytes;
        RetentionMaxEntries = _settings.Current.RetentionMaxEntries;
        RetentionModeIndex = (int)_settings.Current.RetentionMode;

        if (HotkeyGesture.TryParse(_settings.Current.Hotkey, out var existing) && existing is not null)
        {
            _gesture = existing;
            HotkeyDisplay = existing.ToDisplayString();
            IsHotkeyCaptured = true;
            // Brouillon : pas encore re-testé cette session
            IsHotkeyTested = false;
            SetStatus($"Brouillon : {HotkeyDisplay}. Saisissez-le, puis refaites-le pour le tester.", error: false);
        }

        _suppressPersist = false;

        _hotkeys.HotkeyPressed += OnHotkeyPressed;
        _winV.WinVPressed += OnWinVPressed;
    }

    public bool TryCaptureKey(bool ctrl, bool alt, bool shift, bool win, VirtualKey key)
    {
        if (Step != 1 || IsTestingHotkey)
            return false;

        if (key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
            or VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            return false;
        }

        if (!(ctrl || alt || shift || win))
        {
            SetStatus("Ajoutez au moins un modificateur (Ctrl, Alt, Shift ou Win).", error: true);
            return false;
        }

        _gesture = new HotkeyGesture
        {
            Ctrl = ctrl,
            Alt = alt,
            Shift = shift,
            Win = win,
            Key = key
        };

        HotkeyDisplay = _gesture.ToDisplayString();
        IsHotkeyCaptured = true;
        IsHotkeyTested = false;
        _logger.LogInformation("Raccourci capturé : {Gesture} — test lancé", HotkeyDisplay);
        _hotkeys.Unregister();
        PersistDraft();
        StartHotkeyTest();
        return true;
    }

    private void StartHotkeyTest()
    {
        if (_gesture is null)
        {
            SetStatus("Capturez d'abord un raccourci (cliquez la zone puis appuyez sur les touches).", error: true);
            return;
        }

        if (_gesture.IsWinV)
        {
            _hotkeys.Unregister();
            IsHotkeyTested = false;
            IsTestingHotkey = true;
            _awaitingHotkeyTest = true;
            _winV.Resume();
            SetStatus("Raccourci Win+V pris. Refaites Win+V pour le tester.", error: false);
            return;
        }

        if (!_hotkeys.TryRegister(_gesture, out var error))
        {
            IsHotkeyTested = false;
            IsTestingHotkey = false;
            _awaitingHotkeyTest = false;
            SetStatus(error ?? "Échec enregistrement du raccourci.", error: true);
            return;
        }

        IsTestingHotkey = true;
        IsHotkeyTested = false;
        _awaitingHotkeyTest = true;
        SetStatus($"Raccourci {HotkeyDisplay} pris. Refaites-le pour le tester.", error: false);
    }

    [RelayCommand]
    private void CancelHotkeyTest()
    {
        _logger.LogInformation("Test de raccourci annulé (config initiale)");
        _awaitingHotkeyTest = false;
        IsTestingHotkey = false;
        _hotkeys.Unregister();
        SetStatus("Test annulé. Saisissez un raccourci : il faudra le refaire pour le tester.", error: false);
    }

    [RelayCommand]
    private void GoToStep2()
    {
        if (!IsHotkeyTested || _gesture is null)
        {
            SetStatus("Le raccourci doit être testé avec succès avant l'étape 2.", error: true);
            return;
        }

        _logger.LogInformation("Config initiale : passage à l'étape 2 (rétention)");
        Step = 2;
        SetStatus("Choisissez comment limiter l'historique, puis activez Xtrem Clipboard.", error: false);
        PersistDraft();
    }

    [RelayCommand]
    private void BackToStep1()
    {
        _logger.LogInformation("Config initiale : retour à l'étape 1 (raccourci)");
        Step = 1;
        SetStatus("Modifiez le raccourci si besoin, puis retestez-le.", error: false);
    }

    [RelayCommand]
    private async Task ActivateAsync()
    {
        if (_gesture is null || !IsHotkeyTested)
        {
            SetStatus("Configuration incomplète : revenez à l'étape 1.", error: true);
            return;
        }

        _logger.LogInformation("Activation demandée (config initiale)");

        ApplyDraftToSettings(setupCompleted: true);

        try
        {
            await _settings.SaveAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Échec sauvegarde settings à l'activation");
            SetStatus("Impossible d'enregistrer les réglages.", error: true);
            return;
        }

        if (!_gesture.IsWinV && !_hotkeys.IsRegistered)
            _hotkeys.TryRegister(_gesture, out _);

        _winV.Resume();
        _windowsHistory.TryDisableWindowsClipboardHistory(out var winMsg);
        _logger.LogInformation("Activation terminée. {WinMsg}", winMsg);

        SetupCompleted?.Invoke(this, EventArgs.Empty);
    }

    partial void OnRetentionModeIndexChanged(int value) => PersistDraft();

    partial void OnRetentionDaysChanged(double value) => PersistDraft();

    partial void OnRetentionMaxMegabytesChanged(double value) => PersistDraft();

    partial void OnRetentionMaxEntriesChanged(double value) => PersistDraft();

    private void PersistDraft()
    {
        if (_suppressPersist)
            return;

        ApplyDraftToSettings(setupCompleted: false);
        _settings.ScheduleSave();
    }

    private void ApplyDraftToSettings(bool setupCompleted)
    {
        _settings.Current.IsSetupCompleted = setupCompleted;
        if (_gesture is not null)
            _settings.Current.Hotkey = _gesture.ToDisplayString();

        _settings.Current.RetentionMode = (RetentionMode)Math.Clamp(RetentionModeIndex, 0, 2);
        _settings.Current.RetentionDays = Math.Max(1, (int)RetentionDays);
        _settings.Current.RetentionMaxMegabytes = Math.Max(1, (int)RetentionMaxMegabytes);
        _settings.Current.RetentionMaxEntries = Math.Max(1, (int)RetentionMaxEntries);
    }

    private void OnHotkeyPressed(object? sender, EventArgs e) => CompleteHotkeyTest();

    private void OnWinVPressed(object? sender, EventArgs e)
    {
        if (_gesture?.IsWinV == true)
            CompleteHotkeyTest();
    }

    private void CompleteHotkeyTest()
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (!_awaitingHotkeyTest)
                return;

            _awaitingHotkeyTest = false;
            IsTestingHotkey = false;
            IsHotkeyTested = true;
            _logger.LogInformation("Raccourci {Gesture} validé par re-saisie", HotkeyDisplay);
            SetStatus($"Raccourci {HotkeyDisplay} validé. Vous pouvez passer à l'étape 2.", error: false);
            PersistDraft();
        });
    }

    private void SetStatus(string message, bool error)
    {
        StatusMessage = message;
        IsStatusError = error;
    }
}
