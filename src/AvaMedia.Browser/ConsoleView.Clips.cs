using System.Text.Json.Nodes;
using Avalonia.Controls;
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
    private readonly StackPanel _clipEditor = new() { Spacing = 6 };
    private readonly ComboBox _segmentChoice = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _removeSegments = new() { Content = "反选：去除所选片段" };
    private readonly CheckBox _joinSegments = new() { Content = "将每个视频的保留片段合并导出" };
    private readonly ComboBox _clipPreset = new() { ItemsSource = new[] { "MP4", "MKV", "TS", "原格式 / 原码率" }, SelectedIndex = 0 };
    private readonly TextBlock _clipSummary = Label("");
    private readonly RangeBar _range = new() { Height = 32, Step = .001, TrackBrush = Brushes.Gray,
        BorderBrush = Brushes.DimGray, SelectionBrush = Brushes.SteelBlue, HandleBrush = Brushes.WhiteSmoke, GripBrush = Brushes.SteelBlue };
    private readonly SegmentTimeline _segmentTrack = new() { Height = 44, TrackBrush = Brushes.LightGray,
        SelectionBrush = Brushes.SteelBlue, BorderBrush = Brushes.Gray, TextBrush = Brushes.Black, SelectedTextBrush = Brushes.White };
    private bool _syncClipUi;
    private string? SelectedPreviewPath => _previewInput.SelectedItem is FileBrowserItem item && !item.IsFolder ? _source.DisplayAddress(item) : null;
    private ClipDraft? CurrentClip => SelectedPreviewPath is { } path ? _clips.GetValueOrDefault(path) : null;

    private Control BuildClipEditor()
    {
        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(_range); root.Children.Add(_clipEditor);
        _range.Changed += (start, end) =>
        {
            var movedStart = Math.Abs(start - GetNumber("start")) > .00001;
            SetNumber("start", start); SetNumber("end", end);
            _seconds.Value = (decimal)(movedStart ? start : end);
        };
        _segmentChoice.SelectionChanged += (_, _) =>
        { if (!_syncClipUi && CurrentClip is { } draft && _segmentChoice.SelectedIndex >= 0) SelectSegment(draft, _segmentChoice.SelectedIndex); };
        _segmentTrack.NavigateRequested += (index, time) =>
        { if (CurrentClip is { } draft) { SelectSegment(draft, index); _seconds.Value = (decimal)time; } };
        _removeSegments.IsCheckedChanged += (_, _) =>
        { if (!_syncClipUi && CurrentClip is { } draft) { draft.RemoveSelected = _removeSegments.IsChecked == true; UpdateClipEditor(); } };
        _clipEditor.Children.Add(new ScrollViewer { Content = _segmentTrack,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        _clipEditor.Children.Add(_segmentChoice);
        _clipEditor.Children.Add(Actions(Action("新增片段", () => EditSegments("add")), Action("复制片段", () => EditSegments("copy")),
            Action("在此分割", () => EditSegments("split")), Action("删除片段", () => EditSegments("remove"))));
        _clipEditor.Children.Add(Actions(Action("片段上移", () => EditSegments("up")), Action("片段下移", () => EditSegments("down"))));
        _clipEditor.Children.Add(_removeSegments); _clipEditor.Children.Add(_clipSummary);
        Field(_clipEditor, "片段输出预设", _clipPreset); _clipEditor.Children.Add(_joinSegments);
        return root;
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
        SaveOptions(); draft.Selected = index; BuildToolOptions();
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
        _range.Duration = _previewInfo?["duration"]?.GetValue<double>() ?? 0;
        _range.IsVisible = _optionControls.ContainsKey("start") && _range.Duration > 0;
        _range.Start = GetNumber("start"); _range.End = GetNumber("end") > 0 ? GetNumber("end") : _range.Duration;
        _range.InvalidateVisual(); _clipEditor.IsVisible = _activeFeature == "clip";
        if (_activeFeature != "clip" || CurrentClip is not { } draft) return;
        SaveOptions();
        if (_previewInfo is not null)
        {
            draft.Duration = _previewInfo["duration"]?.GetValue<double>() ?? 0;
            draft.SourceVersion = _previewInfo.Text("sourceVersion");
        }
        _syncClipUi = true;
        try
        {
            var segments = draft.Segments.Select((segment, index) =>
            {
                var start = segment["start"]?.GetValue<double>() ?? 0;
                var end = segment["end"]?.GetValue<double>() ?? 0; if (end == 0) end = draft.Duration;
                var speed = draft.RemoveSelected ? 1 : segment["speed"]?.GetValue<double>() ?? 1;
                return new SegmentTimelineItem(index + 1, start, end, speed, $"片段 {index + 1}", TimeLabel((end - start) / speed));
            }).ToArray();
            _segmentChoice.ItemsSource = segments.Select(item => $"{item.Label} · {TimeLabel(item.Start)} – {TimeLabel(item.End)}").ToArray();
            _segmentChoice.SelectedIndex = draft.Selected; _removeSegments.IsChecked = draft.RemoveSelected;
            _segmentTrack.SetItems(segments, draft.Selected); _segmentTrack.SetPosition((double)(_seconds.Value ?? 0));
            // The desktop planner resolves exclusion unions and validates actual output ranges on submission.
            _clipSummary.Text = draft.RemoveSelected ? $"去除 {segments.Length} 个区间，导出其余画面" : $"{segments.Length} 个片段 · 总输出时长 {TimeLabel(segments.Sum(item => (item.End - item.Start) / item.Speed))}";
        }
        finally { _syncClipUi = false; }
    }
    private JsonObject ClipSubmission()
    {
        SaveOptions(); var edits = new JsonArray();
        foreach (var (path, item) in _inputs)
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
