using System.Text;
using Microsoft.Extensions.Logging;
using XtremClipboard.Models;
using XtremClipboard.Services.Native;

namespace XtremClipboard.Services;

/// <summary>
/// Compare le propriétaire du presse-papiers aux règles de <see cref="AppSettings.Exclusions"/>.
/// Ne lit pas le contenu du presse-papiers et ne le journalise pas.
/// </summary>
public sealed class AppExclusionService : IAppExclusionService
{
    private readonly ISettingsService _settings;
    private readonly ILogger<AppExclusionService> _logger;

    public AppExclusionService(ISettingsService settings, ILogger<AppExclusionService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool IsClipboardOwnerExcluded()
    {
        var hwnd = NativeMethods.GetClipboardOwner();
        if (hwnd == IntPtr.Zero)
            hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return false;

        var process = ResolveProcessFileName(hwnd);
        if (process is null)
            return false;

        if (BuiltInExclusions.Matches(process))
            return true;

        var rules = _settings.Current.Exclusions;
        if (rules is null || rules.Count == 0)
            return false;

        var title = ReadTitle(hwnd);
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.ProcessName))
                continue;
            if (!process.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrWhiteSpace(rule.TitleContains))
                return true;
            if (title.Contains(rule.TitleContains, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public string? NormalizeProcessName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var name = Path.GetFileName(raw.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 260)
            return null;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        if (!name.Contains('.'))
            name += ".exe";
        return name;
    }

    public bool IsSelfProcess(string processName) =>
        processName.Equals(SelfFileName(), StringComparison.OrdinalIgnoreCase);

    public bool IsBuiltInPasswordManager(string processName) =>
        BuiltInExclusions.Matches(processName);

    public IReadOnlyList<VisibleWindowApp> ListVisibleApps()
    {
        var found = new List<VisibleWindowApp>();
        try
        {
            NativeMethods.EnumWindowsProc callback = (hwnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd))
                    return true;
                if (NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner) != IntPtr.Zero)
                    return true;

                var title = ReadTitle(hwnd);
                if (string.IsNullOrWhiteSpace(title))
                    return true;

                var process = ResolveProcessFileName(hwnd);
                if (process is null || IsSelfProcess(process) || BuiltInExclusions.Matches(process))
                    return true;

                found.Add(new VisibleWindowApp(process, title));
                return found.Count < 200;
            };

            NativeMethods.EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Liste des fenêtres visibles indisponible");
            return Array.Empty<VisibleWindowApp>();
        }

        return found
            .GroupBy(a => a.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(a => a.WindowTitle.Length).First())
            .OrderBy(a => a.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string SelfFileName()
    {
        var name = Environment.ProcessPath is null
            ? "XtremClipboard.exe"
            : Path.GetFileName(Environment.ProcessPath);
        return string.IsNullOrWhiteSpace(name) ? "XtremClipboard.exe" : name;
    }

    private static string? ResolveProcessFileName(IntPtr hwnd)
    {
        var name = QueryProcessFileName(hwnd);
        if (name is null)
            return null;
        if (!name.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            return name;

        string? hosted = null;
        NativeMethods.EnumWindowsProc childCallback = (child, _) =>
        {
            var childName = QueryProcessFileName(child);
            if (childName is null
                || childName.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            hosted = childName;
            return false;
        };

        NativeMethods.EnumChildWindows(hwnd, childCallback, IntPtr.Zero);
        GC.KeepAlive(childCallback);
        return hosted ?? name;
    }

    private static string? QueryProcessFileName(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
            return null;

        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            if (!NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size))
                return null;

            var file = Path.GetFileName(buffer.ToString());
            return string.IsNullOrWhiteSpace(file) ? null : file;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static string ReadTitle(IntPtr hwnd)
    {
        var buffer = new StringBuilder(512);
        _ = NativeMethods.GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }
}
