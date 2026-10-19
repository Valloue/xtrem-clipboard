namespace XtremClipboard.Models;

/// <summary>Contenu de history/index.json (**D-STORE-LAYOUT**).</summary>
public sealed class HistoryIndex
{
    public int Version { get; set; } = 1;

    public List<ClipboardEntry> Entries { get; set; } = [];
}
