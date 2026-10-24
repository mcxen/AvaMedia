using System.Runtime.InteropServices;

namespace AvaMedia.Core;

/// <summary>Static machine facts that keep small systems responsive without new settings.</summary>
internal static class MachineProfile
{
    public static int LogicalProcessors { get; } = Environment.ProcessorCount;
    public static long TotalMemoryBytes { get; } = ReadTotalMemory();
    public static bool LowMemory { get; } = TotalMemoryBytes > 0 && TotalMemoryBytes < 4L * 1024 * 1024 * 1024;
    public static bool SmallSystem { get; } = LogicalProcessors <= 4 || LowMemory;
    // One heavy encode at a time on dual-core or under-4 GB systems, two up to eight threads;
    // bigger machines keep the previous start-everything behaviour.
    public static int HeavyJobCapacity { get; } = LogicalProcessors <= 2 || LowMemory ? 1 : LogicalProcessors <= 8 ? 2 : int.MaxValue;

    private static long ReadTotalMemory()
    {
        // Best effort: 0 means unknown and disables the memory-based rules.
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var bytes = 0ul; var length = (IntPtr)sizeof(ulong);
                return sysctlbyname("hw.memsize", ref bytes, ref length, IntPtr.Zero, IntPtr.Zero) == 0 ? (long)bytes : 0;
            }
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                return GlobalMemoryStatusEx(ref status) ? (long)status.TotalPhysical : 0;
            }
            if (OperatingSystem.IsLinux())
                foreach (var line in File.ReadLines("/proc/meminfo"))
                    if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    {
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && long.TryParse(parts[1], out var kilobytes)) return kilobytes * 1024;
                    }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ExternalException or OutOfMemoryException)
        { System.Diagnostics.Trace.TraceInformation("Memory profile: {0}", error.Message); }
        return 0;
    }

    [DllImport("libc")] private static extern int sysctlbyname(string name, ref ulong bytes, ref IntPtr length, IntPtr replacement, IntPtr replacementLength);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtended;
    }
}

/// <summary>Admission class for queue throttling: which jobs saturate a small CPU or RAM budget.</summary>
internal static class Workload
{
    // Documents, archives, downloads, single-image tools and audio-only extraction stay light;
    // everything else re-encodes video or runs AI analysis.
    public static bool EncodingHeavy(Job job)
    {
        var feature = Catalog.Find(job.FeatureId);
        if (feature.Operation is Operation.PdfMerge or Operation.PdfSplit or Operation.PdfText or Operation.PdfDocx or Operation.PdfXlsx
            or Operation.TextPdf or Operation.ImagesPdf or Operation.Zip or Operation.Unzip or Operation.Download or Operation.Info
            or Operation.Player or Operation.IsoCopy or Operation.ImageCompress or Operation.SplitAudio)
            return false;
        if (feature.Operation == Operation.BatchTools) return job.FeatureId == "contact-sheet";
        if (job.FeatureId.StartsWith("image-", StringComparison.Ordinal) && job.FeatureId != "image-gif") return false;
        return true;
    }
}
