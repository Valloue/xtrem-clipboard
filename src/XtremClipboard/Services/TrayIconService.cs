using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using XtremClipboard.Services.Native;

namespace XtremClipboard.Services;

/// <summary>
/// Icône zone de notification Win32 (**D-UI-OVERLAY-TRAY**).
/// </summary>
public sealed class TrayIconService : ITrayIconService
{
    private const uint TrayId = 1;

    private readonly ILogger<TrayIconService> _logger;
    private IntPtr _ownerHwnd;
    private IntPtr _icon;
    private bool _ownsIcon;
    private bool _added;
    private bool _disposed;
    private SubclassProc? _subclassKeepAlive;

    public event EventHandler? OpenHistoryRequested;
    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService(ILogger<TrayIconService> logger)
    {
        _logger = logger;
    }

    public void Start(IntPtr ownerHwnd)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_added || ownerHwnd == IntPtr.Zero)
            return;

        _ownerHwnd = ownerHwnd;
        ReleaseIcon();
        _icon = LoadAppIcon();

        _subclassKeepAlive = SubclassWndProc;
        if (!SetWindowSubclass(_ownerHwnd, _subclassKeepAlive, 1, IntPtr.Zero))
            _logger.LogWarning("SetWindowSubclass tray échoué — clics tray peut-être inactifs");

        var data = CreateData("Xtrem Clipboard");
        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NimAdd, ref data))
        {
            _logger.LogWarning("Shell_NotifyIcon NIM_ADD échoué");
            return;
        }

        _added = true;
        _logger.LogInformation("Icône tray ajoutée");
    }

    public void Stop()
    {
        if (_added && _ownerHwnd != IntPtr.Zero)
        {
            var data = CreateData(string.Empty);
            NativeMethods.Shell_NotifyIcon(NativeMethods.NimDelete, ref data);
            _added = false;
            _logger.LogInformation("Icône tray retirée");
        }

        if (_ownerHwnd != IntPtr.Zero && _subclassKeepAlive is not null)
        {
            RemoveWindowSubclass(_ownerHwnd, _subclassKeepAlive, 1);
            _subclassKeepAlive = null;
        }

        ReleaseIcon();
    }

    private IntPtr LoadAppIcon()
    {
        var size = NativeMethods.GetSystemMetrics(NativeMethods.SmCxSmIcon);
        if (size < 16)
            size = 16;

        if (AppIcon.FilePath is not null)
        {
            var fromFile = NativeMethods.LoadImage(
                IntPtr.Zero,
                AppIcon.FilePath,
                NativeMethods.ImageIcon,
                size,
                size,
                NativeMethods.LrLoadFromFile);
            if (fromFile != IntPtr.Zero)
            {
                _ownsIcon = true;
                return fromFile;
            }

            _logger.LogWarning("LoadImage de l'icône tray échoué");
        }

        var exe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exe)
            && NativeMethods.ExtractIconEx(exe, 0, out var large, out var small, 1) > 0)
        {
            var chosen = size > 16 && large != IntPtr.Zero ? large : small;
            var other = chosen == large ? small : large;
            if (other != IntPtr.Zero)
                NativeMethods.DestroyIcon(other);
            if (chosen != IntPtr.Zero)
            {
                _ownsIcon = true;
                return chosen;
            }
        }

        _ownsIcon = false;
        return NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr(NativeMethods.IdiApplication));
    }

    private void ReleaseIcon()
    {
        if (_ownsIcon && _icon != IntPtr.Zero)
            NativeMethods.DestroyIcon(_icon);
        _icon = IntPtr.Zero;
        _ownsIcon = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private NativeMethods.NotifyIconData CreateData(string tip) => new()
    {
        cbSize = Marshal.SizeOf<NativeMethods.NotifyIconData>(),
        hWnd = _ownerHwnd,
        uID = TrayId,
        uFlags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip,
        uCallbackMessage = NativeMethods.WmAppTray,
        hIcon = _icon,
        szTip = tip.Length > 127 ? tip[..127] : tip
    };

    private IntPtr SubclassWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (msg == NativeMethods.WmAppTray)
        {
            var mouseMsg = (uint)lParam.ToInt64() & 0xFFFF;
            if (mouseMsg is NativeMethods.WmLbuttonup or NativeMethods.WmLbuthordblclk)
            {
                OpenHistoryRequested?.Invoke(this, EventArgs.Empty);
                return IntPtr.Zero;
            }

            if (mouseMsg == NativeMethods.WmRbuttonup)
            {
                ShowContextMenu();
                return IntPtr.Zero;
            }
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        // Menu Win32 minimal : Historique / Réglages / Quitter
        var hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero)
            return;

        AppendMenu(hMenu, 0, 1, "Historique");
        AppendMenu(hMenu, 0, 2, "Réglages");
        AppendMenu(hMenu, 0x800, 0, string.Empty); // MF_SEPARATOR
        AppendMenu(hMenu, 0, 3, "Quitter");

        NativeMethods.GetCursorPos(out var pt);
        SetForegroundWindow(_ownerHwnd);
        var cmd = TrackPopupMenu(hMenu, 0x0100 /*TPM_RETURNCMD*/, pt.X, pt.Y, 0, _ownerHwnd, IntPtr.Zero);
        DestroyMenu(hMenu);

        switch (cmd)
        {
            case 1:
                _logger.LogInformation("Menu tray : Historique");
                OpenHistoryRequested?.Invoke(this, EventArgs.Empty);
                break;
            case 2:
                _logger.LogInformation("Menu tray : Réglages");
                OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
                break;
            case 3:
                _logger.LogInformation("Menu tray : Quitter");
                ExitRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, uint uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, uint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
