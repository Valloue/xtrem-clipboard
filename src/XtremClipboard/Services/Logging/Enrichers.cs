using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace XtremClipboard.Services.Logging;

/// <summary>Ajoute le thread managé sur chaque ligne.</summary>
public sealed class ThreadIdEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) =>
        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty("ThreadId", Environment.CurrentManagedThreadId));
}

/// <summary>
/// Sort le type, le HRESULT, le fichier:ligne et l'exception interne en champs.
/// Le message de l'exception reste dans le rendu, passé ensuite au scrub.
/// </summary>
public sealed class ExceptionDetailEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.Exception is null)
            return;

        var exception = logEvent.Exception;
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(
            "ExceptionDetail",
            new
            {
                Type = exception.GetType().FullName,
                exception.HResult,
                Location = TryLocation(exception),
                Inner = exception.InnerException?.GetType().FullName
            },
            destructureObjects: true));
    }

    private static string TryLocation(Exception exception)
    {
        try
        {
            var frame = new StackTrace(exception, fNeedFileInfo: true).GetFrame(0);
            if (frame?.GetFileName() is { } file)
                return $"{Path.GetFileName(file)}:{frame.GetFileLineNumber()}";

            return exception.TargetSite?.Name ?? "";
        }
        catch
        {
            return exception.TargetSite?.Name ?? "";
        }
    }
}

/// <summary>
/// Sur les lignes Error et Fatal : fil d'Ariane et contexte système utile.
/// Jamais de chemin ni de nom de machine dans ces propriétés.
/// </summary>
public sealed class FaultContextEnricher : ILogEventEnricher
{
    private readonly string _dataRoot;

    public FaultContextEnricher(string dataRoot)
    {
        _dataRoot = dataRoot;
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.Level < LogEventLevel.Error)
            return;

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(
            "Breadcrumbs",
            string.Join(" | ", Breadcrumbs.Snapshot())));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(
            "OsVersion",
            Environment.OSVersion.VersionString));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(
            "DotNet",
            Environment.Version.ToString()));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(
            "CpuCores",
            Environment.ProcessorCount));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(
            "RamMo",
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(
            "DiskFreeMo",
            SystemContext.DiskFreeMo(_dataRoot)));
    }
}
