using XtremClipboard.Models;

namespace XtremClipboard.Services;

public interface ISettingsService
{
    AppSettings Current { get; }

    string DataDirectory { get; }

    event EventHandler? SettingsChanged;

    Task LoadAsync();

    Task SaveAsync();

    /// <summary>Sauvegarde différée (D-SETTINGS temps réel).</summary>
    void ScheduleSave(int delayMs = 400);
}
