namespace XtremClipboard.Services;

/// <summary>
/// Empêche Win+V d'ouvrir le panneau Windows et notifie l'app (**D-CLIP-WINV-BLOCK**).
/// </summary>
public interface IWinVSuppressService : IDisposable
{
    bool IsActive { get; }

    /// <summary>Levée quand Win+V est intercepté (panneau Windows bloqué).</summary>
    event EventHandler? WinVPressed;

    void Start();

    void Stop();

    /// <summary>Suspend l'avalement Win+V (ex. pendant capture d'un raccourci custom).</summary>
    void Pause();

    /// <summary>Reprend l'interception Win+V après Pause.</summary>
    void Resume();
}
