using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using XtremClipboard.Models;
using XtremClipboard.Services.Native;

namespace XtremClipboard.Services;

/// <summary>
/// Raccourci global Win32 (RegisterHotKey) via fenêtre message-only.
/// Register/Unregister s'exécutent sur le thread propriétaire du HWND.
/// </summary>
public sealed class HotkeyService : IHotkeyService
{
    private const int HotkeyId = 1;

    private readonly ILogger<HotkeyService> _logger;
    private readonly ConcurrentQueue<Action> _pumpActions = new();
    private Thread? _pumpThread;
    private uint _pumpThreadId;
    private IntPtr _hwnd;
    private NativeMethods.WndProc? _wndProcKeepAlive;
    private bool _classRegistered;
    private bool _disposed;

    public event EventHandler? HotkeyPressed;

    public bool IsRegistered { get; private set; }

    public HotkeyService(ILogger<HotkeyService> logger)
    {
        _logger = logger;
        EnsureMessageWindow();
    }

    public bool TryRegister(HotkeyGesture gesture, out string? errorMessage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        errorMessage = null;

        EnsureMessageWindow();

        var modifiers = NativeMethods.ModNoRepeat;
        if (gesture.Ctrl) modifiers |= NativeMethods.ModControl;
        if (gesture.Alt) modifiers |= NativeMethods.ModAlt;
        if (gesture.Shift) modifiers |= NativeMethods.ModShift;
        if (gesture.Win) modifiers |= NativeMethods.ModWin;
        var vk = (uint)gesture.Key;

        var errorCode = 0;
        var ok = false;

        InvokeOnPumpThread(() =>
        {
            if (IsRegistered)
            {
                NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
                IsRegistered = false;
            }

            ok = NativeMethods.RegisterHotKey(_hwnd, HotkeyId, modifiers, vk);
            if (!ok)
            {
                errorCode = Marshal.GetLastWin32Error();
                return;
            }

            IsRegistered = true;
        });

        if (!ok)
        {
            var err = new Win32Exception(errorCode);
            errorMessage = $"Impossible d'enregistrer le raccourci « {gesture.ToDisplayString()} » : {err.Message}";
            _logger.LogWarning("RegisterHotKey échoué ({Code}) : {Message}", errorCode, err.Message);
            return false;
        }

        _logger.LogInformation("Raccourci global enregistré");
        return true;
    }

    public void Unregister()
    {
        if (_disposed || _hwnd == IntPtr.Zero)
            return;

        InvokeOnPumpThread(() =>
        {
            if (!IsRegistered)
                return;

            NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
            IsRegistered = false;
            _logger.LogInformation("Raccourci global retiré");
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                InvokeOnPumpThread(() =>
                {
                    if (IsRegistered)
                    {
                        NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
                        IsRegistered = false;
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unregister pendant Dispose");
        }

        if (_pumpThreadId != 0)
            NativeMethods.PostThreadMessage(_pumpThreadId, NativeMethods.WmQuit, IntPtr.Zero, IntPtr.Zero);

        _pumpThread?.Join(2000);
        _pumpThread = null;
    }

    private void InvokeOnPumpThread(Action action)
    {
        if (_pumpThread is not null
            && Thread.CurrentThread.ManagedThreadId == _pumpThread.ManagedThreadId)
        {
            action();
            return;
        }

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
                done.Set();
            }
        });

        if (!NativeMethods.PostMessage(_hwnd, NativeMethods.WmAppInvoke, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "PostMessage hotkey échoué");

        if (!done.Wait(5000))
            throw new TimeoutException("Timeout RegisterHotKey sur le thread message.");

        if (error is not null)
            throw error;
    }

    private void DrainPumpActions()
    {
        while (_pumpActions.TryDequeue(out var action))
            action();
    }

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

                var className = "XtremClipboard.HotkeyMessageWindow";
                var wndClass = new NativeMethods.WndClass
                {
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
                    hInstance = NativeMethods.GetModuleHandle(null),
                    lpszClassName = className
                };

                if (!_classRegistered)
                {
                    var atom = NativeMethods.RegisterClass(ref wndClass);
                    if (atom == 0)
                    {
                        var err = Marshal.GetLastWin32Error();
                        // 1410 = class already exists
                        if (err != 1410)
                            throw new Win32Exception(err);
                    }

                    _classRegistered = true;
                }

                _hwnd = NativeMethods.CreateWindowEx(
                    0,
                    className,
                    string.Empty,
                    0,
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
            }
        })
        {
            IsBackground = true,
            Name = "XtremClipboard.HotkeyPump"
        };

        _pumpThread.SetApartmentState(ApartmentState.STA);
        _pumpThread.Start();

        if (!ready.Wait(5000))
            throw new TimeoutException("Timeout création fenêtre hotkey.");

        if (startError is not null)
            throw startError;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WmAppInvoke)
        {
            DrainPumpActions();
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            try
            {
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur gestionnaire HotkeyPressed");
            }

            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }
}
