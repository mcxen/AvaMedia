using System.ComponentModel;
using System.Diagnostics;

namespace AvaMedia.Core.Compute;

/// <summary>Owns the existing Job.Estimate during one compute attempt, including QueueService's timer updates.</summary>
public sealed class ComputeProgressScope : IDisposable
{
    private readonly Job _job;
    private readonly PriorAwareProgressEstimator _estimator;
    private readonly Func<TimeSpan> _elapsed;
    private readonly object _gate = new();
    private bool _publishing, _disposed;
    private double _progress;
    public ComputeProgressScope(Job job, ThroughputModel model, ThroughputKey key, double totalWork, Func<TimeSpan>? elapsed = null)
    {
        _job = job; _estimator = new(model, key, totalWork);
        var watch = Stopwatch.StartNew(); _elapsed = elapsed ?? (() => watch.Elapsed);
        _job.PropertyChanged += Changed;
        Update(0);
    }
    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(Job.Estimate) || _job.State != JobState.Running) return;
        lock (_gate) { if (!_publishing && !_disposed) Publish(); }
    }
    public ProgressEstimate Update(double progress)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ComputeProgressScope));
            _progress = progress; return Publish();
        }
    }
    public void SwitchBackend(ThroughputKey key)
    { lock (_gate) { _estimator.SwitchBackend(key); Publish(); } }
    private ProgressEstimate Publish()
    {
        var estimate = _estimator.Update(_progress, _elapsed());
        _publishing = true;
        try { _job.SetComputeEstimate(estimate); _job.Estimate = estimate; }
        finally { _publishing = false; }
        return estimate;
    }
    public void Dispose()
    { lock (_gate) { _disposed = true; _job.PropertyChanged -= Changed; } }
}
