namespace XtremClipboard.Services.Logging;

/// <summary>
/// Tampon des 50 dernières actions, joint aux lignes Error et Fatal.
/// Libellés seulement : pas de texte copié, d'aperçu, d'OCR, de titre ni de frappe.
/// </summary>
public static class Breadcrumbs
{
    public const int Max = 50;

    private static readonly Queue<string> Items = new();
    private static readonly object Gate = new();

    public static void Add(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return;

        var cleaned = action.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (cleaned.Length > 160)
            cleaned = cleaned[..160];

        var line = $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} {cleaned}";
        lock (Gate)
        {
            Items.Enqueue(line);
            while (Items.Count > Max)
                Items.Dequeue();
        }
    }

    public static IReadOnlyList<string> Snapshot()
    {
        lock (Gate)
            return Items.ToArray();
    }

    /// <summary>Isolation des tests. Pas un chemin produit.</summary>
    internal static void Reset()
    {
        lock (Gate)
            Items.Clear();
    }
}
