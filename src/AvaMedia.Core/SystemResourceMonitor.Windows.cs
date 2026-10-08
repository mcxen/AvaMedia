using System.Runtime.InteropServices;

namespace AvaMedia.Core;

public sealed partial class SystemResourceMonitor
{
    private nint _windowsGpuQuery, _windowsGpuCounter;
    private bool _windowsGpuInitialized, _windowsGpuPrimed;
    private const uint PdhMoreData = 0x800007d2;
    private const uint PdhDouble = 0x200;

    private double? ReadWindowsGpu()
    {
        if (!_windowsGpuInitialized)
        {
            _windowsGpuInitialized = true;
            if (PdhOpenQueryW(null, 0, out _windowsGpuQuery) != 0) return null;
            if (PdhAddEnglishCounterW(_windowsGpuQuery, @"\GPU Engine(*)\Utilization Percentage", 0, out _windowsGpuCounter) != 0)
            {
                PdhCloseQuery(_windowsGpuQuery); _windowsGpuQuery = 0; return null;
            }
        }
        if (_windowsGpuQuery == 0 || PdhCollectQueryData(_windowsGpuQuery) != 0) return null;
        if (!_windowsGpuPrimed) { _windowsGpuPrimed = true; return null; }
        uint size = 0;
        if (PdhGetFormattedCounterArrayW(_windowsGpuCounter, PdhDouble, ref size, out _, 0) != PdhMoreData
            || size == 0 || size > 16 * 1024 * 1024) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(_windowsGpuCounter, PdhDouble, ref size, out var count, buffer) != 0) return null;
            var stride = Marshal.SizeOf<PdhCounterItem>();
            if ((ulong)count * (uint)stride > size) return null;
            var engines = new Dictionary<string, double>(StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhCounterItem>(buffer + i * stride);
                if (item.Value.Status > 1 || !double.IsFinite(item.Value.Value)) continue;
                var name = Marshal.PtrToStringUni(item.Name);
                if (name is null) continue;
                var start = name.IndexOf("luid_", StringComparison.Ordinal);
                var end = name.IndexOf("_engtype_", StringComparison.Ordinal);
                if (start < 0 || end <= start) continue;
                var engine = name[start..end];
                engines[engine] = engines.GetValueOrDefault(engine) + Math.Max(0, item.Value.Value);
            }
            // Sum processes on each physical engine, then take the busiest engine.
            return engines.Count > 0 ? Math.Clamp(engines.Values.Max(), 0, 100) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhCounterValue { public uint Status; public double Value; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PdhCounterItem { public nint Name; public PdhCounterValue Value; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? source, nuint userData, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(nint query, string path, nuint userData, out nint counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint size, out uint count, nint buffer);
}
