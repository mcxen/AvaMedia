using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;

namespace AvaMedia.Browser;

public sealed partial class ConsoleView
{
    private readonly Dictionary<string, JsonNode> _editorMedia = new(StringComparer.Ordinal);
    private readonly TextBlock _compressionPlan = Label("");
    private string? TrackSource(string kind) => _activeFeature == "mux"
        ? _inputs.Keys.ElementAtOrDefault(kind == "audio" ? 1 : 0) : SelectedPreviewPath;

    private ComboBox BuildTrackChoice(string kind, JsonNode? selected)
    {
        var source = TrackSource(kind);
        var media = source is null ? null : _editorMedia.GetValueOrDefault(source);
        var tracks = media?["tracks"].Items().Where(track => track.Text("kind") == kind)
            .Select(track => (JsonNode)new JsonObject { ["label"] = track.Text("label"), ["value"] = track["index"]?.DeepClone() }).ToList() ?? [];
        if (kind == "subtitle") tracks.Insert(0, new JsonObject { ["label"] = "自动选择", ["value"] = -1 });
        if (tracks.Count == 0) tracks.Add(new JsonObject { ["label"] = media is null ? "默认轨道（读取后可选择）" : "没有此类轨道", ["value"] = 0 });
        var choice = new ComboBox { ItemsSource = tracks, HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<JsonNode>((item, _) => Label(item.Text("label"))) };
        choice.SelectedItem = tracks.FirstOrDefault(track => JsonNode.DeepEquals(track["value"], selected)) ?? tracks.First();
        return choice;
    }
    private async Task ReadEditorTracks()
    {
        var feature = _activeFeature;
        var paths = feature == "mux" ? _inputs.Keys.Take(2).ToArray() : SelectedPreviewPath is { } path ? new[] { path } : [];
        if (paths.Length == 0) throw new InvalidOperationException("请先添加文件。");
        foreach (var source in paths)
        {
            var media = await RemoteApi.Call("editor_media", new() { ["path"] = source });
            _editorMedia[source] = media;
            while (_editorMedia.Count > 8) _editorMedia.Remove(_editorMedia.Keys.First());
        }
        if (_activeFeature == feature) BuildToolOptions();
    }
    private void AddMediaDetails(JsonNode form)
    {
        if (form["fields"].Items().Any(field => field.Text("kind").EndsWith("-track", StringComparison.Ordinal)) || _activeFeature == "info")
            _toolOptions.Children.Add(Action("读取媒体信息 / 实际轨道", ReadEditorTracks));
        if (_activeFeature == "info" && SelectedPreviewPath is { } path && _editorMedia.GetValueOrDefault(path) is { } media)
        {
            var info = media["info"];
            _toolOptions.Children.Add(Label($"{info?["width"]} × {info?["height"]} · {info?["frameRate"]} fps · {TimeLabel(info?["duration"]?.GetValue<double>() ?? 0)}"));
            _toolOptions.Children.Add(Label($"文件体积 {(media["bytes"]?.GetValue<long>() ?? 0) / 1000000d:0.##} MB"));
            foreach (var track in media["tracks"].Items()) _toolOptions.Children.Add(Label(track.Text("label")));
        }
        if (_activeFeature != "video-compress") return;
        _compressionPlan.Text = "";
        _toolOptions.Children.Add(Action("预估当前视频压缩体积", async () =>
        {
            var source = PreviewPath(); SaveOptions();
            var options = _optionDraft["videoCompression"]?.DeepClone(); var snapshot = options?.ToJsonString();
            var plan = await RemoteApi.Call("preview_compression", new() { ["path"] = source, ["options"] = options });
            SaveOptions();
            if (_activeFeature != "video-compress" || source != SelectedPreviewPath || snapshot != _optionDraft["videoCompression"]?.ToJsonString()) return;
            var estimate = plan["estimatedBytes"]?.GetValue<long>();
            _compressionPlan.Text = $"{plan["width"]} × {plan["height"]} · 源文件 {(plan["sourceBytes"]?.GetValue<long>() ?? 0) / 1000000d:0.##} MB\n"
                + (estimate is { } bytes ? $"预计 {bytes / 1000000d:0.##} MB · 视频 {plan["videoBitrate"]} kbps" : "质量模式：实际体积取决于画面内容，无法预估。")
                + $" · 音频 {plan["audioBitrate"]} kbps";
        }));
        _toolOptions.Children.Add(_compressionPlan);
    }
}
