namespace XtremClipboard.Services;

public interface IWindowsClipboardHistoryService
{
    /// <summary>Désactive l'historique presse-papiers Windows s'il est activé.</summary>
    bool TryDisableWindowsClipboardHistory(out string? message);

    /// <summary>Kill soft des hôtes UI clipboard (TextInputHost / ShellExperienceHost).</summary>
    void RestartClipboardUiHosts();

    /// <summary>
    /// Libère Win+V côté Explorer (DisabledHotkeys=V). Peut redémarrer Explorer une fois.
    /// </summary>
    bool TryReleaseWinVFromExplorer(out string? message);
}
