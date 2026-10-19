namespace XtremClipboard.Services;

/// <summary>
/// Dossier de données utilisateur : LocalAppData\XtremSuite\XtremClipboard (D-CLIP-LOCAL).
/// L'exe de dev reste dans LocalAppData\XtremClipboard\dev-run.
/// </summary>
public static class AppDataPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XtremSuite",
        "XtremClipboard");

    public static string LegacyRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XtremClipboard");

    public static bool MigratedFromLegacy { get; private set; }

    /// <summary>Erreur de migration conservée pour être journalisée par App (logger pas encore prêt ici).</summary>
    public static Exception? MigrationError { get; private set; }

    /// <summary>Nombre d'éléments déplacés (settings.json, history, logs).</summary>
    public static int MovedItems { get; private set; }

    private static bool _ensured;

    /// <summary>
    /// Déplace settings.json, history\ et logs\ depuis l'ancien dossier s'ils n'existent pas encore au nouvel emplacement.
    /// Ne touche pas à dev-run.
    /// </summary>
    public static void EnsureMigrated()
    {
        if (_ensured)
            return;

        _ensured = true;

        try
        {
            Directory.CreateDirectory(Root);

            var moved = false;
            moved |= MoveFileIfAbsent("settings.json");
            moved |= MoveDirectoryIfAbsent("history");
            moved |= MoveDirectoryIfAbsent("logs");
            if (moved)
                MigratedFromLegacy = true;
        }
        catch (Exception ex)
        {
            // Le dossier neuf reste utilisable même si l'ancien est verrouillé —
            // l'erreur est journalisée par App dès que le logger est prêt.
            MigrationError ??= ex;
        }
    }

    private static bool MoveFileIfAbsent(string name)
    {
        var source = Path.Combine(LegacyRoot, name);
        var dest = Path.Combine(Root, name);
        if (!File.Exists(source) || File.Exists(dest))
            return false;

        try
        {
            File.Move(source, dest);
            MovedItems++;
            return true;
        }
        catch (Exception ex)
        {
            MigrationError ??= ex;
            return false;
        }
    }

    private static bool MoveDirectoryIfAbsent(string name)
    {
        var source = Path.Combine(LegacyRoot, name);
        var dest = Path.Combine(Root, name);
        if (!Directory.Exists(source) || Directory.Exists(dest))
            return false;

        try
        {
            Directory.Move(source, dest);
            MovedItems++;
            return true;
        }
        catch (Exception ex)
        {
            MigrationError ??= ex;
            return false;
        }
    }
}
