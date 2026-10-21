using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using XtremClipboard.Services.Native;

namespace XtremClipboard.Services;

/// <summary>
/// Intercepte Win+V : hook LL sur le thread UI, RegisterHotKey sur un thread qui pompe.
/// Un seul <see cref="WinVPressed"/> par accord physique. Le premier signal (hotkey ou secours) gagne.
/// </summary>
public sealed class WinVSuppressService : IWinVSuppressService
{
    private const int WinVHotkeyId = 42;

    /// <summary>Filet entre deux accords physiques distincts (H4). Ne couvre pas le second signal du même accord.</summary>
    private const int DedupWindowMs = 200;

    /// <summary>Keyup perdu : sans ça le verrou colle et Win+V est mort jusqu'au redémarrage.</summary>
    private const int ChordSafetyMs = 2000;

    private const int FallbackDelayMs = 40;

    private const string SourceRegisterHotKey = "RegisterHotKey";
    private const string SourceHookLl = "HookLl";
    private const string SourceFallback = "Secours";

    private readonly ILogger<WinVSuppressService> _logger;
    private readonly ConcurrentQueue<Action> _pumpActions = new();
    private NativeMethods.LowLevelKeyboardProc? _procKeepAlive;
    private NativeMethods.WndProc? _wndProcKeepAlive;
    private DispatcherQueue? _uiDispatcher;
    private DispatcherQueueTimer? _fallbackTimer;
    private Thread? _pumpThread;
    private uint _pumpThreadId;
    private string _className = "";
    private IntPtr _hook;
    private IntPtr _hwnd;
    private bool _fallbackTickHooked;
    private bool _disposed;
    private volatile bool _hotkeyRegistered;
    private bool _paused;
    private bool _winDown;

    /// <summary>V encore physiquement enfoncé : les répétitions ne sont pas un nouvel accord.</summary>
    private bool _vDown;

    /// <summary>Verrou d'accord tant que l'appui n'est pas relâché (testé par le harnais).</summary>
    private bool _vChordHeld;

    private bool _hasAcceptedRaise;
    private long _lastAcceptedTick;

    /// <summary>Un signal a déjà été accepté et aucun nouveau V down n'a commencé.</summary>
    private bool _chordOpen;

    /// <summary>Le hook a vu le V down de l'accord en cours. Le second signal reste ignoré, même à 340 ms.</summary>
    private bool _hookSawVDown;

    /// <summary>Un V up est passé depuis cet accord : le prochain V down est un nouvel appui (filet 200 ms).</summary>
    private bool _vUpSinceChord;

    private long _lastChordEventTick;
    private int _fallbackToken;
    private int _fallbackDeliveredToken = -1;
    private int _scheduledFallbackToken;

    public bool IsActive { get; private set; }

    public event EventHandler? WinVPressed;

    public WinVSuppressService(ILogger<WinVSuppressService> logger)
    {
        _logger = logger;
    }

    /// <summary>Doit être appelé depuis le thread UI WinUI (le hook y est posé, ce thread doit pomper).</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsActive)
            return;

        _uiDispatcher = DispatcherQueue.GetForCurrentThread();
        if (_uiDispatcher is null)
            _logger.LogError("WinVSuppressService.Start sans dispatcher UI — levés synchrones");

        var hotkeyError = default(Exception);
        try
        {
            EnsureMessageWindow();
            InvokeOnPumpThread(() => TryStealWinVHotkey(retries: 25, delayMs: 80));
        }
        catch (Exception ex)
        {
            hotkeyError = ex;
        }

        if (!_hotkeyRegistered)
        {
            _logger.LogError(
                hotkeyError,
                "Moitié hotkey indisponible {EventCode} {Category} {Result}",
                "CLIP-WINV-HOTKEY",
                "erreur",
                "degrade");
        }

        var hookOk = TryInstallHook();
        if (!hookOk)
        {
            _logger.LogError(
                "Moitié hook indisponible ({Code}) {EventCode} {Category} {Result}",
                Marshal.GetLastWin32Error(),
                "CLIP-WINV-HOOK",
                "erreur",
                "degrade");
        }

        // Actif seulement si le hook est posé ou si le hotkey est enregistré.
        // La fenêtre message peut exister alors que RegisterHotKey a échoué : ce n'est pas une moitié vivante.
        IsActive = hookOk || _hotkeyRegistered;
        if (IsActive)
        {
            _logger.LogInformation(
                "Interception Win+V active (UI thread). RegisterHotKey={Hotkey}",
                _hotkeyRegistered);
        }
        else
        {
            _logger.LogError("Interception Win+V inactive — hook et hotkey indisponibles");
        }
    }

    public void Stop()
    {
        if (IsActive)
            _logger.LogInformation("Interception Win+V arrêtée");

        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        if (_pumpThreadId != 0)
        {
            try
            {
                if (_hwnd != IntPtr.Zero)
                {
                    InvokeOnPumpThread(() =>
                    {
                        if (_hotkeyRegistered && _hwnd != IntPtr.Zero)
                        {
                            NativeMethods.UnregisterHotKey(_hwnd, WinVHotkeyId);
                            _hotkeyRegistered = false;
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unregister Win+V pendant l'arrêt");
            }

            NativeMethods.PostThreadMessage(_pumpThreadId, NativeMethods.WmQuit, IntPtr.Zero, IntPtr.Zero);
            _pumpThread?.Join(2000);
        }

        _pumpThread = null;
        _pumpThreadId = 0;
        _hwnd = IntPtr.Zero;
        _hotkeyRegistered = false;
        ResetPressGate();
        _paused = false;
        IsActive = false;
    }

    public void Pause()
    {
        // Flag même avant Start() : FirstConfig Pause avant WinV.Start.
        // Ne pas désenregistrer le hotkey (sinon le panneau Windows revient).
        if (_paused)
            return;
        _paused = true;
        ResetPressGate();
        _logger.LogInformation("Win+V suppress en pause (capture raccourci)");
    }

    public void Resume()
    {
        if (!_paused)
            return;
        _paused = false;
        if (IsActive && !_hotkeyRegistered && _hwnd != IntPtr.Zero)
        {
            try
            {
                InvokeOnPumpThread(() => TryStealWinVHotkey(retries: 10, delayMs: 50));
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Moitié hotkey indisponible {EventCode} {Category} {Result}",
                    "CLIP-WINV-HOTKEY",
                    "erreur",
                    "degrade");
            }
        }

        _logger.LogInformation("Win+V suppress repris. RegisterHotKey={Hotkey}", _hotkeyRegistered);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private bool TryInstallHook()
    {
        _procKeepAlive = HookCallback;

        IntPtr module;
        using (var proc = Process.GetCurrentProcess())
        {
            var main = proc.MainModule?.ModuleName;
            module = NativeMethods.GetModuleHandle(main);
            if (module == IntPtr.Zero)
                module = NativeMethods.GetModuleHandle(null);
            if (module == IntPtr.Zero)
                module = NativeMethods.GetModuleHandle("user32.dll");
        }

        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLl,
            _procKeepAlive,
            module,
            0);

        return _hook != IntPtr.Zero;
    }

    /// <summary>Fenêtre message + GetMessage sur un thread dédié. Pas de Sleep ici.</summary>
    private void EnsureMessageWindow()
    {
        if (_hwnd != IntPtr.Zero)
            return;

        using var ready = new ManualResetEventSlim(false);
        Exception? startError = null;

        _pumpThread = new Thread(() =>
        {
            try
            {
                _pumpThreadId = NativeMethods.GetCurrentThreadId();
                _wndProcKeepAlive = WndProc;
                _className = "XtremClipboard.WinVMessageWindow." + Guid.NewGuid().ToString("N");

                var wndClass = new NativeMethods.WndClass
                {
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
                    hInstance = NativeMethods.GetModuleHandle(null),
                    lpszClassName = _className
                };

                var atom = NativeMethods.RegisterClass(ref wndClass);
                if (atom == 0)
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err != 1410)
                        throw new Win32Exception(err);
                }

                _hwnd = NativeMethods.CreateWindowEx(
                    0, _className, string.Empty, 0,
                    0, 0, 0, 0,
                    NativeMethods.HwndMessage,
                    IntPtr.Zero,
                    NativeMethods.GetModuleHandle(null),
                    IntPtr.Zero);

                if (_hwnd == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                ready.Set();

                while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
                {
                    if (msg.message == NativeMethods.WmAppInvoke)
                        DrainPumpActions();

                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                startError = ex;
                ready.Set();
            }
            finally
            {
                if (_hwnd != IntPtr.Zero)
                {
                    NativeMethods.DestroyWindow(_hwnd);
                    _hwnd = IntPtr.Zero;
                }

                if (!string.IsNullOrEmpty(_className))
                    NativeMethods.UnregisterClass(_className, NativeMethods.GetModuleHandle(null));
            }
        })
        {
            IsBackground = true,
            Name = "XtremClipboard.WinVPump"
        };

        _pumpThread.SetApartmentState(ApartmentState.STA);
        _pumpThread.Start();

        if (!ready.Wait(5000))
            throw new TimeoutException("Timeout création fenêtre Win+V.");

        if (startError is not null)
            throw startError;
    }

    /// <summary>Vol du hotkey. Uniquement sur le thread qui pompe — le Sleep ne bloque pas l'UI.</summary>
    private void TryStealWinVHotkey(int retries, int delayMs)
    {
        if (_hwnd == IntPtr.Zero || _hotkeyRegistered)
            return;

        for (var i = 0; i < retries; i++)
        {
            if (NativeMethods.RegisterHotKey(
                    _hwnd,
                    WinVHotkeyId,
                    NativeMethods.ModWin | NativeMethods.ModNoRepeat,
                    NativeMethods.VkV))
            {
                _hotkeyRegistered = true;
                _logger.LogInformation("RegisterHotKey Win+V OK (essai {N})", i + 1);
                return;
            }

            var err = Marshal.GetLastWin32Error();
            if (err != 1409)
            {
                _logger.LogWarning("RegisterHotKey Win+V erreur {Code}", err);
                return;
            }

            if (i < retries - 1)
                Thread.Sleep(delayMs);
        }

        _logger.LogWarning(
            "RegisterHotKey Win+V toujours pris par le shell (1409) — dépendance au hook LL");
    }

    private void InvokeOnPumpThread(Action action)
    {
        if (_pumpThread is not null
            && Thread.CurrentThread.ManagedThreadId == _pumpThread.ManagedThreadId)
        {
            action();
            return;
        }

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Fenêtre Win+V absente.");

        using var done = new ManualResetEventSlim(false);
        Exception? error = null;

        _pumpActions.Enqueue(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                try
                {
                    done.Set();
                }
                catch (ObjectDisposedException)
                {
                    // Timeout côté appelant : le thread pompe continue.
                }
            }
        });

        if (!NativeMethods.PostMessage(_hwnd, NativeMethods.WmAppInvoke, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "PostMessage Win+V échoué");

        if (!done.Wait(8000))
            throw new TimeoutException("Timeout action Win+V sur le thread message.");

        if (error is not null)
            throw error;
    }

    private void DrainPumpActions()
    {
        while (_pumpActions.TryDequeue(out var action))
            action();
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WmAppInvoke)
        {
            DrainPumpActions();
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WmHotkey && wParam.ToInt32() == WinVHotkeyId)
        {
            // Jamais traité dans le hook : renvoyé au thread UI quand un dispatcher existe.
            RaiseWinV(SourceRegisterHotKey);
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_paused)
        {
            ReleaseStaleChordLock();

            var msg = wParam.ToInt32();
            var info = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
            var isDown = msg is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
            var isUp = msg is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;

            if (info.vkCode is NativeMethods.VkLWin or NativeMethods.VkRWin)
            {
                if (isDown)
                    _winDown = true;
                if (isUp)
                {
                    _winDown = false;
                    // Win up lâche la tenue, pas l'identité de l'accord : un WM_HOTKEY tardif
                    // du même appui ne doit pas rouvrir après le relâchement.
                    _vChordHeld = false;
                    NoteChordEvent();
                }
            }

            if (isUp && info.vkCode == NativeMethods.VkV)
            {
                _vDown = false;
                _vChordHeld = false;
                _vUpSinceChord = true;
                NoteChordEvent();
            }

            if (isDown && info.vkCode == NativeMethods.VkV && IsExactWinVChord())
            {
                if (_vDown)
                {
                    _vChordHeld = true;
                    NoteChordEvent();
                    if (_hotkeyRegistered)
                        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
                    return (IntPtr)1;
                }

                _vDown = true;
                _vChordHeld = true;
                NoteChordEvent();

                // Hotkey déjà levé, hook pas encore vu ce V, aucun V up : même accord, quel que soit l'âge.
                // Le filet de 200 ms ne vaut qu'après un vrai V up. Seul le délai de 2 s lâche un keyup perdu.
                if (_chordOpen && !_hookSawVDown && !_vUpSinceChord)
                {
                    _hookSawVDown = true;
                    if (_hotkeyRegistered)
                        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
                    return (IntPtr)1;
                }

                // Nouveau V down (premier, ou après un V up). Le filet court s'applique dans Fire.
                _chordOpen = false;
                _hookSawVDown = true;
                _vUpSinceChord = false;

                if (_hotkeyRegistered)
                {
                    // Laisser passer pour que le système poste WM_HOTKEY. Le secours lève si rien n'arrive.
                    ArmFallback();
                    return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
                }

                RaiseWinV(SourceHookLl);
                return (IntPtr)1;
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Secours appelable par le harnais. Avec un dispatcher, <see cref="ArmFallback"/> le planifie
    /// une fois par accord. Sans dispatcher, personne ne l'appelle à notre place.
    /// </summary>
    private void DeliverFallback() => DeliverFallback(_fallbackToken);

    private void DeliverFallback(int token)
    {
        if (token <= 0 || token != _fallbackToken || _fallbackDeliveredToken == token)
            return;

        _fallbackDeliveredToken = token;
        RaiseWinV(SourceFallback);
    }

    private void ArmFallback()
    {
        var token = ++_fallbackToken;
        if (_uiDispatcher is null)
            return;

        if (!_uiDispatcher.TryEnqueue(() => ScheduleFallback(token)))
        {
            _logger.LogError(
                "TryEnqueue Win+V a échoué {EventCode} {Category} {Result}",
                "CLIP-WINV-ENQUEUE",
                "erreur",
                "echec");
        }
    }

    private void ScheduleFallback(int token)
    {
        if (_uiDispatcher is null)
            return;

        _fallbackTimer ??= _uiDispatcher.CreateTimer();
        _scheduledFallbackToken = token;
        _fallbackTimer.Stop();
        _fallbackTimer.IsRepeating = false;
        _fallbackTimer.Interval = TimeSpan.FromMilliseconds(FallbackDelayMs);
        if (!_fallbackTickHooked)
        {
            _fallbackTimer.Tick += OnFallbackTick;
            _fallbackTickHooked = true;
        }

        _fallbackTimer.Start();
    }

    private void OnFallbackTick(DispatcherQueueTimer sender, object args) =>
        DeliverFallback(_scheduledFallbackToken);

    private void RaiseWinV(string source)
    {
        // Show ne s'exécute pas dans le hook : le levé est différé dès qu'un dispatcher existe.
        void Fire()
        {
            ReleaseStaleChordLock();

            if (_paused)
            {
                _logger.LogInformation("Win+V ignoré (pause) {Source}", source);
                return;
            }

            var now = Environment.TickCount64;

            // Hook a vu le V : tout signal suivant sans nouveau V down est le même accord, même à 340 ms.
            if (_chordOpen && _hookSawVDown)
            {
                LogIgnoredSameChord(source, now);
                return;
            }

            // Hotkey seul, puis l'autre source (hook ou secours) : encore le même accord.
            if (_chordOpen && !_hookSawVDown && source != SourceRegisterHotKey)
            {
                LogIgnoredSameChord(source, now);
                return;
            }

            // Second WM_HOTKEY (fenêtre élevée, le hook ne voit rien) : filet court, pas le verrou long.
            if (_chordOpen && !_hookSawVDown && source == SourceRegisterHotKey)
            {
                var hotkeyAge = _lastAcceptedTick == 0 ? 0 : now - _lastAcceptedTick;
                if (hotkeyAge < DedupWindowMs)
                {
                    _logger.LogInformation(
                        "Win+V ignoré (dédup) {Source} {AgeMs} ms",
                        source,
                        hotkeyAge);
                    return;
                }

                _chordOpen = false;
            }

            if (_hasAcceptedRaise)
            {
                var age = _lastAcceptedTick == 0 ? 0 : now - _lastAcceptedTick;
                if (age < DedupWindowMs)
                {
                    _logger.LogInformation(
                        "Win+V ignoré (dédup) {Source} {AgeMs} ms",
                        source,
                        age);
                    _chordOpen = true;
                    return;
                }
            }

            _chordOpen = true;
            _hasAcceptedRaise = true;
            _lastAcceptedTick = now;
            _vUpSinceChord = false;
            if (source != SourceRegisterHotKey)
                _hookSawVDown = true;
            NoteChordEvent();

            if (source == SourceRegisterHotKey)
                _logger.LogInformation("Win+V via RegisterHotKey");
            else if (source == SourceFallback)
            {
                _logger.LogInformation(
                    "Win+V secours utilisé {EventCode} {Category} {Result}",
                    "CLIP-WINV-FALLBACK",
                    "usage",
                    "degrade");
            }
            else
                _logger.LogInformation("Win+V via hook LL (avalé)");

            try
            {
                WinVPressed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur WinVPressed");
                // Le filet ne doit pas manger l'appui suivant si l'affichage a jeté.
                _hasAcceptedRaise = false;
                _chordOpen = false;
                _hookSawVDown = false;
                _vUpSinceChord = false;
                _lastAcceptedTick = 0;
            }
        }

        if (_uiDispatcher is null)
            Fire();
        else if (!_uiDispatcher.TryEnqueue(Fire))
        {
            _logger.LogError(
                "TryEnqueue Win+V a échoué {EventCode} {Category} {Result}",
                "CLIP-WINV-ENQUEUE",
                "erreur",
                "echec");
        }
    }

    private void LogIgnoredSameChord(string source, long now)
    {
        var age = _lastAcceptedTick == 0 ? 0 : now - _lastAcceptedTick;
        if (age < DedupWindowMs)
        {
            _logger.LogInformation(
                "Win+V ignoré (dédup) {Source} {AgeMs} ms",
                source,
                age);
        }
        else
        {
            _logger.LogInformation(
                "Win+V ignoré (accord) {Source} {AgeMs} ms {EventCode} {Category} {Result}",
                source,
                age,
                "CLIP-WINV-CHORD",
                "usage",
                "ok");
        }
    }

    private void NoteChordEvent() => _lastChordEventTick = Environment.TickCount64;

    /// <summary>Keyup perdu : au bout d'environ 2 s sans événement, le verrou lâche.</summary>
    private void ReleaseStaleChordLock()
    {
        if (_lastChordEventTick == 0)
            return;
        if (Environment.TickCount64 - _lastChordEventTick < ChordSafetyMs)
            return;

        _chordOpen = false;
        _vChordHeld = false;
        _vDown = false;
        _hookSawVDown = false;
        _vUpSinceChord = false;
        _hasAcceptedRaise = false;
        _lastAcceptedTick = 0;
        _lastChordEventTick = 0;
        CancelFallback();
        _logger.LogInformation(
            "Win+V verrou d'accord relâché (délai de sécurité) {EventCode} {Category} {Result}",
            "CLIP-WINV-LOCK",
            "usage",
            "ok");
    }

    private bool IsExactWinVChord()
    {
        if (!_winDown && !IsWinDownAsync())
            return false;
        if (IsModifierDown())
            return false;
        return true;
    }

    /// <summary>Horodatage du filet et verrou jusqu'au keyup, remis à zéro.</summary>
    private void ResetPressGate()
    {
        _vChordHeld = false;
        _vDown = false;
        _hasAcceptedRaise = false;
        _lastAcceptedTick = 0;
        _chordOpen = false;
        _hookSawVDown = false;
        _vUpSinceChord = false;
        _lastChordEventTick = 0;
        CancelFallback();
    }

    private void CancelFallback()
    {
        _fallbackToken++;
        _fallbackDeliveredToken = _fallbackToken;
    }

    private static bool IsWinDownAsync() =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VkLWin) & 0x8000) != 0
        || (NativeMethods.GetAsyncKeyState(NativeMethods.VkRWin) & 0x8000) != 0;

    private static bool IsModifierDown() =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VkShift) & 0x8000) != 0
        || (NativeMethods.GetAsyncKeyState(NativeMethods.VkControl) & 0x8000) != 0
        || (NativeMethods.GetAsyncKeyState(NativeMethods.VkMenu) & 0x8000) != 0;
}
