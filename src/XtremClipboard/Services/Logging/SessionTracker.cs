using System.Diagnostics;
using Serilog;

namespace XtremClipboard.Services.Logging;

/// <summary>
/// Marqueur <c>logs\session.marker</c> et drapeau <c>logs\session.crash</c>.
/// Un arrêt propre passe par <see cref="Close"/> ; <c>ProcessExit</c> ne retire rien.
/// </summary>
public sealed class SessionTracker
{
    public const string CrashIntercepted = "crash intercepté";
    public const string KilledOrWindowsShutdown = "tuée ou arrêt Windows (aucun arrêt propre)";
    public const string MarkerFileName = "session.marker";
    public const string CrashFileName = "session.crash";

    private readonly ILogger _log;
    private readonly string _logDirectory;
    private readonly string _markerPath;
    private readonly string _crashFlagPath;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private bool _opened;
    private bool _closed;

    public SessionTracker(ILogger log, string logDirectory, string version, string commit, string sessionId)
    {
        _log = log;
        _logDirectory = logDirectory;
        Version = version;
        Commit = commit;
        SessionId = sessionId;
        _markerPath = Path.Combine(logDirectory, MarkerFileName);
        _crashFlagPath = Path.Combine(logDirectory, CrashFileName);
    }

    public string Version { get; }

    public string Commit { get; }

    public string SessionId { get; }

    public bool IsOpen => _opened;

    /// <summary>
    /// Marqueur absent → null. Drapeau crash → crash intercepté.
    /// Sinon (y compris l'ancien fichier qui ne contient qu'une date) → tuée ou arrêt Windows.
    /// </summary>
    public static string? Classify(string logDirectory)
    {
        var marker = Path.Combine(logDirectory, MarkerFileName);
        if (!File.Exists(marker))
            return null;

        var crash = Path.Combine(logDirectory, CrashFileName);
        return File.Exists(crash) ? CrashIntercepted : KilledOrWindowsShutdown;
    }

    public void Open()
    {
        var abnormal = Classify(_logDirectory);
        if (abnormal is not null)
        {
            _log.Warning(
                "Arrêt anormal de la session précédente : {AbnormalExitType}. Marqueur : {PreviousSession} {EventCode} {Category} {Result}",
                abnormal,
                TryRead(_markerPath),
                "CLIP-LIFE-003",
                "crash",
                "arret-anormal");
        }

        TryDelete(_crashFlagPath);

        try
        {
            Directory.CreateDirectory(_logDirectory);
            File.WriteAllText(
                _markerPath,
                $"{SessionId}|{DateTimeOffset.UtcNow:o}|{Version}+{Commit}");
            _opened = true;
            _log.Information(
                "Session ouverte {EventCode} {Category} {Result}",
                "CLIP-LIFE-001",
                "cycle",
                "ok");
        }
        catch (Exception ex)
        {
            _log.Warning(
                ex,
                "Marqueur de session indisponible — détection d'arrêt anormal désactivée {EventCode} {Category} {Result}",
                "CLIP-LIFE-004",
                "cycle",
                "degrade");
        }
    }

    /// <summary>Posé par un crash qui termine le process. N'efface pas le marqueur.</summary>
    public void MarkCrash()
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            File.WriteAllText(_crashFlagPath, "1");
        }
        catch
        {
            // Le drapeau manquant sera lu comme un process tué, pas comme une boucle de log.
        }
    }

    /// <summary>
    /// Arrêt propre. Retire marqueur et drapeau seulement si cette session les a posés
    /// (une seconde instance ne doit pas effacer la session principale).
    /// </summary>
    public void Close(string reason, int exitCode = 0)
    {
        if (_closed)
            return;

        _closed = true;
        _log.Information(
            "Session fermée. Raison : {ShutdownReason}, durée {DurationMs} ms, code {ExitCode} {EventCode} {Category} {Result}",
            reason,
            _uptime.ElapsedMilliseconds,
            exitCode,
            "CLIP-LIFE-002",
            "cycle",
            "ferme");

        if (_opened)
        {
            TryDelete(_markerPath);
            TryDelete(_crashFlagPath);
            _opened = false;
        }

        TryDisposeLogger();
    }

    private void TryDisposeLogger()
    {
        try
        {
            if (ReferenceEquals(Log.Logger, _log))
                Log.CloseAndFlush();
            else if (_log is IDisposable disposable)
                disposable.Dispose();
        }
        catch
        {
            // Filet anti-boucle : le journal est peut-être déjà fermé.
        }
    }

    private static string? TryRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Le fichier restant sera relu au prochain lancement.
        }
    }
}
