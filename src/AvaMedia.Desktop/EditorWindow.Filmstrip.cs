using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.MediaUi;

namespace AvaMedia.Desktop;

// Thumbnail filmstrip above the segment order track. Frames come from the shared FrameStripCache
// (same extraction as the web /web/preview/strip endpoint) and appear progressively.
public partial class EditorWindow
{
    private static FrameStripCache? _stripCache;
    private static IMediaPreview? _stripCacheOwner;
    private bool _filmstripLoading;

    private void InitializeFilmstrip()
    {
        Filmstrip.Translate = Localization.Text;
        Filmstrip.NavigateRequested += (index, seconds) =>
        {
            if (index >= 0) _segmentReady = NavigateSegment(index, seconds);
            else Scrub(seconds);
        };
        Filmstrip.NavigateCompleted += seconds => Seek(seconds);
        Filmstrip.HandleMoved += (index, isStart, seconds, finished) =>
        {
            if (!finished) { Scrub(seconds); return; }
            if (isStart) ShiftStart(seconds - _options.Start); else ShiftEnd(seconds - _options.End);
            Seek(seconds);
        };
        TimelineZoom.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) ApplyTimelineZoom(); };
        SegmentTrackScroll.SizeChanged += (_, _) => ApplyTimelineZoom();
    }

    private void ApplyTimelineZoom()
    {
        if (QuickRoot.IsVisible) { Filmstrip.Width = double.NaN; Filmstrip.Zoom = TimelineZoom.Value; return; }
        var width = Math.Max(200, SegmentTrackScroll.Bounds.Width) * TimelineZoom.Value;
        Filmstrip.Width = SegmentTrack.MinWidth = width;
        SegmentTrack.Width = width;
    }
    private void ZoomOutClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => TimelineZoom.Value = Math.Max(TimelineZoom.Minimum, TimelineZoom.Value / 1.5);
    private void ZoomInClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => TimelineZoom.Value = Math.Min(TimelineZoom.Maximum, TimelineZoom.Value * 1.5);

    private void RefreshFilmstrip(int selected)
    {
        Filmstrip.RemoveMode = _removeSelected;
        Filmstrip.Duration = _info?.Duration ?? 0;
        Filmstrip.SetSegments(_segments.Select(s => new FilmstripTimeline.Segment(s.Options.Start, s.End, Localization.Format($"片段 {s.Number}"))).ToArray(), selected);
        var kept = _segments.Sum(s => (s.End - s.Options.Start) / (_removeSelected ? 1 : Math.Max(.25, s.Options.Speed)));
        ExportSummary.Text = _removeSelected
            ? Localization.Format($"去除 {_segments.Count} 段 · 保留 {MediaTime.Format(Math.Max(0, (_info?.Duration ?? 0) - kept))}")
            : Localization.Format($"{_segments.Count} 个片段 · 合计 {MediaTime.Format(kept)}");
        if (!_filmstripLoading && _info is { Duration: > 0 }) _ = LoadFilmstrip(_info.Duration);
    }

    private async Task LoadFilmstrip(double duration)
    {
        _filmstripLoading = true;
        try
        {
            await _ready.Task;
            if (_stripCache is null || !ReferenceEquals(_stripCacheOwner, _previewFrames)) { _stripCache = new FrameStripCache(_previewFrames); _stripCacheOwner = _previewFrames; }
            var frames = new SortedDictionary<int, (double, IImage)>();
            void Add(FrameStripFrame frame)
            {
                if (_closed || frame.Image.Length == 0 || frames.ContainsKey(frame.Index)) return;
                try { using var stream = new MemoryStream(frame.Image); frames[frame.Index] = (frame.Seconds, new Bitmap(stream)); }
                catch (Exception) { return; } // Skip undecodable frames; the strip just has fewer cells.
                Filmstrip.SetStrip(frames.Values.ToArray());
            }
            var progress = new Progress<FrameStripFrame>(Add);
            var result = await _stripCache.GetAsync(_path, duration, 16, 160, 90, _options.VideoStreamIndex, progress, _lifetime.Token);
            await Dispatcher.UIThread.InvokeAsync(() => { foreach (var frame in result) Add(frame); });
        }
        catch (OperationCanceledException) { }
        catch (Exception) { } // A filmstrip is optional; the segment track still works without frames.
    }
}
