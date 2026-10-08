using System.Diagnostics;

namespace AvaMedia.Core;

public sealed record DownloadSpeedSample(double BytesPerSecond, bool IsDownloading)
{
    public long Timestamp { get; } = Stopwatch.GetTimestamp();
}

public readonly record struct DownloadSpeedTotals(double Current, double Peak, double Average);

// Integrate reported rates over time, including stalls but excluding media postprocessing.
public sealed class DownloadSpeedTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DownloadSpeedSample> _downloads = [];
    private long _lastTimestamp = Stopwatch.GetTimestamp();
    private double _bytes, _seconds, _peak;
    private static readonly long Freshness = Stopwatch.Frequency * 3;

    public void Reset()
    {
        lock (_gate)
        {
            _downloads.Clear(); _bytes = _seconds = _peak = 0;
            _lastTimestamp = Stopwatch.GetTimestamp();
        }
    }

    public void Observe(Job job)
    {
        if (job.FeatureId != "download") return;
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            Integrate(now);
            if (job.State == JobState.Running && job.DownloadSpeed is { IsDownloading: true } sample)
                _downloads[job.Id] = sample;
            else _downloads.Remove(job.Id);
            _peak = Math.Max(_peak, Current(now));
        }
    }

    public DownloadSpeedTotals Sample()
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            Integrate(now);
            var current = Current(now);
            _peak = Math.Max(_peak, current);
            return new(current, _peak, _seconds > 0 ? _bytes / _seconds : 0);
        }
    }

    private void Integrate(long now)
    {
        if (_downloads.Count > 0)
        {
            _seconds += (now - _lastTimestamp) / (double)Stopwatch.Frequency;
            foreach (var sample in _downloads.Values)
            {
                var end = Math.Min(now, sample.Timestamp + Freshness);
                _bytes += ValidSpeed(sample) * Math.Max(0, end - _lastTimestamp) / Stopwatch.Frequency;
            }
        }
        _lastTimestamp = now;
    }

    private double Current(long now) => _downloads.Values
        .Where(sample => now - sample.Timestamp <= Freshness).Sum(ValidSpeed);
    private static double ValidSpeed(DownloadSpeedSample sample) =>
        double.IsFinite(sample.BytesPerSecond) ? Math.Max(0, sample.BytesPerSecond) : 0;
}
