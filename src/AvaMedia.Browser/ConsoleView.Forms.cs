using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using AvaMedia.FileUi;

namespace AvaMedia.Browser;

public sealed partial class ConsoleView
{
    private readonly StackPanel _toolOptions = new() { Spacing = 8 };
    private readonly Dictionary<string, Func<JsonNode?>> _optionReaders = new();
    private readonly Dictionary<string, Control> _optionControls = new();
    private readonly Dictionary<string, (Control Container, JsonNode Field)> _optionFields = new();
    private readonly Dictionary<string, JsonObject> _drafts = new();
    private string _activeFeature = "";
    private JsonObject _optionDraft = new();
    private JsonNode[] _features = [];

    private void BuildToolOptions()
    {
        SaveOptions();
        if (_activeFeature.Length > 0) _drafts[_activeFeature] = _optionDraft;
        var previousFeature = _activeFeature;
        var feature = _feature.SelectedItem as JsonNode;
        _activeFeature = feature.Text("id");
        if (previousFeature != _activeFeature) ClearPreview();
        _optionDraft = _activeFeature == "clip" ? ClipOptions(feature)
            : _drafts.GetValueOrDefault(_activeFeature) ?? feature?["form"]?["defaults"]?.DeepClone() as JsonObject ?? new();
        _optionReaders.Clear(); _optionControls.Clear(); _optionFields.Clear(); _toolOptions.Children.Clear(); _toolOptions.Spacing = 8;
        var form = feature?["form"];
        if (form is null) { UpdateRegion(); return; }
        if (_activeFeature == "auto-subtitle") { BuildSubtitleOptions(form); return; }
        if (form.Text("hint") is { Length: > 0 } hint)
        {
            var hintText = Label(hint); hintText.FontSize = 12.5;
            _toolOptions.Children.Add(new Border { Child = hintText, Background = Palette.Fill, CornerRadius = new(9), Padding = new(11, 8) });
        }
        AddMediaDetails(form);
        AddVideoBatchControls();
        if (form.Flag("preview") || HasEditor)
        {
            _openEditor = Action("打开视频编辑 / 预览", () => { _workspaceTabs.SelectedIndex = 1; return _activeFeature == "join" ? Task.CompletedTask : RefreshPreview(); }, "film");
            _openEditor.IsVisible = _workspaceTabs.SelectedIndex != 1; _toolOptions.Children.Add(_openEditor);
        }
        foreach (var group in form["fields"].Items().GroupBy(field => field.Text("group")))
        {
            var groupPanel = new StackPanel { Spacing = 4 };
            foreach (var field in group)
            {
                var panel = new StackPanel { Spacing = 4 }; groupPanel.Children.Add(panel);
                var path = field.Text("path"); var value = ReadOption(_optionDraft, path); Control control;
                switch (field.Text("kind"))
                {
                    case "video-track": case "audio-track": case "subtitle-track":
                        var track = BuildTrackChoice(field.Text("kind").Split('-')[0], value);
                        _optionReaders[path] = () => (track.SelectedItem as JsonNode)?["value"]?.DeepClone(); control = track;
                        track.SelectionChanged += (_, _) => { UpdateOptionVisibility(); UpdateRegion(); ScheduleParameterPreview(); };
                        Field(panel, field.Text("label"), control); break;
                    case "boolean":
                        var check = new CheckBox { Content = field.Text("label"), IsChecked = value?.GetValue<bool>() == true, Margin = new(0, 4) };
                        check.IsCheckedChanged += (_, _) => { UpdateOptionVisibility(); UpdateRegion(); };
                        _optionReaders[path] = () => JsonValue.Create(check.IsChecked == true); control = check; panel.Children.Add(control); break;
                    case "number":
                        var number = new VisualNumber { Minimum = (decimal)(field["minimum"]?.GetValue<double>() ?? 0),
                            Maximum = (decimal)(field["maximum"]?.GetValue<double>() ?? 1000000), Increment = (decimal)(field["step"]?.GetValue<double>() ?? 1),
                            Value = (decimal)(value?.GetValue<double>() ?? 0), HorizontalAlignment = HorizontalAlignment.Stretch };
                        _optionReaders[path] = () => JsonValue.Create((double)(number.Value ?? 0)); control = number;
                        number.ValueChanged += (_, _) => { UpdateOptionVisibility(); UpdateRegion(); }; Field(panel, field.Text("label"), control); break;
                    case "choice":
                        var choices = field["choices"].Items().ToArray();
                        var choice = new ComboBox { ItemsSource = choices, HorizontalAlignment = HorizontalAlignment.Stretch,
                            ItemTemplate = new FuncDataTemplate<JsonNode>((item, _) => Label(item.Text("label"))) };
                        choice.SelectedItem = choices.FirstOrDefault(item => JsonNode.DeepEquals(item["value"], value)) ?? choices.FirstOrDefault();
                        choice.SelectionChanged += (_, _) => { UpdateOptionVisibility(); UpdateRegion(); };
                        _optionReaders[path] = () => (choice.SelectedItem as JsonNode)?["value"]?.DeepClone(); control = choice;
                        Field(panel, field.Text("label"), control); break;
                    case "file":
                        var file = new FilePathChoice { Text = value?.ToString() ?? "" };
                        _optionReaders[path] = () => JsonValue.Create(file.Text ?? ""); control = file;
                        Field(panel, field.Text("label"), control);
                        panel.Children.Add(Actions(Action("选择文件…", () => PickServerFile(field.Text("label"), false, selected => file.Text = selected)),
                            Action("清除", () => { file.Text = ""; return Task.CompletedTask; })));
                        break;
                    default: throw new InvalidOperationException("服务器返回了不支持的参数控件。");
                }
                _optionControls[path] = control; _optionFields[path] = (panel, field);
            }
            _toolOptions.Children.Add(Section(group.Key, groupPanel, _toolOptions.Children.OfType<OptionSection>().Count() == 0));
        }
        UpdateOptionVisibility();
        if (_previewInfo is not null) ShowMediaInfo(_previewInfo);
        UpdateRegion();
    }
    private static OptionSection Section(string title, StackPanel body, bool expanded) => new(title, body, expanded);
    private void UpdateOptionVisibility()
    {
        _compressionPlan.Text = "";
        InvalidateVideoBatch();
        if (_activeFeature == "contact-sheet") ScheduleParameterPreview();
        foreach (var (_, (container, field)) in _optionFields)
            container.IsVisible = field["visibleWhen"].Items().All(condition =>
            {
                var dependency = condition.Text("path");
                var value = _optionReaders.TryGetValue(dependency, out var read) ? read() : ReadOption(_optionDraft, dependency);
                return condition["values"].Items().Any(expected => JsonNode.DeepEquals(expected, value));
            });
        foreach (var section in _toolOptions.Children.OfType<OptionSection>())
            section.IsVisible = section.Body.Children.Any(child => child.IsVisible);
    }
    private Control? _openEditor;
    private void SaveOptions()
    {
        foreach (var (path, read) in _optionReaders) SetOption(_optionDraft, path, read());
    }
    private JsonObject ReadOptions()
    {
        SaveOptions();
        var options = (JsonObject)_optionDraft.DeepClone();
        if (_activeFeature == "clip") options["copyStreams"] = _clipPreset.SelectedIndex == 3;
        if (_activeFeature is "video-compress" or "video-slim")
            options["format"] = options[_activeFeature == "video-compress" ? "videoCompression" : "videoSlimming"]?["format"]?.DeepClone();
        if (_activeFeature == "delogo") ApplyDelogoRegions(options);
        else if (_activeFeature == "crop")
        {
            var prefix = "crop";
            if ((ReadOption(options, prefix + "Width")?.GetValue<double>() ?? 0) <= 0 || (ReadOption(options, prefix + "Height")?.GetValue<double>() ?? 0) <= 0)
                throw new ArgumentException("请先在预览中框选区域，或拖动区域滑块。");
        }
        if (_activeFeature == "auto-subtitle" && options["transcription"].Flag("recognitionOnly")) options["format"] = "srt";
        var start = ReadOption(options, "start")?.GetValue<double>() ?? 0;
        var end = ReadOption(options, "end")?.GetValue<double>() ?? 0;
        if (end > 0 && end <= start) throw new ArgumentException("请将结束点移到开始点之后。");
        if (options.Flag("copyStreams") && (_activeFeature == "clip") &&
            (GetNumber("cropWidth") > 0 || GetNumber("speed") != 1 || GetNumber("fadeIn") > 0 || GetNumber("fadeOut") > 0
                || (_optionControls.GetValueOrDefault("rotation") as ComboBox)?.SelectedIndex > 0
                || (_optionControls.GetValueOrDefault("flip") as CheckBox)?.IsChecked == true))
            throw new ArgumentException("流复制不能组合画面编辑，请关闭流复制。");
        if (_activeFeature == "mux" && (IncludedInputs.Count() != 2 || IncludedInputs.Any(item => item.Value.IsFolder)))
            throw new ArgumentException("混流请按顺序添加视频、音频两个文件。");
        return options;
    }
    private static JsonNode? ReadOption(JsonObject options, string path)
    {
        JsonNode? current = options;
        foreach (var part in path.Split('.')) current = current?[part];
        return current;
    }
    private static void SetOption(JsonObject options, string path, JsonNode? value)
    {
        var parts = path.Split('.'); var current = options;
        foreach (var part in parts[..^1])
        {
            if (current[part] is not JsonObject child) current[part] = child = new();
            current = child;
        }
        current[parts[^1]] = value;
    }
    private void MoveInput(string path, int direction)
    {
        var items = _inputs.ToList(); var index = items.FindIndex(item => item.Key == path); var next = index + direction;
        if (index < 0 || next < 0 || next >= items.Count) return;
        (items[index], items[next]) = (items[next], items[index]); _inputs.Clear();
        foreach (var item in items) _inputs.Add(item.Key, item.Value);
        RenderInputs();
    }
    private void MoveInputTo(string path, int target)
    {
        var items = _inputs.ToList(); var index = items.FindIndex(item => item.Key == path);
        if (index < 0) return; target = Math.Clamp(target, 0, items.Count - 1); if (target == index) return;
        var moved = items[index]; items.RemoveAt(index); items.Insert(target, moved); _inputs.Clear();
        foreach (var item in items) _inputs.Add(item.Key, item.Value);
        RenderInputs();
    }

    private TabItem? _fileChoiceTab;
    private FileBrowserView? _fileChoice;
    private async Task PickServerFile(string title, bool folders, Action<string> selected)
    {
        if (_fileChoiceTab is not null) _workspaceTabs.Items.Remove(_fileChoiceTab);
        _fileChoice?.Dispose();
        var previous = _workspaceTabs.Items[0];
        var picker = _fileChoice = new FileBrowserView(_source, new(title, Folders: folders, StartFolder: _files.CurrentFolder, Embedded: true));
        void Close()
        {
            if (_fileChoiceTab is not null) _workspaceTabs.Items.Remove(_fileChoiceTab);
            _workspaceTabs.SelectedItem = previous; picker.Dispose(); _fileChoice = null; _fileChoiceTab = null;
        }
        picker.Selected += items => { if (items.FirstOrDefault() is { } item) selected(_source.DisplayAddress(item)); Close(); };
        picker.Cancelled += Close;
        var panel = new Grid { RowDefinitions = new("Auto,*") };
        panel.Children.Add(Action("取消选择", () => { Close(); return Task.CompletedTask; }));
        Grid.SetRow(picker, 1); panel.Children.Add(picker);
        _fileChoiceTab = new TabItem { Header = title, Content = panel };
        _workspaceTabs.Items.Add(_fileChoiceTab); _workspaceTabs.SelectedItem = _fileChoiceTab;
        await picker.InitializeAsync();
    }
}

/// <summary>Collapsible option group: "⌄ 标题" header with plain rows below.</summary>
internal sealed class OptionSection : StackPanel
{
    public StackPanel Body { get; }
    public OptionSection(string title, StackPanel body, bool expanded)
    {
        Body = body; Spacing = 4; body.IsVisible = expanded;
        var icon = Ui.Icon(expanded ? "chevron-down" : "chevron-right", 12, Palette.Secondary);
        var text = Ui.Text(title, "section");
        var header = new Button { Content = Ui.Row(5, icon, text), Classes = { "plain" }, Padding = new(0, 4), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        header.Click += (_, _) => { body.IsVisible = !body.IsVisible; icon.Kind = body.IsVisible ? "chevron-down" : "chevron-right"; };
        Children.Add(header); Children.Add(body);
    }
}
