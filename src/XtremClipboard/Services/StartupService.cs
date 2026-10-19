using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace XtremClipboard.Services;

/// <summary>
/// Démarrage avec Windows via HKCU Run (**D-STARTUP-V1**).
/// </summary>
public sealed class StartupService : IStartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "XtremClipboard";

    private readonly ILogger<StartupService> _logger;

    public StartupService(ILogger<StartupService> logger)
    {
        _logger = logger;
    }

    public void Apply(bool startWithWindows)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null)
                return;

            if (!startWithWindows)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                _logger.LogInformation("Démarrage Windows retiré");
                return;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                _logger.LogWarning("Chemin de l'exécutable indisponible — démarrage Windows non configuré");
                return;
            }

            key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            _logger.LogInformation("Démarrage Windows enregistré");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Échec écriture clé Run");
        }
    }
}
