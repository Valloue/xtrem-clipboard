using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using XtremClipboard.Models;
using XtremClipboard.Services.Native;

namespace XtremClipboard.Services;

/// <summary>
/// Remet un snapshot dans le clipboard puis Ctrl+V vers l'app précédente.
/// </summary>
public sealed class PasteService : IPasteService
{
    private readonly IClipboardHistoryStore _store;
    private readonly IClipboardListener _listener;
    private readonly ILogger<PasteService> _logger;

    public IntPtr TargetHwnd { get; set; }

    public PasteService(
        IClipboardHistoryStore store,
        IClipboardListener listener,
        ILogger<PasteService> logger)
    {
        _store = store;
        _listener = listener;
        _logger = logger;
    }

    public async Task CopyToClipboardAsync(ClipboardEntry entry)
    {
        using (_listener.SuppressCapture())
        {
            var package = new DataPackage();
            if (entry.Kind == ClipboardEntryKind.Text)
            {
                var text = await _store.ReadTextPayloadAsync(entry).ConfigureAwait(true);
                if (text is null)
                {
                    _logger.LogWarning("Payload texte introuvable — collage annulé (id={Id})", entry.Id);
                    return;
                }
                package.SetText(text);
            }
            else
            {
                var png = await _store.ReadImagePayloadAsync(entry).ConfigureAwait(true);
                if (png is null)
                {
                    _logger.LogWarning("Payload image introuvable — collage annulé (id={Id})", entry.Id);
                    return;
                }

                var mem = new InMemoryRandomAccessStream();
                var writer = new DataWriter(mem);
                writer.WriteBytes(png);
                await writer.StoreAsync();
                await writer.FlushAsync();
                mem.Seek(0);
                package.SetBitmap(RandomAccessStreamReference.CreateFromStream(mem));
            }

            Clipboard.SetContent(package);
            Clipboard.Flush();
            // Laisser le suppress un peu après Flush
            await Task.Delay(80).ConfigureAwait(true);
        }

        _logger.LogInformation("Contenu remis dans le clipboard ({Kind})", entry.Kind);
    }

    public async Task PasteAsync(ClipboardEntry entry)
    {
        var target = TargetHwnd;
        await CopyToClipboardAsync(entry).ConfigureAwait(true);

        if (target == IntPtr.Zero || target == NativeMethods.GetForegroundWindow())
        {
            _logger.LogInformation("Collage : pas de fenêtre cible — contenu seulement dans le clipboard");
            return;
        }

        ForceForeground(target);
        await Task.Delay(40).ConfigureAwait(true);
        SendCtrlV();
        _logger.LogInformation("Collage Ctrl+V envoyé");
    }

    private static void ForceForeground(IntPtr hwnd)
    {
        var fg = NativeMethods.GetForegroundWindow();
        var fgThread = NativeMethods.GetWindowThreadProcessId(fg, out _);
        var ourThread = NativeMethods.GetCurrentThreadId();

        if (fgThread != ourThread)
            NativeMethods.AttachThreadInput(ourThread, fgThread, true);

        NativeMethods.ShowWindow(hwnd, 9);
        NativeMethods.SetForegroundWindow(hwnd);

        if (fgThread != ourThread)
            NativeMethods.AttachThreadInput(ourThread, fgThread, false);
    }

    private static void SendCtrlV()
    {
        var inputs = new NativeMethods.Input[4];
        inputs[0] = Key(NativeMethods.VkControl, keyUp: false);
        inputs[1] = Key(NativeMethods.VkV, keyUp: false);
        inputs[2] = Key(NativeMethods.VkV, keyUp: true);
        inputs[3] = Key(NativeMethods.VkControl, keyUp: true);
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
    }

    private static NativeMethods.Input Key(int vk, bool keyUp) => new()
    {
        type = NativeMethods.InputKeyboard,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KeybdInput
            {
                wVk = (ushort)vk,
                dwFlags = keyUp ? (uint)NativeMethods.KeyeventfKeyup : 0
            }
        }
    };
}
