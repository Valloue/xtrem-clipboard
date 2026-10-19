using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using XtremClipboard.Services.Native;

namespace XtremClipboard.Services;

/// <summary>
/// Écoute Win32 AddClipboardFormatListener (**D-CLIP-LISTEN-API**) + snapshot WinRT.
/// </summary>
public sealed class ClipboardListener : IClipboardListener
{
    private readonly IClipboardHistoryStore _store;
    private readonly ISettingsService _settings;
    private readonly IAppExclusionService _exclusions;
    private readonly ILogger<ClipboardListener> _logger;
    private NativeMethods.WndProc? _wndProcKeepAlive;
    private DispatcherQueue? _dispatcher;
    private IntPtr _hwnd;
    private bool _classRegistered;
    private bool _disposed;
    private int _busy; // 0/1 — ignore updates pendant lecture
    private int _suppressDepth;

    public bool IsListening { get; private set; }

    public ClipboardListener(
        IClipboardHistoryStore store,
        ISettingsService settings,
        IAppExclusionService exclusions,
        ILogger<ClipboardListener> logger)
    {
        _store = store;
        _settings = settings;
        _exclusions = exclusions;
        _logger = logger;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsListening)
            return;

        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("ClipboardListener.Start nécessite le thread UI.");

        _wndProcKeepAlive = WndProc;
        EnsureMessageWindow();

        if (!NativeMethods.AddClipboardFormatListener(_hwnd))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AddClipboardFormatListener échoué");

        IsListening = true;
        _logger.LogInformation("Écoute presse-papiers active (AddClipboardFormatListener)");
    }

    public void Stop()
    {
        if (IsListening)
            _logger.LogInformation("Écoute presse-papiers arrêtée");

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.RemoveClipboardFormatListener(_hwnd);
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        IsListening = false;
    }

    public IDisposable SuppressCapture()
    {
        Interlocked.Increment(ref _suppressDepth);
        return new SuppressScope(this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private sealed class SuppressScope : IDisposable
    {
        private ClipboardListener? _owner;

        public SuppressScope(ClipboardListener owner) => _owner = owner;

        public void Dispose()
        {
            var o = Interlocked.Exchange(ref _owner, null);
            if (o is not null)
                Interlocked.Decrement(ref o._suppressDepth);
        }
    }

    private void EnsureMessageWindow()
    {
        if (_hwnd != IntPtr.Zero)
            return;

        var className = "XtremClipboard.ClipboardListenerWindow";
        var wndClass = new NativeMethods.WndClass
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive!),
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = className
        };

        if (!_classRegistered)
        {
            var atom = NativeMethods.RegisterClass(ref wndClass);
            if (atom == 0)
            {
                var err = Marshal.GetLastWin32Error();
                if (err != 1410) // already registered
                    throw new Win32Exception(err);
            }

            _classRegistered = true;
        }

        _hwnd = NativeMethods.CreateWindowEx(
            0, className, string.Empty, 0,
            0, 0, 0, 0,
            NativeMethods.HwndMessage,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WmClipboardUpdate)
        {
            _dispatcher?.TryEnqueue(() => _ = CaptureAsync());
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private async Task CaptureAsync()
    {
        if (Volatile.Read(ref _suppressDepth) > 0)
            return;

        if (_settings.Current.CapturePaused)
            return;

        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;

        try
        {
            // Petit délai : certaines apps remplissent le clipboard en deux temps
            await Task.Delay(50).ConfigureAwait(true);

            if (Volatile.Read(ref _suppressDepth) > 0)
                return;

            if (_exclusions.IsClipboardOwnerExcluded())
            {
                _logger.LogDebug("Capture ignorée : application exclue");
                return;
            }

            var content = Clipboard.GetContent();
            if (content is null)
                return;

            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                if (!string.IsNullOrEmpty(text))
                {
                    _store.AddText(text);
                    return;
                }
            }

            if (content.Contains(StandardDataFormats.Bitmap))
            {
                var bmpRef = await content.GetBitmapAsync();
                var png = await EncodePngAsync(bmpRef).ConfigureAwait(true);
                if (png is { Length: > 0 })
                    _store.AddImagePng(png);
            }
        }
        catch (Exception ex)
        {
            // Contenu non lisible / verrouillé — normal, ne pas logger le contenu
            _logger.LogDebug(ex, "Capture clipboard ignorée");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static async Task<byte[]?> EncodePngAsync(IRandomAccessStreamReference bitmapRef)
    {
        using var input = await bitmapRef.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(input);
        var software = await decoder.GetSoftwareBitmapAsync();

        var mem = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, mem);
        encoder.SetSoftwareBitmap(SoftwareBitmap.Convert(software, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied));
        await encoder.FlushAsync();

        mem.Seek(0);
        var size = (uint)mem.Size;
        if (size == 0)
            return null;

        var reader = new DataReader(mem.GetInputStreamAt(0));
        await reader.LoadAsync(size);
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
