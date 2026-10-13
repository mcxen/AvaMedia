using System.Globalization;
using System.Runtime.InteropServices;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

internal static class LinuxInterfaceScaling
{
    internal static readonly int[] Percentages = [0, 100, 125, 150, 200];

    internal static void Configure()
    {
        if (!OperatingSystem.IsLinux()) return;
        var percent = new Storage().LoadSettings().LinuxInterfaceScalePercent;
        if (percent != 0 && Percentages.Contains(percent))
        {
            SetAbsolute(percent / 100d);
            return;
        }
        // Avalonia already handles its own environment variables, Qt and Xft.dpi.
        if (new[] { "AVALONIA_GLOBAL_SCALE_FACTOR", "AVALONIA_SCREEN_SCALE_FACTORS" }
            .Any(name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))) return;
        if (Environment.GetEnvironmentVariable("AVALONIA_SCREEN_SCALE_IGNORE_QT") != "1"
            && new[] { "QT_SCALE_FACTOR", "QT_SCREEN_SCALE_FACTORS" }
                .Any(name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))) return;
        if (!TryScale("GDK_SCALE", out var scale) || HasXftDpi()) return;
        if (TryScale("GDK_DPI_SCALE", out var textScale)) scale *= textScale;
        if (scale is >= 0.5 and <= 8) SetAbsolute(scale);
    }

    private static bool TryScale(string variable, out double scale) =>
        double.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.Float,
            CultureInfo.InvariantCulture, out scale) && double.IsFinite(scale) && scale is >= 0.5 and <= 8;

    private static void SetAbsolute(double scale)
    {
        // An indexed baseline of 1 also yields 1 for every other screen. Avalonia then
        // multiplies only this baseline, rather than Xft.dpi, by the global factor.
        Environment.SetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS", "1");
        Environment.SetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR", scale.ToString(CultureInfo.InvariantCulture));
    }

    private static bool HasXftDpi()
    {
        try
        {
            var display = XOpenDisplay(null);
            if (display == IntPtr.Zero) return true; // Cannot safely infer the desktop's DPI.
            try
            {
                var resources = Marshal.PtrToStringUTF8(XResourceManagerString(display));
                return resources?.Split('\n').Any(line => line.Split(':', 2) is [var key, var value]
                    && key.Trim() == "Xft.dpi" && double.TryParse(value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var dpi) && double.IsFinite(dpi) && dpi > 0) == true;
            }
            finally { XCloseDisplay(display); }
        }
        catch (DllNotFoundException) { return true; }
        catch (EntryPointNotFoundException) { return true; }
    }

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(string? display);
    [DllImport("libX11.so.6")] private static extern IntPtr XResourceManagerString(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
}
