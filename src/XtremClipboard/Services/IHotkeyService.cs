using XtremClipboard.Models;

namespace XtremClipboard.Services;

public interface IHotkeyService : IDisposable
{
    event EventHandler? HotkeyPressed;

    bool IsRegistered { get; }

    /// <summary>Enregistre le raccourci global. false = conflit / échec.</summary>
    bool TryRegister(HotkeyGesture gesture, out string? errorMessage);

    void Unregister();
}
