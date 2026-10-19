using XtremClipboard.Models;

namespace XtremClipboard.Services;

public interface IClipboardHistoryStore
{
    IReadOnlyList<ClipboardEntry> Entries { get; }

    event EventHandler? HistoryChanged;

    void Load();

    /// <summary>Ajoute un snapshot texte. Retourne null si dédup consécutive.</summary>
    ClipboardEntry? AddText(string text);

    /// <summary>Ajoute un snapshot image PNG déjà écrit. Retourne null si dédup.</summary>
    ClipboardEntry? AddImagePng(byte[] pngBytes, string? contentHash = null);

    Task<string?> ReadTextPayloadAsync(ClipboardEntry entry);

    Task<byte[]?> ReadImagePayloadAsync(ClipboardEntry entry);

    string GetAbsolutePayloadPath(ClipboardEntry entry);

    bool TryGet(Guid id, out ClipboardEntry? entry);

    bool SetPinned(Guid id, bool pinned);

    bool Delete(Guid id);

    /// <summary>Supprime les entrées non épinglées. Retourne le nombre retiré.</summary>
    int ClearUnpinned();

    /// <summary>Recherche dans l'aperçu et, pour le texte, dans le fichier payload.</summary>
    bool MatchesSearch(ClipboardEntry entry, string query);

    void ApplyRetention();
}
