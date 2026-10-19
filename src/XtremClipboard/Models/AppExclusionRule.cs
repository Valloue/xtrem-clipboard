namespace XtremClipboard.Models;

/// <summary>
/// Règle d'exclusion : un processus, et éventuellement un extrait de titre de fenêtre.
/// </summary>
public sealed class AppExclusionRule
{
    /// <summary>Nom de fichier du processus, ex. KeePass.exe.</summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>Si vide, toute fenêtre de ce processus est exclue.</summary>
    public string TitleContains { get; set; } = string.Empty;
}
