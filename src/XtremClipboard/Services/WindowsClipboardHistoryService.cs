using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace XtremClipboard.Services;

/// <summary>
/// Coupe l'historique Windows + libère Win+V d'Explorer (DisabledHotkeys).
/// </summary>
public sealed class WindowsClipboardHistoryService : IWindowsClipboardHistoryService
{
    private const string ClipboardKey = @"Software\Microsoft\Clipboard";
    private const string PolicyKey = @"Software\Policies\Microsoft\Windows\System";
    private const string ExplorerAdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private readonly ILogger<WindowsClipboardHistoryService> _logger;

    public WindowsClipboardHistoryService(ILogger<WindowsClipboardHistoryService> logger)
    {
        _logger = logger;
    }

    public bool TryDisableWindowsClipboardHistory(out string? message)
    {
        var ok = false;
        var wasEnabled = false;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ClipboardKey);
            if (key is null)
            {
                message = "Impossible d'ouvrir la clé registre Clipboard.";
                _logger.LogWarning(message);
                return false;
            }

            wasEnabled = IsTruthy(key.GetValue("EnableClipboardHistory"));
            key.SetValue("EnableClipboardHistory", 0, RegistryValueKind.DWord);
            key.SetValue("EnableCloudClipboard", 0, RegistryValueKind.DWord);
            key.SetValue("CloudClipboardAutomaticUpload", 0, RegistryValueKind.DWord);
            ok = true;
        }
        catch (Exception ex)
        {
            message = "Échec écriture HKCU\\Clipboard (voir logs).";
            _logger.LogError(ex, "Échec écriture registre Clipboard");
            return false;
        }

        TrySetPolicyValue("AllowClipboardHistory", 0);
        TrySetPolicyValue("AllowCrossDeviceClipboard", 0);

        try
        {
            using var machinePolicy = Registry.LocalMachine.CreateSubKey(PolicyKey);
            machinePolicy?.SetValue("AllowClipboardHistory", 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HKLM AllowClipboardHistory non modifiable");
        }

        TryRestartClipboardUiHosts();

        message = wasEnabled
            ? "Historique presse-papiers Windows désactivé (registre)."
            : "Historique Windows forcé à off (registre).";
        _logger.LogInformation("Clipboard history disable ok={Ok} wasEnabled={Was}", ok, wasEnabled);
        return ok;
    }

    public void RestartClipboardUiHosts() => TryRestartClipboardUiHosts();

    public bool TryReleaseWinVFromExplorer(out string? message)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvancedKey);
            if (key is null)
            {
                message = "Impossible d'ouvrir Explorer\\Advanced.";
                return false;
            }

            var current = key.GetValue("DisabledHotkeys") as string ?? string.Empty;
            var upper = current.ToUpperInvariant();
            if (upper.Contains('V', StringComparison.Ordinal))
            {
                message = "DisabledHotkeys contient déjà V — pas de restart Explorer.";
                _logger.LogInformation(message);
                return true;
            }

            // Ajouter V (majuscule) pour que Explorer ne prenne plus Win+V
            var next = upper + "V";
            key.SetValue("DisabledHotkeys", next, RegistryValueKind.String);
            _logger.LogInformation("DisabledHotkeys={Value} — restart Explorer pour appliquer", next);

            RestartExplorer();
            // Laisser Explorer se relancer avant RegisterHotKey
            Thread.Sleep(1500);

            message = "Win+V libéré côté Explorer (DisabledHotkeys + restart Explorer).";
            return true;
        }
        catch (Exception ex)
        {
            message = "Échec DisabledHotkeys (voir logs).";
            _logger.LogError(ex, "Échec libération Win+V Explorer");
            return false;
        }
    }

    private void RestartExplorer()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Kill explorer ignoré");
                }
                finally
                {
                    p.Dispose();
                }
            }

            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo
            {
                FileName = shell,
                UseShellExecute = true
            });
            _logger.LogInformation("Explorer redémarré");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Échec restart Explorer");
        }
    }

    private void TrySetPolicyValue(string name, int value)
    {
        try
        {
            using var policy = Registry.CurrentUser.CreateSubKey(PolicyKey);
            policy?.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Policies\\{Name} inaccessible — ignoré", name);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Policies\\{Name} non écrit", name);
        }
    }

    private void TryRestartClipboardUiHosts()
    {
        foreach (var name in new[] { "TextInputHost", "ShellExperienceHost" })
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        p.Kill(entireProcessTree: true);
                        _logger.LogInformation("Processus {Name} tué pour recharger UI clipboard", name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Kill {Name} ignoré", name);
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Enumeration {Name} ignorée", name);
            }
        }
    }

    private static bool IsTruthy(object? value) =>
        value is int i && i != 0
        || value is long l && l != 0
        || value is uint ui && ui != 0;
}
