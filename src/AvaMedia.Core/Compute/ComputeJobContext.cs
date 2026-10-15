namespace AvaMedia.Core.Compute;

/// <summary>Flows through async model work without changing service or multi-stage contracts.</summary>
public static class ComputeJobContext
{
    private static readonly AsyncLocal<Action<ComputeOutcome>?> Sink = new();
    public static IDisposable Enter(Job job)
    {
        ComputeOutcome? last = null;
        return Enter(outcome =>
        {
            if (ReferenceEquals(last, outcome)) return;
            last = outcome; job.SetComputeOutcome(outcome);
        });
    }
    public static IDisposable Enter(Action<ComputeOutcome> publish)
    {
        var previous = Sink.Value; Sink.Value = publish; return new Scope(previous);
    }
    public static void Report(ComputeOutcome outcome) => Sink.Value?.Invoke(outcome);
    private sealed class Scope(Action<ComputeOutcome>? previous) : IDisposable
    { public void Dispose() => Sink.Value = previous; }
}
