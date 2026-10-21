namespace XtremClipboard.Services;

/// <summary>
/// Issu d'une extraction OCR — distingue le succès (avec ou sans texte) des échecs techniques
/// (revue §6 : un échec ne doit jamais masquer en « Completed sans texte »).
/// </summary>
public enum OcrOutcome
{
    /// <summary>OCR exécuté : texte extrait ou absence de texte détecté.</summary>
    Completed,

    /// <summary>Moteur ou modèles indisponibles (fichiers manquants, échec d'init).</summary>
    EngineUnavailable,

    /// <summary>Image illisible (décodage PNG impossible).</summary>
    DecodeFailed,

    /// <summary>Erreur inattendue pendant l'inférence.</summary>
    Failed
}

/// <summary>
/// Résultat OCR : <see cref="Text"/> est vide si aucun texte n'a été détecté.
/// Le texte est du contenu clipboard — ne jamais le journaliser (D-CLIP-PRIVACY).
/// </summary>
public sealed record OcrResult(OcrOutcome Outcome, string Text)
{
    public static OcrResult Empty(OcrOutcome outcome) => new(outcome, string.Empty);
}

/// <summary>
/// Service d'extraction OCR à partir d'une image PNG du presse-papiers.
/// Aucun contenu clipboard (texte ou OCR) ne doit être journalisé en clair.
/// </summary>
public interface IOcrService
{
    /// <summary>Indique si le moteur est prêt (modèles chargés).</summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Extrait le texte OCR d'une image PNG. Ne doit jamais être appelé sur le thread UI.
    /// Retourne <see cref="OcrOutcome.Completed"/> avec un texte vide quand aucune région
    /// n'est détectée, et un statut d'échec distinct quand l'inférence n'a pas pu aboutir.
    /// </summary>
    Task<OcrResult> ExtractTextAsync(byte[] imageData);
}
