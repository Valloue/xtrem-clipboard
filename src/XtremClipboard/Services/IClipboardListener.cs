namespace XtremClipboard.Services;

public interface IClipboardListener : IDisposable
{
    bool IsListening { get; }

    /// <summary>Doit être appelé sur le thread UI (HWND message).</summary>
    void Start();

    void Stop();

    /// <summary>Ignore les updates pendant qu'on écrit nous-mêmes dans le clipboard.</summary>
    IDisposable SuppressCapture();

    /// <summary>
    /// Relit une fois le presse-papiers courant (rattrapage post-initialisation :
    /// aucune copie faite pendant l'init ne doit être perdue — la dédup par hash du
    /// store évite les doublons). À appeler sur le thread UI, écoute active.
    /// </summary>
    void CaptureOnce();
}
