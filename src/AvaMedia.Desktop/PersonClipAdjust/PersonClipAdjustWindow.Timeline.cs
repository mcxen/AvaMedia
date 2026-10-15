using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;
using AvaMedia.Desktop.MultiStage;

namespace AvaMedia.Desktop.PersonClipAdjust;

internal sealed partial class PersonClipAdjustWindow
{
    private readonly AdjustTimeline _timeline = new();
    private readonly Slider _zoom = new() { Classes = { "ac" }, Minimum = 0, Maximum = 6, Width = 84 };
    private readonly TextBlock _viewRange = new() { Classes = { "ms-small" }, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly PreviewRequest _stripRequest = new(), _dragRequest = new();
    private readonly DispatcherTimer _stripTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly Dictionary<long, Bitmap> _stripFrames = [];
    private bool _syncZoom;
    private (double Start, double End)? _selectedRange;
    private (int Id, double Start, double End)? _liveBounds;
    internal (int Id, double Start, double End)? LiveBounds => _liveBounds;
    partial void RefreshLiveBounds();

    partial void BuildTimelineContent()
    {
        var panel = new StackPanel { Spacing = 8 };
        var tools = new Grid { ColumnDefinitions = new("Auto,*,Auto"), MinHeight = 28 };
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] {
            TextButton("新增片段", AddAtPlayhead, "plus"),
            TextButton("在播放头拆分", SplitAtPlayhead, "scissors"),
            TextButton("与下一段合并", () => { if (Selected is { } s) Do(() => _session.MergeWithNext(s.Id)); }),
            TextButton("设为起点", () => SetBoundFromPlayhead(true)),
            TextButton("设为终点", () => SetBoundFromPlayhead(false)) })
        { button.Margin = new(0, 0, 6, 0); actions.Children.Add(button); }
        tools.Children.Add(actions);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var snap = new ToggleSwitch { Classes = { "ac", "small" }, IsChecked = true, OnContent = null, OffContent = null };
        Avalonia.Automation.AutomationProperties.SetName(snap, Localization.Text("吸附检测边界"));
        snap.IsCheckedChanged += (_, _) => _timeline.SnapEnabled = snap.IsChecked == true;
        right.Children.Add(snap); right.Children.Add(MsUi.Caption(Localization.Text("吸附检测边界")));
        right.Children.Add(IconButton("zoom-out", "缩小", () => _timeline.Zoom /= 1.25));
        Avalonia.Automation.AutomationProperties.SetName(_zoom, Localization.Text("时间线缩放"));
        _zoom.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty && !_syncZoom) _timeline.Zoom = Math.Pow(2, _zoom.Value); };
        right.Children.Add(_zoom);
        right.Children.Add(IconButton("zoom-in", "放大", () => _timeline.Zoom *= 1.25));
        var all = TextButton("全片", _timeline.ShowAll); all.Classes.Add("link"); right.Children.Add(all);
        Grid.SetColumn(right, 2); tools.Children.Add(right); panel.Children.Add(tools);
        _timeline.Snap = _session.Snap; _timeline.MinimumSeconds = _session.MinimumSeconds;
        _timeline.Seek += SeekTo;
        _timeline.SegmentClicked += id => Select(id, reveal: false);
        _timeline.BoundsPreview += (id, start, end, snapTarget) =>
        {
            _liveBounds = (id, start, end); RefreshLiveBounds();
            SeekTo(_timeline.DragPosition);
            _ = LoadDragFrameAsync(start, end, id);
        };
        _timeline.BoundsCommitted += (id, start, end) => { _liveBounds = null; Do(() => _session.SetBounds(id, start, end)); RefreshInspector(); };
        _timeline.DragCancelled += () => { _liveBounds = null; RefreshInspector(); };
        _timeline.RangeChanged += range => _selectedRange = range;
        _timeline.ViewChanged += () => { UpdateViewLabel(); ScheduleStrip(); };
        _timeline.SizeChanged += (_, _) => ScheduleStrip();
        _timeline.ExclusionMenuRequested += (index, point) =>
        {
            var remove = new MenuItem { Header = Localization.Text("移除排除区间") };
            remove.Click += (_, _) => Do(() => _session.RemoveExclusion(index));
            new ContextMenu { Items = { remove } }.Open(_timeline);
        };
        _timeline.ExclusionBoundsCommitted += (index, start, end) => Do(() =>
        {
            _session.RemoveExclusion(index);
            try { _session.AddExclusion(start, end); } catch { _session.Undo(); throw; }
        });
        _timeline.SegmentMenuRequested += (id, at, point) => { if (id is { } segmentId) Select(segmentId, seek: false); if (Selected is { } s) OpenSegmentMenu(_timeline, s.Id); };
        panel.Children.Add(_timeline);
        var foot = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        var legend = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (text, brush, outline) in new[] {
            ("有人 · 保留", "UiAccent", false), ("已手动移除", "AcTextTertiary", true),
            ("无人 · 移除", "AcTextTertiary", false), ("未检测（黑灯画面 / 排除区间）", "AcUnscannedFill", false),
            ("不确定", "UiWarning", false), ("已调整", "UiWarning", false) })
        {
            var mark = new Border { Width = 7, Height = 7, CornerRadius = new(2), VerticalAlignment = VerticalAlignment.Center };
            MsUi.Brush(mark, outline ? Border.BorderBrushProperty : Border.BackgroundProperty, brush);
            if (outline) mark.BorderThickness = new(1);
            legend.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new(0, 0, 12, 0), Children = { mark, MsUi.Small(Localization.Text(text)) } });
        }
        foot.Children.Add(legend); Grid.SetColumn(_viewRange, 1); foot.Children.Add(_viewRange); panel.Children.Add(foot);
        _timelineSlot.Children.Add(new Border { Classes = { "ms-card" }, Padding = new(14, 10), Child = panel });
        _stripTimer.Tick += (_, _) => { _stripTimer.Stop(); _ = LoadStripAsync(); };
    }

    partial void SelectTimeline(int? id, bool reveal)
    {
        _timeline.SelectedId = id;
        if (reveal && Selected is { } segment) _timeline.Reveal(segment.Start, segment.End);
    }
    partial void TrackPosition(double seconds) => _timeline.Position = seconds;
    partial void RefreshTimeline()
    {
        _timeline.Update(_session.Segments, _session.Spans, _session.Samples, _session.Exclusions, _session.Duration, _session.Threshold);
        _timeline.SelectedId = _selected; UpdateViewLabel();
    }
    private void UpdateViewLabel()
    {
        _syncZoom = true; _zoom.Value = Math.Log2(_timeline.Zoom); _syncZoom = false;
        _viewRange.Text = Localization.Format($"显示 {AdjustTime.Clock(_timeline.ViewStart, 0)} – {AdjustTime.Clock(_timeline.ViewStart + _timeline.VisibleDuration, 0)} · 共 {AdjustTime.Clock(_session.Duration, 0)}");
    }
    partial void ScheduleStrip() { if (_closed) return; _stripTimer.Stop(); _stripTimer.Start(); }
    private async Task LoadStripAsync()
    {
        var token = _stripRequest.Restart(_lifetime.Token);
        var times = _timeline.TileTimes();
        // Keep only the visible filmstrip, plus the current drag frame. No cache growth while panning.
        _timeline.ClearFrames();
        var wanted = times.Select(AdjustTime.Key).ToHashSet();
        foreach (var key in _stripFrames.Keys.Where(key => !wanted.Contains(key)).ToArray()) { _stripFrames[key].Dispose(); _stripFrames.Remove(key); }
        await Task.WhenAll(times.Select(async at =>
        {
            var key = AdjustTime.Key(at);
            if (_stripFrames.TryGetValue(key, out var existing)) { _timeline.SetFrame(at, existing); return; }
            var frame = await DecodeAsync(at, 128, 72, token);
            if (frame is null) return;
            if (!_stripRequest.IsCurrent(token) || _closed) { frame.Dispose(); return; }
            if (_stripFrames.TryGetValue(key, out var old)) old.Dispose();
            _stripFrames[key] = frame; _timeline.SetFrame(at, frame);
        }));
    }
    private Bitmap? _dragFrame;
    private async Task LoadDragFrameAsync(double start, double end, int id)
    {
        var token = _dragRequest.Restart(_lifetime.Token);
        var original = _session.Segments.First(s => s.Id == id);
        var at = Math.Abs(original.Start - start) > 1e-6 ? start : end;
        var bitmap = await DecodeAsync(at, 108, 61, token);
        if (bitmap is null) return;
        if (!_dragRequest.IsCurrent(token) || _closed) { bitmap.Dispose(); return; }
        _timeline.DragFrame = bitmap; var old = _dragFrame; _dragFrame = bitmap; old?.Dispose(); _timeline.InvalidateVisual();
    }
    partial void DisposeTimeline()
    {
        _stripTimer.Stop(); _stripRequest.Dispose(); _dragRequest.Dispose(); _timeline.ClearFrames(); _dragFrame?.Dispose();
        foreach (var image in _stripFrames.Values) image.Dispose(); _stripFrames.Clear();
    }
    private void AddAtPlayhead()
    {
        var end = Math.Min(_session.Duration, _position + 2);
        var next = _session.Segments.Where(s => s.Start > _position).MinBy(s => s.Start);
        if (next is not null) end = Math.Min(end, next.Start);
        Do(() => Select(_session.Add(_position, end)));
    }
    private void SplitAtPlayhead() { if (Selected is { } segment) Do(() => Select(_session.Split(segment.Id, _position))); }
    private void SetBoundFromPlayhead(bool start)
    {
        if (Selected is { } segment) Do(() => _session.SetBounds(segment.Id, start ? _position : segment.Start, start ? segment.End : _position));
    }
}
