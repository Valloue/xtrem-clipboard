namespace XtremClipboard.Services.Logging;

/// <summary>
/// GUID anonyme créé au premier lancement, à la racine des données (pas dans logs\).
/// Jamais le nom de machine ni le compte. Échec : <c>unknown</c>, le démarrage continue.
/// </summary>
public static class InstallId
{
    public const string FileName = "installation-id.txt";

    public static string GetOrCreate(string rootDirectory)
    {
        try
        {
            Directory.CreateDirectory(rootDirectory);
            var file = Path.Combine(rootDirectory, FileName);
            if (File.Exists(file))
            {
                var existing = File.ReadAllText(file).Trim();
                if (Guid.TryParse(existing, out _))
                    return existing;
            }

            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(file, id);
            return id;
        }
        catch
        {
            return "unknown";
        }
    }
}
