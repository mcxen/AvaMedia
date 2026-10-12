using System.Diagnostics;
using System.Globalization;

namespace AvaMedia.Core;

/// <summary>Shared CPU headroom hints and cooperative backoff, not an OS utilization quota.</summary>
internal static class CpuResourcePolicy
{
    private static AppSettings _settings = new();
    private static readonly object SampleGate = new();
    private static readonly SystemResourceMonitor Monitor = new();
    private static long _sampledAt;
    private static double? _cpu;
    internal static void Configure(AppSettings settings) => Volatile.Write(ref _settings, settings);
    internal static int ReservePercent => Math.Clamp(Volatile.Read(ref _settings).CpuReservePercent, 0, 80);
    internal static bool Enabled => ReservePercent > 0;
    internal static int PolicyKey
    {
        get
        {
            var settings = Volatile.Read(ref _settings);
            return HashCode.Combine(settings.CpuReservePercent, settings.EffectiveCpuThreads, settings.MultiThread);
        }
    }
    internal static int Threads(int requested, AppSettings? settings = null)
    {
        settings ??= Volatile.Read(ref _settings);
        if (!settings.MultiThread) return 1;
        var maximum = Math.Min(Math.Clamp(requested, 1, 16), Math.Clamp(settings.EffectiveCpuThreads, 1, 16));
        if (settings.CpuReservePercent == 0) return maximum;
        var workers = Math.Max(1, (int)Math.Floor(Environment.ProcessorCount * (100 - Math.Clamp(settings.CpuReservePercent, 0, 80)) / 100d));
        return Math.Min(maximum, workers);
    }

    private static int DelayMilliseconds()
    {
        if (!Enabled) return 0;
        lock (SampleGate)
        {
            if (_sampledAt == 0 || Stopwatch.GetElapsedTime(_sampledAt).TotalMilliseconds >= 750)
            { _cpu = Monitor.SampleCpu(); _sampledAt = Stopwatch.GetTimestamp(); }
            var target = 100 - ReservePercent;
            return _cpu is { } cpu && cpu > target ? (int)Math.Clamp((cpu - target) * 8, 25, 250) : 0;
        }
    }
    internal static Task YieldAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var delay = DelayMilliseconds();
        return delay == 0 ? Task.CompletedTask : Task.Delay(delay, ct);
    }
    internal static void Yield(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var delay = DelayMilliseconds();
        if (delay > 0 && ct.WaitHandle.WaitOne(delay)) ct.ThrowIfCancellationRequested();
    }

    internal static void LowerWorkerPriority(Process process)
    {
        if (!Enabled) return;
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        { Trace.TraceInformation("Worker process priority: {0}", error.Message); }
    }

    internal static IEnumerable<string> ToolArguments(string executable, IEnumerable<string> arguments)
    {
        var source = arguments.ToArray();
        if (!Enabled || !Path.GetFileNameWithoutExtension(executable).Equals("ffmpeg", StringComparison.OrdinalIgnoreCase)
            || !source.Contains("-i")) return source;
        var requestedThreads = 16;
        for (var index = 0; index + 1 < source.Length; index++)
            if ((source[index] == "-threads" || source[index].StartsWith("-threads:", StringComparison.Ordinal))
                && int.TryParse(source[index + 1], out var requested) && requested > 0)
                requestedThreads = Math.Min(requestedThreads, requested);
        var threads = Threads(requestedThreads).ToString(CultureInfo.InvariantCulture);
        var decoderThreads = Math.Min(Threads(requestedThreads), 2).ToString(CultureInfo.InvariantCulture);
        var result = new List<string>();
        if (!source.Contains("-filter_threads")) result.AddRange(["-filter_threads", "1"]);
        if (!source.Contains("-filter_complex_threads")) result.AddRange(["-filter_complex_threads", "1"]);
        for (var index = 0; index < source.Length; index++)
        {
            var option = source[index];
            // Decoder thread options are scoped to each input; encoder options precede the output.
            if (option == "-i") result.AddRange(["-threads", decoderThreads]);
            if (index == source.Length - 1 && index > 0 && source[index - 1] != "-i"
                && (!option.StartsWith('-') || option == "-")) result.AddRange(["-threads", threads]);
            result.Add(option);
            if (index + 1 < source.Length && (option == "-filter_threads" || option == "-filter_complex_threads"))
            { result.Add("1"); index++; }
            else if (index + 1 < source.Length && (option == "-threads" || option.StartsWith("-threads:", StringComparison.Ordinal))
                && int.TryParse(source[index + 1], out var requested))
            { result.Add((requested <= 0 ? Threads(16) : Threads(requested)).ToString(CultureInfo.InvariantCulture)); index++; }
        }
        return result;
    }
}
