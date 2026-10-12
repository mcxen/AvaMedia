namespace AvaMedia.Core;

/// <summary>Source-clock chunk accounting; subtitle cues retain their separate output clock.</summary>
public sealed class AiProgressSpeechTracker
{
    public const double ChunkSeconds = 300;
    private readonly string _jobId;
    private readonly double _duration;
    private readonly SpeechSegmentProgress[] _segments;
    private readonly List<VoiceInterval> _voices = [];
    private readonly object _gate = new();
    private int _current = -1;
    private int _cues;
    private string _stage = "";

    public AiProgressSpeechTracker(string jobId, double sourceStart, double duration)
    {
        if (!double.IsFinite(sourceStart) || sourceStart < 0) throw new ArgumentOutOfRangeException(nameof(sourceStart));
        if (!double.IsFinite(duration) || duration <= 0) throw new ArgumentOutOfRangeException(nameof(duration));
        _jobId = jobId; _duration = duration;
        _segments = Enumerable.Range(0, checked((int)Math.Ceiling(duration / ChunkSeconds)))
            .Select(index => new SpeechSegmentProgress(index, sourceStart + index * ChunkSeconds,
                sourceStart + Math.Min(duration, (index + 1) * ChunkSeconds), "pending", 0, 0, 0)).ToArray();
        Publish();
    }

    public void Begin(int index)
    {
        lock (_gate)
        {
            _current = index; _stage = "提取音轨";
            _segments[index] = _segments[index] with { State = "running" };
            Publish();
        }
    }

    public void Stage(string stage) { lock (_gate) { _stage = stage; Publish(); } }

    /// <summary>Clip overlapping extraction windows to the nominal chunk; never count overlap twice.</summary>
    public void Detected(IEnumerable<VoiceInterval> intervals)
    {
        lock (_gate)
        {
            var segment = _segments[_current];
            var owned = intervals.Where(value => double.IsFinite(value.StartSeconds) && double.IsFinite(value.EndSeconds))
                .Select(value => new VoiceInterval(Math.Max(segment.StartSeconds, value.StartSeconds), Math.Min(segment.EndSeconds, value.EndSeconds)))
                .Where(value => value.EndSeconds > value.StartSeconds).OrderBy(value => value.StartSeconds).ToArray();
            _segments[_current] = segment with { VoiceIntervalCount = owned.Length };
            foreach (var interval in owned)
            {
                if (_voices.LastOrDefault() is { } previous && interval.StartSeconds <= previous.EndSeconds)
                    _voices[^1] = previous with { EndSeconds = Math.Max(previous.EndSeconds, interval.EndSeconds) };
                else _voices.Add(interval);
            }
            Publish();
        }
    }

    public void Advance(double absoluteSeconds)
    {
        lock (_gate)
        {
            if (_current < 0 || !double.IsFinite(absoluteSeconds)) return;
            var segment = _segments[_current];
            _segments[_current] = segment with { RecognizedSeconds = Math.Max(segment.RecognizedSeconds,
                Math.Clamp(absoluteSeconds - segment.StartSeconds, 0, segment.EndSeconds - segment.StartSeconds)) };
            Publish();
        }
    }

    public void Cue()
    {
        lock (_gate)
        {
            _cues++;
            _segments[_current] = _segments[_current] with { CueCount = _segments[_current].CueCount + 1 };
            Publish();
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            var segment = _segments[_current];
            _segments[_current] = segment with { State = segment.VoiceIntervalCount == 0 ? "empty" : "done",
                RecognizedSeconds = segment.EndSeconds - segment.StartSeconds };
            Publish();
        }
    }

    private void Publish() => AiProgressStore.SetSpeech(_jobId,
        new(_duration, ChunkSeconds, _current, _segments, _voices.ToArray(), _cues, _stage));
}
