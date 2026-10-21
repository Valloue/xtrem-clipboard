using System.Reflection;
using Serilog;
using XtremClipboard.Services;
using Serilog.Core;
using Serilog.Formatting.Display;
using Serilog.Templates;

namespace XtremClipboard.Services.Logging;

/// <summary>
/// Deux sorties sous le dossier reçu (défaut : <c>AppDataPaths.Root\logs</c>).
/// Le texte garde le nom <c>xtrem-clipboard-.log</c>. Le CLEF est à côté.
/// Niveau plancher Information. Le chemin XtremSuite n'est pas celui du modèle Commun.
/// </summary>
public static class LogBootstrap
{
    public const string FormatVersion = "1";

    private const string TextTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext:l}: {Message:lj} {Properties:j}{NewLine}{Exception}";

    private const string ClefTemplate =
        "{ {" +
        "t: @t, lvl: @l, msg: @m, " +
        "ex: @x, " +
        "exType: ExceptionDetail.Type, " +
        "hResult: ExceptionDetail.HResult, " +
        "at: ExceptionDetail.Location, " +
        "src: SourceContext, evt: EventCode, cat: Category, " +
        "result: Result, dur: DurationMs, " +
        "app: App, ver: Version, commit: Commit, chan: Channel, " +
        "sid: SessionId, iid: InstallId, pid: Pid, tid: ThreadId, " +
        "osb: OsBuild, fmt: LogFormat, " +
        "..rest()" +
        "} }\n";

    public static LoggingHost Configure(string? logDirectory = null)
    {
        logDirectory ??= Path.Combine(AppDataPaths.Root, "logs");
        logDirectory = Path.GetFullPath(logDirectory);
        Directory.CreateDirectory(logDirectory);

        var parent = Directory.GetParent(logDirectory)?.FullName;
        var installId = parent is null ? "unknown" : InstallId.GetOrCreate(parent);
        var (version, commit, channel) = ReadVersion();
        var sessionId = Guid.NewGuid().ToString("N");

        var text = new ScrubbingTextFormatter(new MessageTemplateTextFormatter(TextTemplate));
        var clef = new ScrubbingTextFormatter(new ExpressionTemplate(ClefTemplate));

        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .Enrich.With(new ThreadIdEnricher())
            .Enrich.With(new ExceptionDetailEnricher())
            .Enrich.With(new FaultContextEnricher(parent ?? logDirectory))
            .Enrich.WithProperty("App", "XtremClipboard")
            .Enrich.WithProperty("Version", version)
            .Enrich.WithProperty("Commit", commit)
            .Enrich.WithProperty("Channel", channel)
            .Enrich.WithProperty("SessionId", sessionId)
            .Enrich.WithProperty("InstallId", installId)
            .Enrich.WithProperty("Pid", Environment.ProcessId)
            .Enrich.WithProperty("OsBuild", Environment.OSVersion.Version.ToString())
            .Enrich.WithProperty("LogFormat", FormatVersion)
            .WriteTo.File(
                text,
                Path.Combine(logDirectory, "xtrem-clipboard-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .WriteTo.File(
                clef,
                Path.Combine(logDirectory, "xtrem-clipboard-.clef"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();

        var session = new SessionTracker(logger, logDirectory, version, commit, sessionId);
        return new LoggingHost(logger, session);
    }

    private static (string Version, string Commit, string Channel) ReadVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(LogBootstrap).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
        var plus = info.IndexOf('+', StringComparison.Ordinal);
        var version = plus < 0 ? info : info[..plus];
        var commit = plus < 0 ? "" : info[(plus + 1)..];
        var channel = version.Contains('-', StringComparison.Ordinal) ? "dev" : "stable";
        return (version, commit, channel);
    }
}

/// <summary>Logger et session créés ensemble. L'app assigne <c>Log.Logger</c>.</summary>
public sealed class LoggingHost
{
    public LoggingHost(Logger logger, SessionTracker session)
    {
        Logger = logger;
        Session = session;
    }

    public Logger Logger { get; }

    public SessionTracker Session { get; }
}
