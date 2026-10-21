using System.Runtime.InteropServices;

namespace XtremClipboard.Services.Logging;

/// <summary>
/// WM_ENDSESSION sur la fenêtre hôte. Ne touche pas au filtre Win+V.
/// </summary>
public sealed class HostShutdownHook : IDisposable
{
    public const uint WmEndSession = 0x0016;

    private const uint SubclassId = 41;

    private readonly IntPtr _hwnd;
    private readonly Action _onShutdown;
    private SubclassProc? _proc;
    private bool _fired;
    private bool _disposed;

    private HostShutdownHook(IntPtr hwnd, Action onShutdown)
    {
        _hwnd = hwnd;
        _onShutdown = onShutdown;
    }

    public static bool IsWindowsShutdown(uint message, IntPtr wParam) =>
        message == WmEndSession && wParam != IntPtr.Zero;

    public static HostShutdownHook? TryAttach(IntPtr hwnd, Action onShutdown)
    {
        if (hwnd == IntPtr.Zero)
            return null;

        var hook = new HostShutdownHook(hwnd, onShutdown);
        hook._proc = hook.Subclass;
        if (!SetWindowSubclass(hwnd, hook._proc, SubclassId, IntPtr.Zero))
            return null;

        return hook;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_proc is not null && _hwnd != IntPtr.Zero)
        {
            RemoveWindowSubclass(_hwnd, _proc, SubclassId);
            _proc = null;
        }
    }

    private IntPtr Subclass(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (!_fired && IsWindowsShutdown(msg, wParam))
        {
            _fired = true;
            try
            {
                _onShutdown();
            }
            catch
            {
                // L'arrêt Windows continue même si le journal échoue.
            }
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private delegate IntPtr SubclassProc(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, uint uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, uint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
}
