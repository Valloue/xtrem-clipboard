using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Serilog;

namespace XtremClipboard.Services.Logging;

/// <summary>
/// Nombre, taille et échelle des écrans. Les bounds viennent de <see cref="DisplayArea"/>.
/// L'échelle est le DPI effectif du moniteur (100 % = 96), DisplayArea ne l'expose pas.
/// </summary>
public static class DisplaySummary
{
    private static int _warned;

    public static string Describe(ILogger log)
    {
        try
        {
            var areas = DisplayArea.FindAll();
            if (areas is null || areas.Count == 0)
                return "aucun";

            var parts = new string[areas.Count];
            for (var i = 0; i < areas.Count; i++)
            {
                var area = areas[i];
                var bounds = area.OuterBounds;
                var scale = TryScalePercent(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));
                var size = scale is null
                    ? $"{bounds.Width}x{bounds.Height}"
                    : $"{bounds.Width}x{bounds.Height}@{scale}%";
                parts[i] = area.IsPrimary ? size + " principal" : size;
            }

            return areas.Count + " " + string.Join(", ", parts);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                log.Warning(
                    ex,
                    "Écrans indisponibles {EventCode} {Category} {Result}",
                    "CLIP-SYS-DISP",
                    "system",
                    "degrade");
            }

            return "indisponible";
        }
    }

    private static int? TryScalePercent(int x, int y)
    {
        try
        {
            var monitor = MonitorFromPoint(new POINT { X = x, Y = y }, 2);
            if (monitor == IntPtr.Zero)
                return null;

            if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) != 0 || dpiX == 0)
                return null;

            return (int)Math.Round(dpiX * 100.0 / 96.0);
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
