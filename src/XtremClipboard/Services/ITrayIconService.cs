namespace XtremClipboard.Services;

public interface ITrayIconService : IDisposable
{
    event EventHandler? OpenHistoryRequested;
    event EventHandler? OpenSettingsRequested;
    event EventHandler? ExitRequested;

    /// <summary>HWND propriétaire des messages tray (fenêtre shell cachée).</summary>
    void Start(IntPtr ownerHwnd);

    void Stop();
}
