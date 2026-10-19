namespace XtremClipboard.Models;

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
}
