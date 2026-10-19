namespace XtremClipboard.Models;

/// <summary>Application visible, pour le choix d'une exclusion dans Réglages.</summary>
public sealed record VisibleWindowApp(string ProcessName, string WindowTitle)
{
    public string Display => string.IsNullOrWhiteSpace(WindowTitle)
        ? ProcessName
        : $"{ProcessName} — {WindowTitle}";

    public override string ToString() => Display;
}
