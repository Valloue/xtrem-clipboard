using System.Management;
using Serilog;

namespace XtremClipboard.Services.Logging;

/// <summary>
/// Nom et version de pilote via WMI. Un échec = un seul avertissement, le démarrage continue.
/// </summary>
public static class GpuProbe
{
    private static int _warned;

    public static string? TryDescribe(ILogger log, Func<string?>? query = null)
    {
        try
        {
            var text = query is null ? QueryWmi() : query();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                log.Warning(
                    ex,
                    "GPU indisponible {EventCode} {Category} {Result}",
                    "CLIP-SYS-GPU",
                    "system",
                    "indisponible");
            }

            return null;
        }
    }

    internal static void ResetWarning() => Interlocked.Exchange(ref _warned, 0);

    private static string? QueryWmi()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, DriverVersion FROM Win32_VideoController");
        using var results = searcher.Get();
        var parts = new List<string>();
        foreach (ManagementObject obj in results)
        {
            using (obj)
            {
                var name = obj["Name"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var driver = obj["DriverVersion"]?.ToString()?.Trim();
                parts.Add(string.IsNullOrWhiteSpace(driver) ? name : name + " " + driver);
            }
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}
