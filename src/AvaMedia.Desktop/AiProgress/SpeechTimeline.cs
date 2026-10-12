using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaMedia.Core;

namespace AvaMedia.Desktop.AiProgress;

// The drawing is bounded by the viewport width, regardless of recording length.
internal sealed class SpeechTimeline : Control
{
    private SpeechProgressSnapshot? _speech;
    private AudioEnvelope? _envelope;
    private double _sourceStart, _speed = 1;
    public bool SegmentsOnly { get; init; }
    public SpeechTimeline() { ClipToBounds = true; Height = 104; }
    public void Update(SpeechProgressSnapshot? speech, AudioEnvelope? envelope, double sourceStart, double speed)
    { _speech = speech; _envelope = envelope; _sourceStart = sourceStart; _speed = speed > 0 ? speed : 1; InvalidateVisual(); }
    private IBrush Brush(string key, string fallback) => this.TryFindResource(key, out var value) && value is IBrush brush ? brush : Avalonia.Media.Brush.Parse(fallback);
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width; if (width <= 0 || _speech is not { DurationSeconds: > 0 } speech) return;
        var blue = Brush("UiAccent", "#007AFF"); var grey = Brush("UiDivider", "#E5E5EA"); var secondary = Brush("UiTextSecondary", "#86868B");
        var origin = speech.Segments.FirstOrDefault()?.StartSeconds ?? _sourceStart;
        var recognized = speech.Segments.Where(s => s.State != "pending").Select(s => s.State is "done" or "empty" ? s.EndSeconds - origin : s.StartSeconds - origin + s.RecognizedSeconds).DefaultIfEmpty(0).Max();
        recognized = Math.Clamp(recognized, 0, speech.DurationSeconds);
        if (SegmentsOnly)
        {
            var count = speech.Segments.Length; if (count == 0) return;
            // Coalesce sub-pixel segments for very long media.
            var stride = Math.Max(1, (int)Math.Ceiling(count / Math.Max(1, width / 3)));
            var labelEvery = Math.Max(stride, (int)Math.Ceiling(count * 64 / width));
            for (var i = 0; i < count; i += stride)
            {
                var segment = speech.Segments[i]; var last = speech.Segments[Math.Min(count - 1, i + stride - 1)];
                var x = (segment.StartSeconds - origin) / speech.DurationSeconds * width;
                var end = (last.EndSeconds - origin) / speech.DurationSeconds * width;
                var w = Math.Max(1, end - x - Math.Min(4, (end - x) / 5));
                context.DrawRectangle(grey, null, new Rect(x, 0, w, 8), 3, 3);
                var filled = Math.Clamp((recognized - (segment.StartSeconds - origin)) / Math.Max(.001, last.EndSeconds - segment.StartSeconds), 0, 1);
                if (filled > 0) context.DrawRectangle(blue, null, new Rect(x, 0, w * filled, 8), 3, 3);
                if (i % labelEvery == 0)
                {
                    var label = new FormattedText(TimeLabel(segment.StartSeconds), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, secondary);
                    context.DrawText(label, new Point(Math.Clamp(x, 0, Math.Max(0, width - label.Width)), 13));
                }
            }
            return;
        }
        if (_envelope is not { Peaks.Length: > 0, DurationSeconds: > 0 } envelope) return;
        var bars = Math.Max(1, (int)(width / 5)); var height = Math.Max(10, Bounds.Height - 18);
        for (var i = 0; i < bars; i++)
        {
            var sourceFrom = _sourceStart + i / (double)bars * speech.DurationSeconds * _speed;
            var sourceTo = _sourceStart + (i + 1d) / bars * speech.DurationSeconds * _speed;
            if (sourceFrom >= envelope.DurationSeconds) break;
            var first = Math.Clamp((int)(sourceFrom / envelope.DurationSeconds * envelope.Peaks.Length), 0, envelope.Peaks.Length - 1);
            var last = Math.Clamp((int)Math.Ceiling(sourceTo / envelope.DurationSeconds * envelope.Peaks.Length), first + 1, envelope.Peaks.Length);
            float peak = 0; for (var b = first; b < last; b++) peak = Math.Max(peak, envelope.Peaks[b]);
            var h = Math.Max(2, peak * height); var x = i * width / bars;
            context.DrawRectangle(i / (double)bars * speech.DurationSeconds <= recognized ? blue : grey, null, new Rect(x, (height - h) / 2, 3, h), 1.5, 1.5);
        }
        foreach (var interval in speech.VoiceIntervals)
        {
            var x = Math.Clamp((interval.StartSeconds - origin) / speech.DurationSeconds * width, 0, width);
            var end = Math.Clamp((interval.EndSeconds - origin) / speech.DurationSeconds * width, 0, width);
            if (end <= x) continue;
            using (context.PushOpacity(.3)) context.DrawRectangle(blue, null, new Rect(x, Bounds.Height - 7, end - x, 4), 2, 2);
            var played = Math.Min(end, recognized / speech.DurationSeconds * width);
            if (played > x) context.DrawRectangle(blue, null, new Rect(x, Bounds.Height - 7, played - x, 4), 2, 2);
        }
        var cursor = recognized / speech.DurationSeconds * width;
        context.DrawLine(new Pen(Brush("UiText", "#1D1D1F"), 1.5), new Point(cursor, 0), new Point(cursor, Bounds.Height - 3));
    }
    public static string TimeLabel(double seconds) => seconds >= 3600 ? AiActivity.FormatElapsed(seconds) : TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss");
}
