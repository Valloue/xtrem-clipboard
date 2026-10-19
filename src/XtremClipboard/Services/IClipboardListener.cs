namespace XtremClipboard.Services;

public interface IClipboardListener : IDisposable
{
    bool IsListening { get; }

    /// <summary>Doit être appelé sur le thread UI (HWND message).</summary>
    void Start();

    void Stop();

    /// <summary>Ignore les updates pendant qu'on écrit nous-mêmes dans le clipboard.</summary>
    IDisposable SuppressCapture();
}
