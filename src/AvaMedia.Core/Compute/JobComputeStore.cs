using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AvaMedia.Core.Compute;

public static class JobComputeStore
{
    private sealed class State { public ComputeOutcome? Outcome; public ProgressEstimate? Estimate; }
    private static readonly ConditionalWeakTable<Job, State> Values = new();
    private const string Marker = "[compute-outcome] ";
    public static void SetComputeOutcome(this Job job, ComputeOutcome outcome)
    {
        Values.GetOrCreateValue(job).Outcome = outcome;
        // Keep the last human-readable line as LogSummary, rather than exposing the machine marker there.
        job.AppendLog(SerializeMarker(outcome));
        job.AppendLog(ComputeRuntimeFormatter.Backend(outcome) + "；" + ComputeRuntimeFormatter.Note(outcome));
    }
    public static string SerializeMarker(ComputeOutcome outcome) => Marker + JsonSerializer.Serialize(outcome, ComputeJson.Options).Replace("\n", "").Replace("\r", "");
    public static ComputeOutcome? ParseLog(string log)
    {
        var line = log.Split('\n').LastOrDefault(line => line.StartsWith(Marker, StringComparison.Ordinal));
        if (line is null) return null;
        try { return JsonSerializer.Deserialize<ComputeOutcome>(line[Marker.Length..], ComputeJson.Options); }
        catch (JsonException) { return null; }
    }
    public static ComputeOutcome? GetComputeOutcome(this Job job)
    {
        var state = Values.GetOrCreateValue(job);
        return state.Outcome ??= ParseLog(job.Log);
    }
    public static ProgressEstimate? GetComputeEstimate(this Job job) => Values.GetOrCreateValue(job).Estimate;
    public static void SetComputeEstimate(this Job job, ProgressEstimate estimate) => Values.GetOrCreateValue(job).Estimate = estimate;
}
