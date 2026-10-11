using System.Text.Json.Nodes;
using Avalonia.Controls;
using AvaMedia.FileUi;

namespace AvaMedia.Browser;

public sealed partial class ConsoleView
{
    private readonly HashSet<string> _excludedInputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _rotationOverrides = new(StringComparer.Ordinal);
    private readonly StackPanel _batchPlanRows = new() { Spacing = 5 };
    private readonly ComboBox _cropMode = new() { ItemsSource = new[] { "相同像素区域", "按参考画面比例缩放" }, SelectedIndex = 0 };
    private JsonNode? _videoBatchPlan;
    private string? _videoBatchSnapshot;
    private IEnumerable<KeyValuePair<string, FileBrowserItem>> IncludedInputs => _inputs.Where(pair => !_excludedInputs.Contains(pair.Key));

    private void AddVideoBatchControls()
    {
        if (_activeFeature is not ("crop" or "rotate")) return;
        if (_activeFeature == "crop")
        {
            Field(_toolOptions, "应用到勾选视频", _cropMode);
            _cropMode.SelectionChanged -= CropModeChanged; _cropMode.SelectionChanged += CropModeChanged;
        }
        else
        {
            var directions = new StackPanel { Spacing = 5 };
            foreach (var (path, item) in IncludedInputs.Where(pair => !pair.Value.IsFolder))
            {
                var choice = new ComboBox { ItemsSource = new[] { "跟随统一方向", "无需旋转", "顺时针 90°", "180°", "逆时针 90°" },
                    SelectedIndex = _rotationOverrides.TryGetValue(path, out var angle) ? angle / 90 + 1 : 0 };
                choice.SelectionChanged += (_, _) =>
                {
                    if (choice.SelectedIndex == 0) _rotationOverrides.Remove(path); else _rotationOverrides[path] = (choice.SelectedIndex - 1) * 90;
                    InvalidateVideoBatch(); UpdateRegion();
                };
                Field(directions, item.Name, choice);
            }
            _toolOptions.Children.Add(new Expander { Header = "逐文件旋转方向", Content = directions, IsExpanded = true });
        }
        _toolOptions.Children.Add(Action("检查所有勾选视频", PreviewVideoBatch));
        _toolOptions.Children.Add(_batchPlanRows);
    }
    private void CropModeChanged(object? sender, SelectionChangedEventArgs e) => InvalidateVideoBatch();
    private void InvalidateVideoBatch() { _videoBatchPlan = null; _videoBatchSnapshot = null; _batchPlanRows.Children.Clear(); }
    private JsonObject VideoBatchSpec()
    {
        var sources = IncludedInputs.ToArray();
        if (sources.Length == 0 || sources.Any(pair => pair.Value.IsFolder)) throw new InvalidOperationException("请展开文件夹并勾选视频文件。");
        var options = ReadOptions(); var rotations = new JsonObject();
        foreach (var (path, angle) in _rotationOverrides.Where(pair => sources.Any(source => source.Key == pair.Key))) rotations[path] = angle;
        if (_activeFeature == "crop" && (_previewInfo is null || _previewStale)) throw new InvalidOperationException("请先读取参考视频预览并框选区域。");
        return new() { ["featureId"] = _activeFeature, ["paths"] = new JsonArray(sources.Select(pair => (JsonNode)JsonValue.Create(pair.Key)!).ToArray()),
            ["options"] = options, ["referencePath"] = SelectedPreviewPath, ["referenceVersion"] = _previewInfo.Text("sourceVersion"),
            ["cropMode"] = _cropMode.SelectedIndex, ["rotations"] = rotations };
    }
    private async Task PreviewVideoBatch()
    {
        var spec = VideoBatchSpec(); var snapshot = spec.ToJsonString();
        InvalidateVideoBatch();
        var plan = await RemoteApi.Call("preview_video_batch", new() { ["spec"] = spec });
        if (_activeFeature is not ("crop" or "rotate") || VideoBatchSpec().ToJsonString() != snapshot) return;
        _videoBatchPlan = plan; _videoBatchSnapshot = snapshot;
        foreach (var entry in plan["entries"].Items())
        {
            var path = entry.Text("path"); var name = _inputs.GetValueOrDefault(path)?.Name ?? path;
            var error = entry.Text("error"); var crop = entry["crop"];
            var description = error.Length > 0 ? error : crop is not null
                ? $"{crop["width"]} × {crop["height"]} · 区域 ({crop["x"]}, {crop["y"]}) · {entry["format"]}"
                : $"旋转 {entry["rotation"]}° · {(entry["rotation"]?.GetValue<int>() == 0 ? "跳过" : entry.Text("format"))}";
            _batchPlanRows.Children.Add(Label(name + " · " + description));
            if (error.Length > 0) _batchPlanRows.Children.Add(Action("取消勾选此文件", () =>
            { _excludedInputs.Add(path); InvalidateVideoBatch(); RenderInputs(); return Task.CompletedTask; }));
        }
    }
    private JsonObject VideoBatchSubmission()
    {
        var spec = VideoBatchSpec();
        if (_videoBatchPlan is null || _videoBatchSnapshot != spec.ToJsonString())
            throw new InvalidOperationException("参数或文件已变化，请先检查所有勾选视频。");
        if (_videoBatchPlan["entries"].Items().Any(entry => entry.Text("error").Length > 0)) throw new InvalidOperationException("请修正错误或取消勾选错误文件后重新检查。");
        return new() { ["requestId"] = Guid.NewGuid().ToString(), ["plan"] = _videoBatchPlan.DeepClone(),
            ["outputFolder"] = _output.Text, ["startImmediately"] = _start.IsChecked == true };
    }
}
