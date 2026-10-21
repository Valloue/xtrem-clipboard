using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Serilog;

namespace XtremClipboard.Services.Logging;

/// <summary>
/// Une ligne au démarrage. Pas de chemin, pas de nom de machine.
/// Les écrans et le GPU sont fournis par l'appelant (WinUI / WMI).
/// </summary>
public static class SystemContext
{
    public static void LogAtStartup(ILogger log, string dataRoot, string displays, string? gpu)
    {
        log.Information(
            "Système : {Os}, process {ProcArch}, .NET {DotNet}, CPU {CpuCores} cœurs, RAM {RamMo} Mo, disque libre {DiskFreeMo} Mo, admin={IsAdmin}, locale={Locale}, écrans={Displays}, gpu={Gpu} {EventCode} {Category} {Result}",
            Environment.OSVersion.VersionString,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.Version.ToString(),
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024),
            DiskFreeMo(dataRoot),
            IsAdmin(),
            CultureInfo.CurrentUICulture.Name,
            displays,
            string.IsNullOrWhiteSpace(gpu) ? "aucun" : gpu,
            "CLIP-SYS-001",
            "system",
            "ok");
    }

    public static long DiskFreeMo(string dataRoot)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dataRoot));
            if (string.IsNullOrEmpty(root))
                return -1;

            return new DriveInfo(root).AvailableFreeSpace / (1024 * 1024);
        }
        catch
        {
            return -1;
        }
    }

    private static bool IsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
