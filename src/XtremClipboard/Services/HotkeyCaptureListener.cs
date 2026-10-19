using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.System;
using XtremClipboard.Services.Native;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;

namespace XtremClipboard.Services;

/// <summary>
/// Capture un raccourci via WH_KEYBOARD_LL — nécessaire pour le modificateur Win
/// (souvent invisible dans KeyDown WinUI).
/// </summary>
public sealed class HotkeyCaptureListener : IDisposable
{
    private readonly ILogger<HotkeyCaptureListener> _logger;
    private NativeMethods.LowLevelKeyboardProc? _procKeepAlive;
    private DispatcherQueue? _dispatcher;
    private IntPtr _hook;
    private bool _winDown;
    private bool _disposed;

    public bool IsListening { get; private set; }

    public event EventHandler<HotkeyCaptureEventArgs>? Captured;

    public HotkeyCaptureListener(ILogger<HotkeyCaptureListener> logger)
    {
        _logger = logger;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsListening)
            return;

        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("HotkeyCaptureListener.Start nécessite le thread UI.");

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

        if (_hook == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                "SetWindowsHookEx capture hotkey échoué");

        IsListening = true;
        _logger.LogInformation("Capture raccourci LL active (modificateur Win inclus)");
    }

    public void Stop()
    {
        if (IsListening)
            _logger.LogInformation("Capture raccourci arrêtée");

        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        _winDown = false;
        IsListening = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsListening)
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

            if (isDown && !IsModifierVk(info.vkCode))
            {
                var ctrl = IsDown(0x11) || IsDown(0xA2) || IsDown(0xA3); // VK_CONTROL / L / R
                var alt = IsDown(0x12) || IsDown(0xA4) || IsDown(0xA5);   // VK_MENU / L / R
                var shift = IsDown(0x10) || IsDown(0xA0) || IsDown(0xA1); // VK_SHIFT / L / R
                var win = _winDown || IsDown(NativeMethods.VkLWin) || IsDown(NativeMethods.VkRWin);

                if (ctrl || alt || shift || win)
                {
                    var key = (VirtualKey)info.vkCode;
                    RaiseCaptured(ctrl, alt, shift, win, key);
                    // Avaler pour éviter le menu Démarrer / raccourcis shell pendant la capture
                    return (IntPtr)1;
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void RaiseCaptured(bool ctrl, bool alt, bool shift, bool win, VirtualKey key)
    {
        void Fire()
        {
            try
            {
                Captured?.Invoke(this, new HotkeyCaptureEventArgs(ctrl, alt, shift, win, key));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur Captured hotkey");
            }
        }

        if (_dispatcher is null || _dispatcher.HasThreadAccess)
            Fire();
        else
            _dispatcher.TryEnqueue(Fire);
    }

    private static bool IsDown(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool IsModifierVk(uint vk) =>
        vk is 0x10 or 0x11 or 0x12 // Shift Ctrl Alt
            or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5
            or NativeMethods.VkLWin or NativeMethods.VkRWin
            or 0x5D; // Apps
}

public sealed class HotkeyCaptureEventArgs : EventArgs
{
    public HotkeyCaptureEventArgs(bool ctrl, bool alt, bool shift, bool win, VirtualKey key)
    {
        Ctrl = ctrl;
        Alt = alt;
        Shift = shift;
        Win = win;
        Key = key;
    }

    public bool Ctrl { get; }
    public bool Alt { get; }
    public bool Shift { get; }
    public bool Win { get; }
    public VirtualKey Key { get; }
}
