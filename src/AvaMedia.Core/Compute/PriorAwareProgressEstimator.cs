namespace AvaMedia.Core.Compute;

/// <summary>Uses a conservative backend prior immediately, then blends a recent measured rate over 15 s.</summary>
public sealed class PriorAwareProgressEstimator
{
    private readonly ThroughputModel _model;
    private readonly double _total;
    private ThroughputKey _key;
    private double _startTime, _startProgress, _lastTime, _lastProgress, _lastAdvance, _recordTime, _recordProgress;
    private bool _started;
    private ProgressEstimate _estimate = new(ProgressEstimateState.Estimating);
    private readonly Queue<(double Time, double Progress)> _window = new();
    public PriorAwareProgressEstimator(ThroughputModel model, ThroughputKey key, double totalWork)
    { _model = model; _key = key; _total = totalWork; }
    public void SwitchBackend(ThroughputKey key) { _key = key; _started = false; _window.Clear(); }
    public ProgressEstimate Update(double progress, TimeSpan elapsed)
    {
        var now = elapsed.TotalSeconds;
        if (!double.IsFinite(progress) || now < 0 || !double.IsFinite(_total) || _total <= 0) return _estimate;
        progress = Math.Clamp(progress, 0, 100);
        if (!_started || now < _lastTime || progress < _lastProgress)
        {
            _started = true; _window.Clear(); _startTime = _lastAdvance = _recordTime = now;
            _startProgress = _recordProgress = progress;
        }
        if (progress > _lastProgress) _lastAdvance = now;
        _lastTime = now; _lastProgress = progress;
        _window.Enqueue((now, progress));
        while (_window.Count > 2 && (now - _window.Peek().Time > 15 || _window.Count > 128)) _window.Dequeue();
        if (now - _recordTime >= 5 && progress > _recordProgress)
        {
            _model.Observe(_key, (progress - _recordProgress) / 100 * _total, TimeSpan.FromSeconds(now - _recordTime));
            _recordTime = now; _recordProgress = progress;
        }
        if (progress >= 99.9) return _estimate = new(ProgressEstimateState.Finalizing);
        if (progress > _startProgress && now - _lastAdvance >= 5) return _estimate = new(ProgressEstimateState.Stalled);
        var prior = _model.Rate(_key); var first = _window.Peek(); var span = now - first.Time;
        var measured = span > 0 ? (progress - first.Progress) / 100 * _total / span : 0;
        var weight = measured > 0 ? Math.Clamp((now - _startTime) / 15, 0, 1) : 0;
        var rate = prior * (1 - weight) + measured * weight;
        var seconds = (1 - progress / 100) * _total / rate;
        return _estimate = double.IsFinite(seconds) && seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds - 1
            ? new(ProgressEstimateState.Available, TimeSpan.FromSeconds(seconds)) : new(ProgressEstimateState.Estimating);
    }
}
