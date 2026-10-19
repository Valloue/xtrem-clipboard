namespace XtremClipboard.ViewModels;

/// <summary>Ligne affichée dans la liste des applications exclues.</summary>
public sealed class ExclusionEntry
{
    public ExclusionEntry(string processName, string titleContains)
    {
        ProcessName = processName;
        TitleContains = titleContains;
    }

    public string ProcessName { get; }

    public string TitleContains { get; }

    public string Display => string.IsNullOrWhiteSpace(TitleContains)
        ? ProcessName
        : $"{ProcessName} — titre contient « {TitleContains} »";
}
