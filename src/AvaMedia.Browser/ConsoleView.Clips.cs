using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.FileUi;
using AvaMedia.MediaUi;

namespace AvaMedia.Browser;

public sealed partial class ConsoleView
{
    private sealed class ClipDraft(JsonObject initial)
    {
        public List<JsonObject> Segments { get; } = [initial];
        public int Selected { get; set; }
        public bool RemoveSelected { get; set; }
        public string SourceVersion { get; set; } = "";
        public double Duration { get; set; }
    }
    private readonly Dictionary<string, ClipDraft> _clips = new(StringComparer.Ordinal);
    private readonly CheckBox _removeSegments = new() { Content = "反选：去除所选片段" };
    private readonly CheckBox _joinSegments = new() { Content = "将每个视频的保留片段合并导出" };
    private readonly ComboBox _clipPreset = new() { ItemsSource = new[] { "MP4", "MKV", "TS", "原格式 / 原码率", "原格式" }, SelectedIndex = 3 };
    private readonly TextBlock _clipSummary = Ui.Text("", "caption");
    private readonly StackPanel _clipSide = new() { Spacing = 10 };
    private readonly StackPanel _segmentRows = new() { Spacing = 2 };
    private Control _clipActions = new Panel();
    private readonly Grid _boundaryCards = new() { ColumnDefinitions = new("*,*"), ColumnSpacing = 8 };
    private readonly TextBlock _segmentTitle = Ui.Text("", "section", raw: true);
    private readonly DispatcherTimer _boundaryTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private CancellationTokenSource? _boundaryCancellation;
    private string _boundaryKey = "";
    private readonly Dictionary<string, IReadOnlyList<(double, Bitmap)>> _strips = new(StringComparer.Ordinal);
    private bool _syncClipUi;
    private string? SelectedPreviewPath => _previewInput.SelectedItem is FileBrowserItem item && !item.IsFolder ? _source.DisplayAddress(item) : null;
    private ClipDraft? CurrentClip => SelectedPreviewPath is { } path ? _clips.GetValueOrDefault(path) : null;

    private string TimelineStartKey => _activeFeature == "contact-sheet" ? "contactSheet.startSeconds" : "start";
    private string TimelineEndKey => _activeFeature == "contact-sheet" ? "contactSheet.endSeconds" : "end";

    private void BuildClipEditor()
    {
        _removeSegments.IsCheckedChanged += (_, _) =>
        { if (!_syncClipUi && CurrentClip is { } draft) { draft.RemoveSelected = _removeSegments.IsChecked == true; UpdateClipEditor(); } };
        _clipActions = Actions(
            Action("设为起点", () => { SetNumber(TimelineStartKey, (double)(_seconds.Value ?? 0)); ScheduleBoundaries(); return Task.CompletedTask; }, "arrow-down-to-line"),
            Action("设为终点", () => { SetNumber(TimelineEndKey, (double)(_seconds.Value ?? 0)); ScheduleBoundaries(); return Task.CompletedTask; }, "arrow-down-to-line"),
            Action("在此分割", () => EditSegments("split"), "split"), Action("新增片段", () => EditSegments("add"), "plus"),
            Action("复制片段", () => EditSegments("copy"), "copy"), Action("删除片段", () => EditSegments("remove"), "trash-2"));
        var header = new Grid { ColumnDefinitions = new("*,Auto") };
        header.Children.Add(Ui.Text("编辑片段", "section"));
        var moves = Ui.Row(2, IconButton("chevron-up", "片段上移", () => EditSegments("up")), IconButton("chevron-down", "片段下移", () => EditSegments("down")));
        Grid.SetColumn(moves, 1); header.Children.Add(moves);
        _clipSide.Children.Add(header); _clipSide.Children.Add(_clipSummary);
        _clipSide.Children.Add(new Border { Background = Palette.Fill, CornerRadius = new(10), Padding = new(4), Child = _segmentRows });
        _clipSide.Children.Add(_segmentTitle); _clipSide.Children.Add(_boundaryCards);
        _clipSide.Children.Add(_removeSegments);
        _clipSide.Children.Add(Ui.Line("导出格式", _clipPreset)); _clipSide.Children.Add(_joinSegments);
        _boundaryTimer.Tick += async (_, _) => { _boundaryTimer.Stop(); await LoadBoundaries(); };
    }
    private Button IconButton(string icon, string tip, Func<Task> run)
    {
        var button = new Button { Content = Ui.Icon(icon, 14, Palette.Text), Classes = { "plain", "icon" }, Width = 26, Height = 26 };
        ToolTip.SetTip(button, L.T(tip)); Avalonia.Automation.AutomationProperties.SetName(button, tip);
        button.Click += async (_, _) => await Guard(run);
        return button;
    }
    private async Task LoadStrip(string path)
    {
        if (_strips.TryGetValue(path, out var cached)) { _timeline.SetStrip(cached); return; }
        _strips[path] = [];
        try
        {
            var strip = await RemotePreview.Strip(path, _mobile ? 8 : 14, _optionDraft["videoStreamIndex"]?.GetValue<int>() ?? 0, CancellationToken.None);
            var frames = new List<(double, Bitmap)>();
            foreach (var frame in strip["frames"].Items())
            {
                using var stream = new MemoryStream(Convert.FromBase64String(frame.Text("image")));
                frames.Add((frame["seconds"]!.GetValue<double>(), new Bitmap(stream)));
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
            _strips[path] = frames;
            if (SelectedPreviewPath == path) _timeline.SetStrip(frames);
        }
        catch (Exception) { _strips.Remove(path); }
    }
    private void ScheduleBoundaries() { if (_activeFeature == "clip") { _boundaryTimer.Stop(); _boundaryTimer.Start(); } }
    private async Task LoadBoundaries()
    {
        if (_activeFeature != "clip" || SelectedPreviewPath is not { } path || _previewInfo is null) return;
        var duration = _previewInfo["duration"]?.GetValue<double>() ?? 0;
        var start = GetNumber("start"); var end = GetNumber("end"); if (end <= 0) end = duration;
        var key = $"{path}|{start:R}|{end:R}"; if (key == _boundaryKey) return;
        _boundaryKey = key; _boundaryCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(); _boundaryCancellation = cancellation;
        RenderBoundaryCards(start, end, null, null);
        try
        {
            var result = await RemotePreview.Boundaries(path, start, end, _optionDraft["videoStreamIndex"]?.GetValue<int>() ?? 0, cancellation.Token);
            if (key != _boundaryKey) return;
            Bitmap? Image(string edge)
            {
                var frame = result["frames"].Items().FirstOrDefault(item => item.Text("edge") == edge);
                if (frame is null) return null; using var stream = new MemoryStream(Convert.FromBase64String(frame.Text("image"))); return new Bitmap(stream);
            }
            var frames = result["frames"].Items().ToArray();
            RenderBoundaryCards(frames.FirstOrDefault(f => f.Text("edge") == "start")?["seconds"]?.GetValue<double>() ?? start,
                frames.FirstOrDefault(f => f.Text("edge") == "end")?["seconds"]?.GetValue<double>() ?? end, Image("start"), Image("end"));
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (key == _boundaryKey) _boundaryKey = ""; }
        finally { if (ReferenceEquals(_boundaryCancellation, cancellation)) _boundaryCancellation = null; }
    }
    private void RenderBoundaryCards(double start, double end, Bitmap? startImage, Bitmap? endImage)
    {
        _boundaryCards.Children.Clear();
        foreach (var (edge, seconds, image, column) in new[] { ("起点", start, startImage, 0), ("终点", end, endImage, 1) })
        {
            var key = column == 0 ? TimelineStartKey : TimelineEndKey;
            var frame = new Border { Height = 72, CornerRadius = new(7), ClipToBounds = true, Background = new SolidColorBrush(Color.Parse("#1C1C1E")),
                Child = image is null ? Ui.Icon("loader-circle", 16, Palette.Secondary) : new Image { Source = image, Stretch = Stretch.UniformToFill } };
            var badge = new Border { Background = column == 0 ? Palette.Blue : Palette.Orange, CornerRadius = new(5), Padding = new(6, 1), Margin = new(5),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = new TextBlock { Text = L.T(edge), Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeight.SemiBold } };
            var stack = new Grid(); stack.Children.Add(frame); stack.Children.Add(badge);
            var time = Ui.Text(ClipTimeline.Long(seconds), "mono", raw: true); time.FontSize = 12; time.HorizontalAlignment = HorizontalAlignment.Center;
            var captured = seconds;
            async Task Nudge(int frames)
            {
                var stream = _optionDraft["videoStreamIndex"]?.GetValue<int>() ?? 0;
                var next = await RemotePreview.Nudge(PreviewPath(), captured, frames, stream, CancellationToken.None);
                // The end card shows the last frame before the exclusive end, so the end lands on the following frame.
                var value = column == 1 ? await RemotePreview.Nudge(PreviewPath(), next, 1, stream, CancellationToken.None) : next;
                if (column == 1 && value <= next) value = _previewInfo?["duration"]?.GetValue<double>() ?? value;
                SetNumber(key, value); _seconds.Value = (decimal)next; ScheduleBoundaries();
            }
            var tools = Ui.Row(0, IconButton("chevron-left", "后退一帧", () => Nudge(-1)), time, IconButton("chevron-right", "前进一帧", () => Nudge(1)));
            tools.HorizontalAlignment = HorizontalAlignment.Center;
            var locate = new Button { Content = L.T("定位"), Classes = { "link" }, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 };
            locate.Click += (_, _) => _seconds.Value = (decimal)captured;
            var card = new StackPanel { Spacing = 4, Children = { stack, tools, locate } };
            Grid.SetColumn(card, column); _boundaryCards.Children.Add(card);
        }
    }
    private JsonObject ClipOptions(JsonNode? feature)
    {
        var initial = feature?["form"]?["defaults"]?.DeepClone() as JsonObject ?? new();
        if (SelectedPreviewPath is not { } path) return initial;
        if (!_clips.TryGetValue(path, out var draft)) _clips[path] = draft = new(initial);
        return draft.Segments[draft.Selected];
    }
    private void SelectSegment(ClipDraft draft, int index)
    {
        SaveOptions(); draft.Selected = index; _boundaryKey = ""; BuildToolOptions();
        _seconds.Value = (decimal)GetNumber("start");
    }
    private Task EditSegments(string action)
    {
        if (CurrentClip is not { } draft || draft.Duration <= 0) throw new InvalidOperationException("请先读取当前视频预览。");
        SaveOptions(); var index = draft.Selected; var current = draft.Segments[index];
        var start = current["start"]?.GetValue<double>() ?? 0;
        var end = current["end"]?.GetValue<double>() ?? 0; if (end == 0) end = draft.Duration;
        var position = (double)(_seconds.Value ?? 0);
        if (action is "add" or "copy" or "split" && draft.Segments.Count >= 100)
            throw new InvalidOperationException("每个视频最多 100 个编辑片段。");
        switch (action)
        {
            case "add":
                if (position >= draft.Duration) throw new InvalidOperationException("请先定位到视频结尾之前。");
                var added = (JsonObject)current.DeepClone(); added["start"] = position; added["end"] = draft.Duration;
                draft.Segments.Insert(index + 1, added); draft.Selected++; break;
            case "copy": draft.Segments.Insert(index + 1, (JsonObject)current.DeepClone()); draft.Selected++; break;
            case "split":
                if (position <= start || position >= end) throw new InvalidOperationException("请将播放位置放在当前片段内部。");
                var next = (JsonObject)current.DeepClone(); next["start"] = position; next["end"] = end;
                current["end"] = position;
                // Update the live control before BuildToolOptions saves the current draft again.
                SetNumber("end", position); draft.Segments.Insert(index + 1, next); draft.Selected++; break;
            case "remove":
                if (draft.Segments.Count == 1) throw new InvalidOperationException("至少保留一个编辑片段。");
                draft.Segments.RemoveAt(index); draft.Selected = Math.Min(index, draft.Segments.Count - 1); break;
            case "up": case "down":
                var target = index + (action == "up" ? -1 : 1);
                if (target < 0 || target >= draft.Segments.Count) return Task.CompletedTask;
                (draft.Segments[index], draft.Segments[target]) = (draft.Segments[target], draft.Segments[index]); draft.Selected = target; break;
        }
        BuildToolOptions(); return Task.CompletedTask;
    }
    private void UpdateClipEditor()
    {
        if (_syncClipUi) return;
        var duration = _previewInfo?["duration"]?.GetValue<double>() ?? 0;
        _timeline.Duration = duration; _timeline.Position = (double)(_seconds.Value ?? 0);
        if (SelectedPreviewPath is { } path && _strips.TryGetValue(path, out var strip)) _timeline.SetStrip(strip); else _timeline.SetStrip([]);
        if (_activeFeature == "delogo") { UpdateDelogoTimeline(); return; }
        if (_activeFeature != "clip" || CurrentClip is not { } draft)
        {
            var end = GetNumber(TimelineEndKey); if (end <= 0) end = duration;
            _timeline.RemoveMode = false;
            _timeline.SetSegments(_optionControls.ContainsKey(TimelineStartKey) && duration > 0 ? [new(GetNumber(TimelineStartKey), end)] : [], 0);
            return;
        }
        SaveOptions();
        if (_previewInfo is not null)
        {
            draft.Duration = duration;
            draft.SourceVersion = _previewInfo.Text("sourceVersion");
        }
        _syncClipUi = true;
        try
        {
            var segments = draft.Segments.Select(segment =>
            {
                var start = segment["start"]?.GetValue<double>() ?? 0;
                var end = segment["end"]?.GetValue<double>() ?? 0; if (end == 0) end = draft.Duration;
                var speed = draft.RemoveSelected ? 1 : segment["speed"]?.GetValue<double>() ?? 1;
                return (Start: start, End: end, Speed: speed);
            }).ToArray();
            _timeline.RemoveMode = draft.RemoveSelected;
            _timeline.SetSegments(segments.Select(item => new ClipTimeline.Segment(item.Start, item.End)).ToArray(), draft.Selected);
            _removeSegments.IsChecked = draft.RemoveSelected;
            _segmentRows.Children.Clear();
            for (var index = 0; index < segments.Length; index++)
            {
                var (start, end, speed) = segments[index]; var captured = index;
                var row = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto"), ColumnSpacing = 8 };
                row.Children.Add(Ui.Icon("grip-vertical", 13, Palette.Secondary));
                var name = Ui.Text(L.F("片段 {0}", index + 1), "strong"); Grid.SetColumn(name, 1); row.Children.Add(name);
                var range = Ui.Text($"{ClipTimeline.Short(start)} → {ClipTimeline.Short(end)}", "secondary", raw: true); range.FontSize = 12.5; Grid.SetColumn(range, 2); row.Children.Add(range);
                var length = Ui.Text(L.F("{0} 秒", ((end - start) / speed).ToString("0.0")), "caption"); Grid.SetColumn(length, 3); row.Children.Add(length);
                var button = new Button { Content = row, Classes = { "row" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(8, 6) };
                if (index == draft.Selected) button.Classes.Add("selected");
                button.Click += (_, _) => { if (captured != draft.Selected) SelectSegment(draft, captured); };
                _segmentRows.Children.Add(button);
            }
            _segmentTitle.Text = L.F("片段 {0} · 首尾帧", draft.Selected + 1);
            UpdateClipSummary();
        }
        finally { _syncClipUi = false; }
        ScheduleBoundaries();
    }
    private void UpdateClipSummary()
    {
        if (CurrentClip is not { } draft) { _clipSummary.Text = ""; return; }
        var total = draft.Segments.Sum(segment =>
        {
            var start = segment["start"]?.GetValue<double>() ?? 0; var end = segment["end"]?.GetValue<double>() ?? 0; if (end == 0) end = draft.Duration;
            return (end - start) / (draft.RemoveSelected ? 1 : segment["speed"]?.GetValue<double>() ?? 1);
        });
        // The desktop planner resolves exclusion unions and validates actual output ranges on submission.
        _clipSummary.Text = draft.RemoveSelected ? L.F("去除 {0} 个区间，导出其余画面", draft.Segments.Count)
            : L.F("{0} 个片段 · 总输出时长 {1}", draft.Segments.Count, ClipTimeline.Short(total));
    }
    private JsonObject ClipSubmission()
    {
        SaveOptions(); var edits = new JsonArray();
        foreach (var (path, item) in IncludedInputs)
        {
            if (item.IsFolder || !_clips.TryGetValue(path, out var draft) || draft.SourceVersion.Length == 0)
                throw new InvalidOperationException("请依次打开每个视频预览并完成剪辑；文件夹请展开后选择视频。");
            edits.Add((JsonNode)new JsonObject { ["path"] = path, ["sourceVersion"] = draft.SourceVersion,
                ["removeSelected"] = draft.RemoveSelected, ["segments"] = new JsonArray(draft.Segments.Select(segment => segment.DeepClone()).ToArray()) });
        }
        if (edits.Count == 0) throw new InvalidOperationException("请先添加视频。");
        return new() { ["requestId"] = Guid.NewGuid().ToString(), ["edits"] = edits,
            ["preset"] = _clipPreset.SelectedIndex == 3 ? "Fast Copy" : _clipPreset.SelectedItem?.ToString() ?? "MP4",
            ["exportOptions"] = ReadOptions(), ["joinSegments"] = _joinSegments.IsChecked == true,
            ["outputFolder"] = _output.Text, ["startImmediately"] = _start.IsChecked == true };
    }
    private static string TimeLabel(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }
}
