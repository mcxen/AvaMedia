using System.Globalization;
using System.Runtime.InteropServices;

namespace AvaMedia.Core;

public readonly record struct SystemResourceUsage(double? Cpu, double? Gpu);

public sealed partial class SystemResourceMonitor : IDisposable
{
    private readonly object _gate = new();
    private (ulong Idle, ulong Total)? _previousCpu;
    private bool _disposed;

    public SystemResourceUsage Sample(bool reset = false)
    {
        lock (_gate)
        {
            if (_disposed) return default;
            if (reset) { _previousCpu = null; _previousMacCpu = null; _windowsGpuPrimed = false; }
            return new(TryRead(ReadCpu), TryRead(ReadGpu));
        }
    }

    private static double? TryRead(Func<double?> read)
    {
        try
        {
            var value = read();
            return value is { } number && double.IsFinite(number) ? Math.Clamp(number, 0, 100) : null;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException
            or IOException or UnauthorizedAccessException or FormatException or OverflowException)
        { return null; }
    }

    private double? ReadCpu()
    {
        if (OperatingSystem.IsMacOS()) return ReadMacCpu();
        ulong idle, total;
        if (OperatingSystem.IsWindows())
        {
            if (!GetSystemTimes(out idle, out var kernel, out var user)) return null;
            total = kernel + user; // Kernel time includes idle time.
        }
        else if (OperatingSystem.IsLinux())
        {
            var values = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (values.Length < 5 || values[0] != "cpu") return null;
            var ticks = values.Skip(1).Take(8).Select(value => ulong.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            idle = ticks[3] + (ticks.Length > 4 ? ticks[4] : 0); total = 0;
            foreach (var tick in ticks) total += tick;
        }
        else return null;
        var previous = _previousCpu; _previousCpu = (idle, total);
        if (previous is not { } before || total <= before.Total || idle < before.Idle) return null;
        return 100 * (1 - (idle - before.Idle) / (double)(total - before.Total));
    }

    private double? ReadGpu() => OperatingSystem.IsMacOS() ? ReadMacGpu()
        : OperatingSystem.IsWindows() ? ReadWindowsGpu() : null;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_windowsGpuQuery != 0) { PdhCloseQuery(_windowsGpuQuery); _windowsGpuQuery = 0; }
            if (_macHost != 0) { mach_port_deallocate(_macTask, _macHost); _macHost = 0; }
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
