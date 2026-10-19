namespace XtremClipboard.Models;

/// <summary>
/// Réglages persistés dans settings.json (D-SETTINGS).
/// </summary>
public sealed class AppSettings
{
    /// <summary>True quand la config post-install est terminée et l'app activée.</summary>
    public bool IsSetupCompleted { get; set; }

    /// <summary>Raccourci global, ex. "Ctrl+Shift+V". Null tant que non choisi.</summary>
    public string? Hotkey { get; set; }

    public RetentionMode RetentionMode { get; set; } = RetentionMode.ByEntryCount;

    /// <summary>Jours de rétention si mode ByTime (défaut proposé : 30).</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Limite Mo si mode ByDiskSize (défaut proposé : 500).</summary>
    public int RetentionMaxMegabytes { get; set; } = 500;

    /// <summary>Limite d'entrées si mode ByEntryCount (défaut proposé : 500).</summary>
    public int RetentionMaxEntries { get; set; } = 500;

    public bool StartWithWindows { get; set; } = true;

    /// <summary>True = ne pas enregistrer les nouvelles copies.</summary>
    public bool CapturePaused { get; set; }

    /// <summary>Applications dont les copies ne sont pas historisées (D-CLIP-PRIVACY).</summary>
    public List<AppExclusionRule> Exclusions { get; set; } = new();
}
