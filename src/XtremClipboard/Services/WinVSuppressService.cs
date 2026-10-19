using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using XtremClipboard.Services.Native;

namespace XtremClipboard.Services;

/// <summary>
/// Intercepte Win+V sur le thread UI (hook LL) + tentatives RegisterHotKey après kill des hôtes shell.
/// </summary>
public sealed class WinVSuppressService : IWinVSuppressService
{
    private const int WinVHotkeyId = 42;

    private readonly ILogger<WinVSuppressService> _logger;
    private NativeMethods.LowLevelKeyboardProc? _procKeepAlive;
    private NativeMethods.WndProc? _wndProcKeepAlive;
    private DispatcherQueue? _uiDispatcher;
    private IntPtr _hook;
    private IntPtr _hwnd;
    private bool _classRegistered;
    private bool _winDown;
    private bool _disposed;
    private bool _hotkeyRegistered;
    private bool _paused;

    public bool IsActive { get; private set; }

    public event EventHandler? WinVPressed;

    public WinVSuppressService(ILogger<WinVSuppressService> logger)
    {
        _logger = logger;
    }

    /// <summary>Doit être appelé depuis le thread UI WinUI.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsActive)
            return;

        _uiDispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("WinVSuppressService.Start nécessite le thread UI.");

        _procKeepAlive = HookCallback;
        _wndProcKeepAlive = WndProc;
        EnsureMessageWindow();

        // Tentatives pour voler Win+V au shell (1409 = déjà pris)
        TryStealWinVHotkey(retries: 25, delayMs: 80);

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

        if (_hook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx WH_KEYBOARD_LL échoué");

        IsActive = true;
        _logger.LogInformation(
            "Interception Win+V active (UI thread). RegisterHotKey={Hotkey}",
            _hotkeyRegistered);
    }

    public void Stop()
    {
        if (IsActive)
            _logger.LogInformation("Interception Win+V arrêtée");

        if (_hotkeyRegistered && _hwnd != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_hwnd, WinVHotkeyId);
            _hotkeyRegistered = false;
        }

        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        _paused = false;
        IsActive = false;
    }

    public void Pause()
    {
        // Flag même avant Start() : FirstConfig Pause avant WinV.Start
        if (_paused)
            return;
        _paused = true;
        _logger.LogInformation("Win+V suppress en pause (capture raccourci)");
    }

    public void Resume()
    {
        if (!_paused)
            return;
        _paused = false;
        if (IsActive && !_hotkeyRegistered && _hwnd != IntPtr.Zero)
            TryStealWinVHotkey(retries: 10, delayMs: 50);
        _logger.LogInformation("Win+V suppress repris. RegisterHotKey={Hotkey}", _hotkeyRegistered);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private void TryStealWinVHotkey(int retries, int delayMs)
    {
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
            if (err != 1409) // ERROR_HOTKEY_ALREADY_REGISTERED
            {
                _logger.LogWarning("RegisterHotKey Win+V erreur {Code}", err);
                return;
            }

            Thread.Sleep(delayMs);
        }

        _logger.LogWarning(
            "RegisterHotKey Win+V toujours pris par le shell (1409) — dépendance au hook LL");
    }

    private void EnsureMessageWindow()
    {
        if (_hwnd != IntPtr.Zero)
            return;

        var className = "XtremClipboard.WinVMessageWindow";
        var wndClass = new NativeMethods.WndClass
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive!),
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = className
        };

        if (!_classRegistered)
        {
            var atom = NativeMethods.RegisterClass(ref wndClass);
            if (atom == 0)
            {
                var err = Marshal.GetLastWin32Error();
                if (err != 1410)
                    throw new Win32Exception(err);
            }

            _classRegistered = true;
        }

        _hwnd = NativeMethods.CreateWindowEx(
            0, className, string.Empty, 0,
            0, 0, 0, 0,
            NativeMethods.HwndMessage,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WmHotkey && wParam.ToInt32() == WinVHotkeyId)
        {
            _logger.LogInformation("Win+V via RegisterHotKey");
            RaiseWinV();
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_paused)
        {
            var msg = wParam.ToInt32();
            var info = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
            var isDown = msg is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
            var isUp = msg is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;

            if (info.vkCode is NativeMethods.VkLWin or NativeMethods.VkRWin)
            {
                if (isDown) _winDown = true;
                if (isUp) _winDown = false;
            }

            if (isDown && info.vkCode == NativeMethods.VkV && (_winDown || IsWinDownAsync()))
            {
                _logger.LogInformation("Win+V via hook LL (avalé)");
                RaiseWinV();
                return (IntPtr)1;
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void RaiseWinV()
    {
        // Remonter sur le dispatcher UI si on est déjà dessus, Invoke reste ok
        void Fire()
        {
            try
            {
                WinVPressed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur WinVPressed");
            }
        }

        if (_uiDispatcher is null || _uiDispatcher.HasThreadAccess)
            Fire();
        else
            _uiDispatcher.TryEnqueue(Fire);
    }

    private static bool IsWinDownAsync() =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VkLWin) & 0x8000) != 0
        || (NativeMethods.GetAsyncKeyState(NativeMethods.VkRWin) & 0x8000) != 0;
}
