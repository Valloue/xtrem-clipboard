using Microsoft.Extensions.Logging;

namespace XtremClipboard.Services;

/// <summary>
/// Une seule instance. La seconde signale la première (overlay / fenêtre).
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\XtremClipboard.SingleInstance";
    private const string EventName = @"Local\XtremClipboard.Activate";

    private readonly ILogger<SingleInstanceGuard> _logger;
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _event;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public bool IsPrimary { get; }

    public event EventHandler? PeerActivated;

    public SingleInstanceGuard(ILogger<SingleInstanceGuard> logger)
    {
        _logger = logger;
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        _mutex = new Mutex(false, MutexName);

        try
        {
            IsPrimary = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // L'instance précédente est morte sans libérer le mutex
            _logger.LogWarning("Mutex abandonné : l'instance précédente s'est arrêtée brutalement");
            IsPrimary = true;
        }

        if (IsPrimary)
            _ = Task.Run(Listen);
    }

    public void SignalPeer()
    {
        try
        {
            _event.Set();
        }
        catch (Exception ex)
        {
            // instance déjà partie
            _logger.LogDebug(ex, "Signal à la première instance impossible — déjà partie");
        }
    }

    private void Listen()
    {
        while (!_cts.IsCancellationRequested)
        {
            var signaled = false;
            try
            {
                signaled = _event.WaitOne(500);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (signaled && !_cts.IsCancellationRequested)
                PeerActivated?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _event.Set(); } catch (Exception ex) { _logger.LogDebug(ex, "Signal final ignoré"); }
        _event.Dispose();
        try
        {
            if (IsPrimary)
                _mutex.ReleaseMutex();
        }
        catch (Exception ex)
        {
            // ignore
            _logger.LogDebug(ex, "Libération mutex ignorée");
        }

        _mutex.Dispose();
        _cts.Dispose();
    }
}
