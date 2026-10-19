using XtremClipboard.Models;

namespace XtremClipboard.Services;

/// <summary>
/// Filtre vie privée : ne pas historiser certaines applications (**D-CLIP-PRIVACY**).
/// </summary>
public interface IAppExclusionService
{
    /// <summary>True si le propriétaire actuel du presse-papiers correspond à une exclusion.</summary>
    bool IsClipboardOwnerExcluded();

    /// <summary>Normalise une saisie utilisateur en nom de fichier de processus, ou null si invalide.</summary>
    string? NormalizeProcessName(string? raw);

    bool IsSelfProcess(string processName);

    /// <summary>True si le processus est un gestionnaire de mots de passe exclu en dur.</summary>
    bool IsBuiltInPasswordManager(string processName);

    IReadOnlyList<VisibleWindowApp> ListVisibleApps();
}
