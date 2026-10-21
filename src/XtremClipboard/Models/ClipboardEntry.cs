namespace XtremClipboard.Models;

/// <summary>
/// Statut d'analyse OCR d'une image clipboard (index métadonnées uniquement).
/// </summary>
public enum ClipboardOcrStatus
{
    /// <summary>Aucune analyse demandée.</summary>
    None,
    /// <summary>Analyse OCR en cours ou en attente.</summary>
    Pending,
    /// <summary>Analyse OCR terminée, texte disponible.</summary>
    Completed,
    /// <summary>Analyse OCR échouée ou refusée.</summary>
    Failed
}

/// <summary>
/// Snapshot d'une entrée presse-papiers (métadonnées index — pas de lien live).
/// </summary>
public sealed class ClipboardEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public ClipboardEntryKind Kind { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsPinned { get; set; }

    /// <summary>Aperçu tronqué pour l'UI — jamais loggé en clair.</summary>
    public string? TextPreview { get; set; }

    /// <summary>Fichier relatif sous history/, ex. payloads\{id}.txt</summary>
    public string PayloadFile { get; set; } = string.Empty;

    public long ByteSize { get; set; }

    /// <summary>Hash contenu pour dédup consécutive (hex SHA-256).</summary>
    public string? ContentHash { get; set; }

    /// <summary>Statut OCR (métadonnées uniquement — le texte OCR reste dans le fichier payload).</summary>
    public ClipboardOcrStatus OcrStatus { get; set; } = ClipboardOcrStatus.None;

    /// <summary>Fichier OCR relatif sous history/, ex. payloads\{id}.ocr.txt.</summary>
    public string? OcrPayloadFile { get; set; }
}
