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
        job.AppendLog(ComputeRuntimeFormatter.Backend(outcome) + "；" + ComputeRuntimeFormatter.Note(outcome));
        // Job.Log is already serialized by the job store. This line restores the attachment after a restart.
        job.AppendLog(Marker + JsonSerializer.Serialize(outcome, ComputeJson.Options).Replace("\n", "").Replace("\r", ""));
    }
    public static ComputeOutcome? GetComputeOutcome(this Job job)
    {
        if (Values.GetOrCreateValue(job).Outcome is { } value) return value;
        var line = job.Log.Split('\n').LastOrDefault(line => line.StartsWith(Marker, StringComparison.Ordinal));
        if (line is null) return null;
        try { return Values.GetOrCreateValue(job).Outcome = JsonSerializer.Deserialize<ComputeOutcome>(line[Marker.Length..], ComputeJson.Options); }
        catch (JsonException) { return null; }
    }
    public static ProgressEstimate? GetComputeEstimate(this Job job) => Values.GetOrCreateValue(job).Estimate;
    public static void SetComputeEstimate(this Job job, ProgressEstimate estimate) => Values.GetOrCreateValue(job).Estimate = estimate;
}
