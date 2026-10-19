namespace XtremClipboard.Services;

/// <summary>Icône Xtrem CP (vert #008A3F), embarquée et copiée à côté de l'exe.</summary>
public static class AppIcon
{
    public static string? FilePath { get; } = Resolve();

    public static void Apply(Microsoft.UI.Windowing.AppWindow window)
    {
        if (FilePath is null)
            return;

        try
        {
            window.SetIcon(FilePath);
        }
        catch
        {
            // L'exe garde l'icône embarquée si le fichier est illisible.
        }
    }

    private static string? Resolve()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico");
        return File.Exists(path) ? path : null;
    }
}
