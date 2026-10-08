using System.Runtime.InteropServices;

namespace AvaMedia.Core;

public sealed partial class SystemResourceMonitor
{
    private const string MacSystem = "/usr/lib/libSystem.B.dylib";
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private uint _macHost, _macTask;
    private MacCpuTicks? _previousMacCpu;

    private double? ReadMacCpu()
    {
        if (_macHost == 0) { _macTask = task_self_trap(); _macHost = mach_host_self(); }
        uint count = 4;
        if (host_statistics(_macHost, 3, out var ticks, ref count) != 0 || count != 4) return null;
        var previous = _previousMacCpu; _previousMacCpu = ticks;
        if (previous is not { } before) return null;
        var idle = (ulong)unchecked(ticks.Idle - before.Idle);
        var total = idle + unchecked(ticks.User - before.User) + unchecked(ticks.System - before.System) + unchecked(ticks.Nice - before.Nice);
        return total > 0 ? 100 * (1 - idle / (double)total) : null;
    }

    private static double? ReadMacGpu()
    {
        var matching = IOServiceMatching("IOAccelerator");
        if (matching == 0) return null;
        // IOServiceGetMatchingServices consumes the matching dictionary.
        if (IOServiceGetMatchingServices(0, matching, out var iterator) != 0) return null;
        var key = CFStringCreateWithCString(0, "PerformanceStatistics", 0x08000100);
        try
        {
            if (key == 0) return null;
            double? maximum = null;
            uint service;
            while ((service = IOIteratorNext(iterator)) != 0)
            {
                try
                {
                    var stats = IORegistryEntryCreateCFProperty(service, key, 0, 0);
                    if (stats == 0) continue;
                    try
                    {
                        if (CFGetTypeID(stats) != CFDictionaryGetTypeID()) continue;
                        var utilization = ReadMacNumber(stats, "Device Utilization %") ?? ReadMacNumber(stats, "GPU Activity(%)");
                        if (utilization is { } value && double.IsFinite(value))
                            maximum = Math.Max(maximum ?? 0, Math.Clamp(value, 0, 100));
                    }
                    finally { CFRelease(stats); }
                }
                finally { IOObjectRelease(service); }
            }
            return maximum;
        }
        finally { if (key != 0) CFRelease(key); IOObjectRelease(iterator); }
    }

    private static double? ReadMacNumber(nint dictionary, string name)
    {
        var key = CFStringCreateWithCString(0, name, 0x08000100);
        if (key == 0) return null;
        try
        {
            var number = CFDictionaryGetValue(dictionary, key);
            return number != 0 && CFGetTypeID(number) == CFNumberGetTypeID() && CFNumberGetValue(number, 13, out var value)
                ? value : null;
        }
        finally { CFRelease(key); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MacCpuTicks { public uint User, System, Idle, Nice; }

    [DllImport(MacSystem)] private static extern uint task_self_trap();
    [DllImport(MacSystem)] private static extern uint mach_host_self();
    [DllImport(MacSystem)] private static extern int host_statistics(uint host, int flavor, out MacCpuTicks ticks, ref uint count);
    [DllImport(MacSystem)] private static extern int mach_port_deallocate(uint task, uint name);
    [DllImport(IOKit)] private static extern nint IOServiceMatching([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(IOKit)] private static extern int IOServiceGetMatchingServices(uint port, nint matching, out uint iterator);
    [DllImport(IOKit)] private static extern uint IOIteratorNext(uint iterator);
    [DllImport(IOKit)] private static extern int IOObjectRelease(uint value);
    [DllImport(IOKit)] private static extern nint IORegistryEntryCreateCFProperty(uint service, nint key, nint allocator, uint options);
    [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(CoreFoundation)] private static extern nint CFDictionaryGetValue(nint dictionary, nint key);
    [DllImport(CoreFoundation)] private static extern nuint CFGetTypeID(nint value);
    [DllImport(CoreFoundation)] private static extern nuint CFDictionaryGetTypeID();
    [DllImport(CoreFoundation)] private static extern nuint CFNumberGetTypeID();
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(nint number, int type, out double value);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);
}
