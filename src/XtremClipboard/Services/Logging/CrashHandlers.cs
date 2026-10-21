using Serilog;
using Serilog.Events;

namespace XtremClipboard.Services.Logging;

/// <summary>
/// Intercepteurs de crash. <see cref="OnProcessExit"/> ne fait que vider le journal :
/// effacer le marqueur ici ferait passer un crash pour un arrêt propre.
/// </summary>
public static class CrashHandlers
{
    /// <summary>Fenêtre déjà fermée (HRESULT documenté). L'app continue, sans drapeau crash.</summary>
    public const int ClosedWindowHResult = unchecked((int)0x800710DD);

    public static void Install(ILogger log, SessionTracker session)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) => OnDomainUnhandled(log, session, args);
        TaskScheduler.UnobservedTaskException += (_, args) => OnUnobserved(log, args);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => OnProcessExit();
    }

    public static void OnProcessExit()
    {
        try
        {
            Log.CloseAndFlush();
        }
        catch
        {
            // Filet anti-boucle.
        }
    }

    /// <summary>
    /// Exception UI. Le HRESULT <c>0x800710DD</c> est une Error sans drapeau.
    /// Toute autre exception UI est Fatal + drapeau ; l'appelant laisse <c>Handled</c> à true
    /// (pas de chemin de sortie dédié, le tray ne doit pas mourir).
    /// </summary>
    public static LogEventLevel HandleUiFault(SessionTracker? session, Exception exception, ILogger log)
    {
        try
        {
            if (ContinuesAfter(exception))
            {
                log.Error(
                    exception,
                    "Exception UI interceptée, l'app continue. {EventCode} {Category} {Result}",
                    "CLIP-CRASH-UI",
                    "crash",
                    "continue");
                return LogEventLevel.Error;
            }

            if (session is { IsOpen: true })
                session.MarkCrash();

            log.Fatal(
                exception,
                "Exception non gérée (UI WinUI). {EventCode} {Category} {Result}",
                "CLIP-CRASH-UI",
                "crash",
                "fatal");
            return LogEventLevel.Fatal;
        }
        catch
        {
            return LogEventLevel.Fatal;
        }
    }

    public static bool ContinuesAfter(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.HResult == ClosedWindowHResult)
                return true;
        }

        return false;
    }

    private static void OnDomainUnhandled(ILogger log, SessionTracker session, UnhandledExceptionEventArgs args)
    {
        try
        {
            if (session.IsOpen)
                session.MarkCrash();

            var exception = args.ExceptionObject as Exception
                ?? new Exception(args.ExceptionObject?.ToString() ?? "exception inconnue");
            log.Fatal(
                exception,
                "Exception domaine non gérée (IsTerminating={Terminating}) {EventCode} {Category} {Result}",
                args.IsTerminating,
                "CLIP-CRASH-DOMAIN",
                "crash",
                "fatal");
            Log.CloseAndFlush();
        }
        catch
        {
            // Filet anti-boucle.
        }
    }

    private static void OnUnobserved(ILogger log, UnobservedTaskExceptionEventArgs args)
    {
        try
        {
            log.Error(
                args.Exception,
                "Tâche non observée ({Count} exception(s)) {EventCode} {Category} {Result}",
                args.Exception.InnerExceptions.Count,
                "CLIP-CRASH-TASK",
                "crash",
                "observe");
            args.SetObserved();
        }
        catch
        {
            // Filet anti-boucle.
        }
    }
}
