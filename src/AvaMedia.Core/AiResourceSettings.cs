namespace AvaMedia.Core;

/// <summary>Public snapshot of the same cooperative policy used by inference and media workers.</summary>
public sealed record AiResourceSettings(int CpuReservePercent, int CpuThreads, bool MultiThread,
    int ProcessorCount, int EffectiveThreads)
{
    public string ReservationMode => "cooperative";
    public string InferencePolicy => "GPU first, CPU fallback";
    public static AiResourceSettings From(AppSettings settings) => new(settings.CpuReservePercent, settings.CpuThreads,
        settings.MultiThread, Environment.ProcessorCount, CpuResourcePolicy.Threads(16, settings));
}
