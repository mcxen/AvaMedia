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
    private readonly Dictionary<string, JsonObject> _drafts = new();
    private string _activeFeature = "";
    private JsonObject _optionDraft = new();
    private readonly ComboBox _category = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private JsonNode[] _features = [];

    private void FilterFeatures()
    {
        var id = (_feature.SelectedItem as JsonNode).Text("id");
        var category = _category.SelectedItem as string;
        var visible = _features.Where(item => category == "全部工具" || item.Text("category") == category
            || category == "视频" && item["form"] is not null).ToArray();
        _feature.ItemsSource = visible;
        _feature.SelectedItem = visible.FirstOrDefault(item => item.Text("id") == id) ?? visible.FirstOrDefault();
    }
    private void BuildToolOptions()
    {
        SaveOptions();
        if (_activeFeature.Length > 0) _drafts[_activeFeature] = _optionDraft;
        var feature = _feature.SelectedItem as JsonNode;
        _activeFeature = feature.Text("id");
        _optionDraft = _drafts.GetValueOrDefault(_activeFeature) ?? feature?["form"]?["defaults"]?.DeepClone() as JsonObject ?? new();
        _optionReaders.Clear(); _optionControls.Clear(); _toolOptions.Children.Clear();
        var form = feature?["form"];
        if (form is null) return;
        _toolOptions.Children.Add(Label(form.Text("hint")));
        if (form.Flag("preview")) _toolOptions.Children.Add(Action("打开视频编辑 / 预览", () => { _workspaceTabs.SelectedIndex = 1; return RefreshPreview(); }));
        foreach (var group in form["fields"].Items().GroupBy(field => field.Text("group")))
        {
            var panel = new StackPanel { Spacing = 6 };
            foreach (var field in group)
            {
                var path = field.Text("path"); var value = ReadOption(_optionDraft, path); Control control;
                switch (field.Text("kind"))
                {
                    case "boolean":
                        var check = new CheckBox { Content = field.Text("label"), IsChecked = value?.GetValue<bool>() == true };
                        check.IsCheckedChanged += (_, _) => UpdateRegion();
                        _optionReaders[path] = () => JsonValue.Create(check.IsChecked == true); control = check; panel.Children.Add(control); break;
                    case "number":
                        var number = new VisualNumber { Minimum = (decimal)(field["minimum"]?.GetValue<double>() ?? 0),
                            Maximum = (decimal)(field["maximum"]?.GetValue<double>() ?? 1000000), Increment = (decimal)(field["step"]?.GetValue<double>() ?? 1),
                            Value = (decimal)(value?.GetValue<double>() ?? 0), HorizontalAlignment = HorizontalAlignment.Stretch };
                        _optionReaders[path] = () => JsonValue.Create((double)(number.Value ?? 0)); control = number;
                        number.ValueChanged += (_, _) => UpdateRegion(); Field(panel, field.Text("label"), control); break;
                    case "choice":
                        var choices = field["choices"].Items().ToArray();
                        var choice = new ComboBox { ItemsSource = choices, HorizontalAlignment = HorizontalAlignment.Stretch,
                            ItemTemplate = new FuncDataTemplate<JsonNode>((item, _) => Label(item.Text("label"))) };
                        choice.SelectedItem = choices.FirstOrDefault(item => JsonNode.DeepEquals(item["value"], value)) ?? choices.FirstOrDefault();
                        choice.SelectionChanged += (_, _) => UpdateRegion();
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
                _optionControls[path] = control;
            }
            _toolOptions.Children.Add(new Expander { Header = group.Key, Content = panel, IsExpanded = _toolOptions.Children.OfType<Expander>().Count() == 0,
                HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        if (_previewInfo is not null) ShowMediaInfo(_previewInfo);
        UpdateRegion();
    }
    private void SaveOptions()
    {
        foreach (var (path, read) in _optionReaders) SetOption(_optionDraft, path, read());
    }
    private JsonObject ReadOptions()
    {
        SaveOptions();
        var options = (JsonObject)_optionDraft.DeepClone();
        if (_activeFeature is "video-compress" or "video-slim")
            options["format"] = options[_activeFeature == "video-compress" ? "videoCompression" : "videoSlimming"]?["format"]?.DeepClone();
        if (_activeFeature is "crop" or "delogo")
        {
            var prefix = _activeFeature == "crop" ? "crop" : "delogo";
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
        if (_activeFeature == "mux" && (_inputs.Count != 2 || _inputs.Values.Any(item => item.IsFolder)))
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
