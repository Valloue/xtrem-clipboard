using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using XtremClipboard.Models;
using XtremClipboard.Services.Native;

namespace XtremClipboard.Services;

/// <summary>
/// Écoute Win32 AddClipboardFormatListener (**D-CLIP-LISTEN-API**) + snapshot WinRT.
/// Une rafale de notifications est fusionnée par <see cref="ClipboardCaptureScheduler"/> ;
/// deux encodages PNG du même bitmap sont une seule image via <see cref="ImageContentHasher"/>.
/// </summary>
public sealed class ClipboardListener : IClipboardListener
{
    private static readonly EventId ImageDedupIgnored = new(7410, "CLIP-IMG-DEDUP");
    private static readonly EventId SequenceUnchanged = new(7411, "CLIP-SEQ-SKIP");

    private readonly IClipboardHistoryStore _store;
    private readonly ISettingsService _settings;
    private readonly IAppExclusionService _exclusions;
    private readonly ILogger<ClipboardListener> _logger;
    private readonly ClipboardCaptureScheduler _scheduler = new();
    private NativeMethods.WndProc? _wndProcKeepAlive;
    private DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _silenceTimer;
    private IntPtr _hwnd;
    private bool _classRegistered;
    private bool _disposed;
    private int _busy; // 0/1 — capture en cours
    private int _pending; // 0/1 — mise à jour arrivée pendant une capture (reprise seulement si la séquence change)
    private int _suppressDepth;
    private bool _hasOwnedSequence;
    private uint _ownedSequence;
    private bool _deferredDuringSuppress;
    private uint _deferredSequence;

    private enum CaptureCoreKind
    {
        Read = 0,
        Suppressed = 1,
        Skipped = 2
    }

    private readonly record struct CaptureCoreResult(bool Read, uint Sequence, CaptureCoreKind Kind);

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

        StopSilenceTimer();

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
            if (o is null)
                return;

            var depth = Interlocked.Decrement(ref o._suppressDepth);
            if (depth == 0)
                o.OnSuppressReleased();
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
            _dispatcher?.TryEnqueue(OnClipboardUpdated);
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Relit une fois le presse-papiers courant (rattrapage fin d'init — BUG-5 testeur :
    /// copies perdues silencieusement pendant l'initialisation). La dédup par hash du
    /// store garantit l'absence de doublon. À appeler sur le thread UI.
    /// </summary>
    public void CaptureOnce()
    {
        if (_disposed)
            return;

        var sequence = NativeMethods.GetClipboardSequenceNumber();
        var now = Environment.TickCount64;
        if (IsOwnedSequence(sequence))
        {
            _logger.LogDebug(SequenceUnchanged, "Relecture ignorée : numéro de séquence inchangé");
            return;
        }

        if (Volatile.Read(ref _busy) == 1)
        {
            Interlocked.Exchange(ref _pending, 1);
            _scheduler.OnCatchUp(sequence, now);
            return;
        }

        var catchUp = _scheduler.OnCatchUp(sequence, now);
        if (catchUp.Schedule == CaptureSchedule.SkipSameSequence)
            _logger.LogDebug(SequenceUnchanged, "Relecture ignorée : numéro de séquence inchangé");

        Apply(catchUp);
    }

    private void OnClipboardUpdated()
    {
        if (_disposed)
            return;

        var sequence = NativeMethods.GetClipboardSequenceNumber();
        var now = Environment.TickCount64;

        if (Volatile.Read(ref _suppressDepth) > 0)
        {
            NoteWhileSuppressed(sequence, now);
            return;
        }

        if (IsOwnedSequence(sequence))
        {
            _logger.LogDebug(SequenceUnchanged, "Relecture ignorée : numéro de séquence inchangé");
            return;
        }

        if (Volatile.Read(ref _busy) == 1)
        {
            Interlocked.Exchange(ref _pending, 1);
            _scheduler.OnNotification(sequence, now);
            return;
        }

        var decision = _scheduler.OnNotification(sequence, now);
        if (decision.Schedule == CaptureSchedule.SkipSameSequence)
            _logger.LogDebug(SequenceUnchanged, "Relecture ignorée : numéro de séquence inchangé");

        Apply(decision);
    }

    /// <summary>
    /// Écriture faite par l'app (collage). La première séquence vue est la nôtre.
    /// Un numéro différent dans la même fenêtre est un vrai contenu : on le lit après.
    /// </summary>
    private void NoteWhileSuppressed(uint sequence, long nowMs)
    {
        if (!_hasOwnedSequence)
        {
            _hasOwnedSequence = true;
            _ownedSequence = sequence;
            return;
        }

        if (sequence == _ownedSequence)
            return;

        if (Volatile.Read(ref _busy) == 1)
        {
            Interlocked.Exchange(ref _pending, 1);
            _scheduler.OnNotification(sequence, nowMs);
            return;
        }

        _deferredDuringSuppress = true;
        _deferredSequence = sequence;
    }

    private void OnSuppressReleased()
    {
        if (_disposed || !_deferredDuringSuppress)
            return;

        _deferredDuringSuppress = false;
        var sequence = _deferredSequence;
        var now = Environment.TickCount64;

        void Arm()
        {
            if (_disposed)
                return;

            if (Volatile.Read(ref _busy) == 1)
            {
                Interlocked.Exchange(ref _pending, 1);
                _scheduler.OnNotification(sequence, now);
                return;
            }

            Apply(_scheduler.OnNotification(sequence, now));
        }

        if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
            _dispatcher.TryEnqueue(Arm);
        else
            Arm();
    }

    private bool IsOwnedSequence(uint sequence) =>
        _hasOwnedSequence && sequence == _ownedSequence;

    private void Apply(ScheduleResult result)
    {
        if (_disposed)
            return;

        switch (result.Schedule)
        {
            case CaptureSchedule.Wait:
                ArmSilence(result.WaitMs);
                break;
            case CaptureSchedule.Capture:
                StartCapture();
                break;
            default:
                StopSilenceTimer();
                break;
        }
    }

    private void ArmSilence(int waitMs)
    {
        if (_dispatcher is null)
            return;

        if (_silenceTimer is null)
        {
            _silenceTimer = _dispatcher.CreateTimer();
            _silenceTimer.IsRepeating = false;
            _silenceTimer.Tick += OnSilenceElapsed;
        }

        var clamped = Math.Clamp(waitMs, 1, ClipboardCaptureScheduler.SilenceWindowMs);
        _silenceTimer.Interval = TimeSpan.FromMilliseconds(clamped);
        _silenceTimer.Start();
    }

    private void OnSilenceElapsed(DispatcherQueueTimer sender, object args) =>
        Apply(_scheduler.OnClock(Environment.TickCount64));

    private void StopSilenceTimer()
    {
        if (_silenceTimer is { IsRunning: true })
            _silenceTimer.Stop();
    }

    private void StartCapture()
    {
        StopSilenceTimer();
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            // Déjà en lecture : la notification retenue est dans l'ordonnanceur.
            Interlocked.Exchange(ref _pending, 1);
            return;
        }

        _scheduler.BeginCapture();
        _ = RunCaptureAsync();
    }

    private async Task RunCaptureAsync()
    {
        var result = new CaptureCoreResult(false, 0, CaptureCoreKind.Skipped);
        try
        {
            try
            {
                result = await CaptureCoreAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // Clipboard verrouillé / contenu illisible — ne jamais logger le contenu.
                _logger.LogWarning(ex, "Capture clipboard échouée — une reprise est tentée");
                await Task.Delay(250).ConfigureAwait(true);
                result = await CaptureCoreAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture clipboard abandonnée après reprise");
        }
        finally
        {
            if (result.Kind == CaptureCoreKind.Skipped)
                _scheduler.DropHeld();

            var schedule = _scheduler.EndCapture(
                result.Read,
                result.Sequence,
                Environment.TickCount64,
                suppressed: result.Kind == CaptureCoreKind.Suppressed);

            Interlocked.Exchange(ref _busy, 0);
            var pending = Interlocked.Exchange(ref _pending, 0) == 1;

            if (pending && schedule.Schedule == CaptureSchedule.SkipSameSequence)
                _logger.LogDebug(SequenceUnchanged, "Relecture ignorée : numéro de séquence inchangé");

            Apply(schedule);
        }
    }

    private async Task<CaptureCoreResult> CaptureCoreAsync()
    {
        var sequence = NativeMethods.GetClipboardSequenceNumber();

        if (Volatile.Read(ref _suppressDepth) > 0)
        {
            // Écriture de l'app : ne pas historiser. On réarme sauf si c'est déjà notre séquence.
            if (IsOwnedSequence(sequence))
                return new CaptureCoreResult(true, sequence, CaptureCoreKind.Read);

            return new CaptureCoreResult(false, sequence, CaptureCoreKind.Suppressed);
        }

        if (_settings.Current.CapturePaused)
            return new CaptureCoreResult(false, sequence, CaptureCoreKind.Skipped);

        if (_exclusions.IsClipboardOwnerExcluded())
        {
            _logger.LogDebug("Capture ignorée : application exclue");
            return new CaptureCoreResult(false, sequence, CaptureCoreKind.Skipped);
        }

        if (IsOwnedSequence(sequence))
        {
            _logger.LogDebug(SequenceUnchanged, "Relecture ignorée : numéro de séquence inchangé");
            return new CaptureCoreResult(true, sequence, CaptureCoreKind.Read);
        }

        var content = Clipboard.GetContent();
        if (content is null)
            return new CaptureCoreResult(true, sequence, CaptureCoreKind.Read);

        if (content.Contains(StandardDataFormats.Text))
        {
            var text = await content.GetTextAsync();
            if (!string.IsNullOrEmpty(text))
            {
                if (!MustNotHistorize())
                    _store.AddText(text);
                return new CaptureCoreResult(true, sequence, CaptureCoreKind.Read);
            }
        }

        if (content.Contains(StandardDataFormats.Bitmap))
        {
            var bmpRef = await content.GetBitmapAsync();
            var (png, hash) = await EncodePngAsync(bmpRef).ConfigureAwait(true);
            if (png is { Length: > 0 } && !MustNotHistorize())
            {
                // Une entrée déjà stockée par une version qui hashait le PNG a une
                // empreinte différente pour les mêmes pixels. On compare alors le fichier.
                if (hash is not null && await SamePixelsAsNewestImageAsync(hash).ConfigureAwait(true))
                {
                    _logger.LogInformation(ImageDedupIgnored, "Image ignorée (dédup consécutive)");
                }
                else
                {
                    var created = _store.AddImagePng(png, hash);
                    if (created is null)
                        _logger.LogInformation(ImageDedupIgnored, "Image ignorée (dédup consécutive)");
                }
            }
        }

        return new CaptureCoreResult(true, sequence, CaptureCoreKind.Read);
    }

    private bool MustNotHistorize()
    {
        if (Volatile.Read(ref _suppressDepth) > 0)
            return true;

        return IsOwnedSequence(NativeMethods.GetClipboardSequenceNumber());
    }

    /// <summary>
    /// Vrai si l'image en tête a les mêmes pixels. Le hash stocké peut être celui
    /// du fichier PNG (exe antérieur) : on redécode alors ce fichier.
    /// </summary>
    private async Task<bool> SamePixelsAsNewestImageAsync(string hash)
    {
        var newest = _store.Entries.FirstOrDefault();
        if (newest is null || newest.Kind != ClipboardEntryKind.Image)
            return false;

        if (string.Equals(newest.ContentHash, hash, StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var bytes = await _store.ReadImagePayloadAsync(newest).ConfigureAwait(true);
            if (bytes is null || bytes.Length == 0)
                return false;

            var existing = await HashPngBytesAsync(bytes).ConfigureAwait(true);
            return existing is not null
                   && string.Equals(existing, hash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Comparaison avec l'image précédente impossible ({ExceptionType})", ex.GetType().Name);
            return false;
        }
    }

    private async Task<string?> HashPngBytesAsync(byte[] png)
    {
        using var mem = new InMemoryRandomAccessStream();
        var writer = new DataWriter(mem.GetOutputStreamAt(0));
        writer.WriteBytes(png);
        await writer.StoreAsync();
        writer.DetachStream();
        writer.Dispose();
        mem.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(mem);
        using var software = await decoder.GetSoftwareBitmapAsync();
        var bgra = software.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                   && software.BitmapAlphaMode == BitmapAlphaMode.Premultiplied
            ? software
            : SoftwareBitmap.Convert(software, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        try
        {
            return TryHashBgra(bgra);
        }
        finally
        {
            if (!ReferenceEquals(bgra, software))
                bgra.Dispose();
        }
    }

    private async Task<(byte[]? Png, string? ContentHash)> EncodePngAsync(IRandomAccessStreamReference bitmapRef)
    {
        using var input = await bitmapRef.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(input);
        using var software = await decoder.GetSoftwareBitmapAsync();
        var bgra = software.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                   && software.BitmapAlphaMode == BitmapAlphaMode.Premultiplied
            ? software
            : SoftwareBitmap.Convert(software, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        try
        {
            var hash = TryHashBgra(bgra);
            var mem = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, mem);
            encoder.SetSoftwareBitmap(bgra);
            await encoder.FlushAsync();

            mem.Seek(0);
            var size = (uint)mem.Size;
            if (size == 0)
                return (null, hash);

            var reader = new DataReader(mem.GetInputStreamAt(0));
            await reader.LoadAsync(size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            return (bytes, hash);
        }
        finally
        {
            if (!ReferenceEquals(bgra, software))
                bgra.Dispose();
        }
    }

    /// <summary>Empreinte pixels. En cas d'échec, null : le store retombe sur le hash du PNG.</summary>
    private string? TryHashBgra(SoftwareBitmap bgra)
    {
        try
        {
            var width = bgra.PixelWidth;
            var height = bgra.PixelHeight;
            if (width <= 0 || height <= 0)
                return null;

            var length = checked(width * height * 4);
            var pixels = new byte[length];
            bgra.CopyToBuffer(pixels.AsBuffer());
            return ImageContentHasher.HashBgra(width, height, pixels);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Empreinte pixels indisponible");
            return null;
        }
    }
}
