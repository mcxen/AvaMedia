using System.Globalization;

namespace AvaMedia.Core;

public enum ProgressEstimateState { Estimating, Available, Stalled, Finalizing }

public sealed record ProgressEstimate(ProgressEstimateState State, TimeSpan? Remaining = null)
{
    public string Text => State switch
    {
        ProgressEstimateState.Available when Remaining is {} time => "预计剩余 " + Format(time),
        ProgressEstimateState.Stalled => "进度暂未更新",
        ProgressEstimateState.Finalizing => "正在收尾",
        _ => "估算中"
    };

    private static string Format(TimeSpan time)
    {
        var seconds = (long)Math.Max(1, Math.Ceiling(time.TotalSeconds));
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}");
    }
}

/// <summary>Estimates from a recent window of actual progress, using monotonic elapsed time.</summary>
public sealed class ProgressEstimator
{
    private const double WindowSeconds = 15, WarmupSeconds = 2, StallSeconds = 5;
    private readonly List<(double Time, double Progress)> _samples = [];
    private double _lastTime, _lastProgress, _lastAdvance;
    private ProgressEstimate _estimate = new(ProgressEstimateState.Estimating);

    public ProgressEstimate Update(double progress, TimeSpan elapsed)
    {
        if (!double.IsFinite(progress) || elapsed < TimeSpan.Zero) return _estimate;
        progress = Math.Clamp(progress, 0, 100);
        var now = elapsed.TotalSeconds;
        if (_samples.Count == 0 || now < _lastTime || progress < _lastProgress - .000001)
        {
            _samples.Clear(); _samples.Add((now, progress)); _lastAdvance = now;
        }
        else if (progress > _lastProgress)
        {
            _samples.Add((now, progress)); _lastAdvance = now;
        }
        _lastProgress = progress; _lastTime = now;
        if (progress >= 99.9) return _estimate = new(ProgressEstimateState.Finalizing);
        if (progress > 0 && now - _lastAdvance >= StallSeconds)
            return _estimate = new(ProgressEstimateState.Stalled);

        var cutoff = now - WindowSeconds;
        while (_samples.Count > 2 && _samples[1].Time <= cutoff) _samples.RemoveAt(0);
        // Bound memory for backends that report more often than FFmpeg's normal progress interval.
        while (_samples.Count > 256) _samples.RemoveAt(1);
        var first = _samples[0];
        if (_samples.Count > 1 && first.Time < cutoff && _samples[1].Time > first.Time)
        {
            var next = _samples[1];
            first = (cutoff, first.Progress + (next.Progress - first.Progress) * (cutoff - first.Time) / (next.Time - first.Time));
        }
        var span = now - first.Time; var advanced = progress - first.Progress;
        if (span < WarmupSeconds || advanced <= .000001)
            return _estimate = new(ProgressEstimateState.Estimating);
        var seconds = (100 - progress) * span / advanced;
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds >= TimeSpan.MaxValue.TotalSeconds - 1)
            return _estimate = new(ProgressEstimateState.Estimating);
        return _estimate = new(ProgressEstimateState.Available, TimeSpan.FromSeconds(seconds));
    }
}
