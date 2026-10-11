namespace AvaMedia.Core;

/// <summary>Public snapshot of the same cooperative policy used by inference and media workers.</summary>
public sealed record AiResourceSettings(int CpuReservePercent, int CpuThreads, int ParallelJobs, bool MultiThread,
    int ProcessorCount, int EffectiveThreads, int EffectiveParallelJobs)
{
    public string ReservationMode => "cooperative";
    public string InferencePolicy => "GPU first, CPU fallback";
    public static AiResourceSettings From(AppSettings settings) => new(settings.CpuReservePercent, settings.CpuThreads,
        settings.ParallelJobs, settings.MultiThread, Environment.ProcessorCount, CpuResourcePolicy.Threads(16, settings),
        settings.MultiThread ? settings.CpuReservePercent > 0 ? CpuResourcePolicy.ParallelLimit(settings.ParallelJobs) : Math.Clamp(settings.ParallelJobs, 1, 8) : 1);
}
