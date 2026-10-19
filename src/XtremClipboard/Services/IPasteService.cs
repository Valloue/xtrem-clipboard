using XtremClipboard.Models;

namespace XtremClipboard.Services;

public interface IPasteService
{
    /// <summary>HWND cible pour Ctrl+V (fenêtre avant ouverture overlay).</summary>
    IntPtr TargetHwnd { get; set; }

    Task CopyToClipboardAsync(ClipboardEntry entry);

    /// <summary>Remet le contenu puis simule Ctrl+V vers TargetHwnd.</summary>
    Task PasteAsync(ClipboardEntry entry);
}
