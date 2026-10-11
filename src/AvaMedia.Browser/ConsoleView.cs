using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaMedia.FileUi;

namespace AvaMedia.Browser;

public sealed partial class ConsoleView : UserControl
{
    private readonly RemoteFiles _source = new();
    private readonly FileBrowserView _files;
    private readonly TabControl _tabs = new();
    private readonly StackPanel _form = new() { Spacing = 9, Margin = new(12) };
    private readonly ComboBox _feature = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly FilePathChoice _output = new();
    private readonly TextBox _urls = new() { AcceptsReturn = true, MinHeight = 60 };
    private readonly VisualNumber _frames = new() { Minimum = 1, Maximum = 120, Value = 8 },
        _threshold = new() { Minimum = 0, Maximum = 1, Increment = .05m, Value = .5m };
    private readonly CheckBox _recursive = new() { Content = "包括子文件夹", IsChecked = true },
        _start = new() { Content = "立即执行", IsChecked = true }, _nsfw = new() { Content = "NSFW 内容识别" },
        _scenes = new() { Content = "场景识别" }, _downloadModels = new() { Content = "允许下载语义模型" };
    private readonly StackPanel _analysis = new() { Spacing = 6 }, _rules = new() { Spacing = 4 }, _selected = new() { Spacing = 3 };
    private readonly TextBlock _status = Label("正在连接…"), _selection = Label("尚未添加文件");
    private readonly Dictionary<string, FileBrowserItem> _inputs = new(StringComparer.Ordinal);
    private readonly Button _submit, _recover, _discard;
    private JsonObject? _pending;
    private JsonNode? _runtime;
    private bool _connected, _submitting;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };

    public ConsoleView()
    {
        _files = new(_source, new("服务器文件", Multiple: true, AllowFolders: true, Embedded: true));
        _files.Selected += AddInputs;
        _feature.ItemTemplate = new FuncDataTemplate<JsonNode>((item, _) => Label(item.Text("name")));
        _feature.SelectionChanged += (_, _) => UpdateFeature();
        _category.SelectionChanged += (_, _) => FilterFeatures();
        Field(_form, "工具分类", _category);
        Field(_form, "任务类型", _feature); Field(_form, "下载链接（每行一个）", _urls);
        _form.Children.Add(_selection);
        _form.Children.Add(new ScrollViewer { Content = _selected, MaxHeight = 110 });
        _form.Children.Add(Actions(Action("添加当前文件夹", () => { if (_files.CurrentFolder is { } folder) AddInputs([folder]); return Task.CompletedTask; }),
                Action("清空选择", () => { _inputs.Clear(); _excludedInputs.Clear(); _rotationOverrides.Clear(); RenderInputs(); return Task.CompletedTask; })));
        _form.Children.Add(_recursive); Field(_form, "服务器输出目录", _output);
        _form.Children.Add(Action("选择输出文件夹…", () => PickServerFile("输出目录", true, selected => _output.Text = selected)));
        _form.Children.Add(Action("使用当前目录输出", () => { if (_files.CurrentFolder is { } folder) _output.Text = _source.DisplayAddress(folder); return Task.CompletedTask; }));
        Field(_analysis, "视频采样帧数", _frames); Field(_analysis, "标签阈值", _threshold);
        _analysis.Children.Add(_nsfw); _analysis.Children.Add(_scenes); _form.Children.Add(_analysis);
        _form.Children.Add(new Expander { Header = "分类规则", Content = new ScrollViewer { Content = _rules, MaxHeight = 220 }, IsExpanded = true, Name = "ClassificationRules" });
        _form.Children.Add(_downloadModels);
        _form.Children.Add(_toolOptions);
        _form.Children.Add(_start);
        _submit = Action("创建任务", Submit); _form.Children.Add(_submit);
        var work = new Grid { ColumnDefinitions = new("*,370") };
        _workspaceTabs.Items.Add(new TabItem { Header = "服务器文件", Content = _files });
        _workspaceTabs.Items.Add(new TabItem { Header = "视频编辑 / 预览", Content = BuildEditor() });
        work.Children.Add(_workspaceTabs); var formScroll = new ScrollViewer { Content = _form, Tag = "*,370" }; Grid.SetColumn(formScroll, 1); work.Children.Add(formScroll);
        AdaptColumns(work, formScroll);
        _tabs.Items.Add(new TabItem { Header = "文件与任务下发", Content = work });
        _tabs.Items.Add(new TabItem { Header = "所有任务", Content = BuildTasks() });
        _tabs.Items.Add(new TabItem { Header = "服务器", Content = BuildServer() });
        var root = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new(10), RowSpacing = 8 };
        root.Children.Add(Actions(new TextBlock { Text = "AvaMedia 远程管理", FontSize = 22, FontWeight = FontWeight.SemiBold }, Action("重新连接", Connect)));
        Grid.SetRow(_tabs, 1); root.Children.Add(_tabs);
        _recover = Action("恢复上次提交", Submit); _discard = Action("放弃恢复记录", async () =>
        {
            BrowserHost.WritePending(""); _pending = null; UpdatePending(); await RefreshTasks();
            _status.Text = "已清除恢复记录；服务器已接收的任务仍保留在所有任务中。";
        });
        var footer = new StackPanel { Spacing = 4 }; footer.Children.Add(_status); footer.Children.Add(Actions(_recover, _discard));
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        _poll.Tick += async (_, _) => { if (_connected) await Guard(RefreshTasks); };
        AttachedToVisualTree += async (_, _) =>
        {
            try { var saved = BrowserHost.ReadPending(); if (saved.Length > 0) _pending = JsonNode.Parse(saved) as JsonObject; }
            catch (Exception error) { _status.Text = "恢复记录无法读取：" + error.Message; }
            UpdatePending(); await Guard(Connect); _poll.Start();
        };
        DetachedFromVisualTree += (_, _) => { _poll.Stop(); ClearPreview(); };
    }

    private async Task Connect()
    {
        _status.Text = "正在连接服务器…";
        var capabilities = await RemoteApi.Call("capabilities"); _runtime = await RemoteApi.Call("get_runtime");
        var selectedId = (_feature.SelectedItem as JsonNode).Text("id");
        _features = capabilities["features"].Items().Where(item => item.Flag("canCreateTask") || item.Text("id") is "player" or "info").ToArray();
        var category = _category.SelectedItem as string ?? "视频";
        _category.ItemsSource = new[] { "全部工具" }.Concat(_features.Select(item => item.Text("category")).Distinct()).ToArray();
        _category.SelectedItem = category;
        FilterFeatures();
        if (_feature.ItemsSource is JsonNode[] visible && visible.FirstOrDefault(item => item.Text("id") == selectedId) is { } selected) _feature.SelectedItem = selected;
        if (string.IsNullOrWhiteSpace(_output.Text)) _output.Text = _runtime.Text("outputFolder");
        _nsfw.IsVisible = _runtime.Flag("nsfwEnabled"); if (!_nsfw.IsVisible) _nsfw.IsChecked = false;
        _rules.Children.Clear();
        foreach (var rule in capabilities["classificationPresets"].Items())
            _rules.Children.Add(new CheckBox { Content = rule.Text("name"), Tag = rule, IsChecked = rule.Text("id") == "scenery" });
        RenderServer(); await _files.InitializeAsync(); _connected = true; UpdatePending();
        await RefreshTasks(); _status.Text = "已连接 · " + _runtime.Text("platform") + " · v" + _runtime.Text("version");
    }
    private void AddInputs(IReadOnlyList<FileBrowserItem> items)
    {
        if (_pending is not null) return;
        foreach (var item in items) _inputs[_source.DisplayAddress(item)] = item;
        RenderInputs();
    }
    private void RenderInputs()
    {
        InvalidateVideoBatch();
        _selected.Children.Clear(); _selection.Text = $"勾选 {IncludedInputs.Count()} / {_inputs.Count} 项";
        foreach (var (path, item) in _inputs)
        {
            var name = Label((item.IsFolder ? "文件夹 · " : "") + item.Name);
            name.MaxWidth = 145; name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
            var include = new CheckBox { Content = name, IsChecked = !_excludedInputs.Contains(path) };
            include.IsCheckedChanged += (_, _) =>
            {
                if (include.IsChecked == true) _excludedInputs.Remove(path); else _excludedInputs.Add(path);
                _selection.Text = $"勾选 {IncludedInputs.Count()} / {_inputs.Count} 项";
                InvalidateVideoBatch(); BuildToolOptions();
            };
            var row = Actions(include,
                Action("↑", () => { MoveInput(path, -1); return Task.CompletedTask; }),
                Action("↓", () => { MoveInput(path, 1); return Task.CompletedTask; }),
                Action("移除", () => { _inputs.Remove(path); _excludedInputs.Remove(path); _rotationOverrides.Remove(path); RenderInputs(); return Task.CompletedTask; }));
            if (!item.IsFolder) row.Children.Add(Action("预览", () =>
            { _workspaceTabs.SelectedIndex = 1; _previewInput.SelectedItem = item; return RefreshPreview(); }));
            ToolTip.SetTip(row, path); _selected.Children.Add(row);
        }
        var previous = _previewInput.SelectedItem;
        var previewItems = _inputs.Values.Where(item => !item.IsFolder).ToArray();
        _updatingPreviewInputs = true;
        try
        {
            _previewInput.ItemsSource = previewItems;
            _previewInput.SelectedItem = previewItems.Contains(previous) ? previous : previewItems.FirstOrDefault();
        }
        finally { _updatingPreviewInputs = false; }
        if (!Equals(previous, _previewInput.SelectedItem)) ClearPreview();
        if (_activeFeature.Length > 0) BuildToolOptions();
    }
    private void UpdateFeature()
    {
        var id = (_feature.SelectedItem as JsonNode).Text("id");
        _analysis.IsVisible = id is "media-ai" or "folder-classification";
        foreach (var expander in _form.Children.OfType<Expander>().Where(control => control.Name == "ClassificationRules")) expander.IsVisible = id == "folder-classification";
        _downloadModels.IsVisible = id == "folder-classification";
        _urls.IsVisible = id == "download";
        BuildToolOptions();
        if (_submit is not null) _submit.IsVisible = id is not ("player" or "info");
        _start.IsVisible = id is not ("player" or "info");
    }
    private JsonObject Submission()
    {
        var id = (_feature.SelectedItem as JsonNode).Text("id");
        if (id.Length == 0) throw new InvalidOperationException("请选择任务类型。");
        var paths = id == "download" ? (_urls.Text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : IncludedInputs.Select(pair => pair.Key).ToArray();
        if (paths.Length == 0) throw new InvalidOperationException("请先添加文件或文件夹。");
        var options = ReadOptions();
        if (id is "media-ai" or "folder-classification")
        {
            var specKey = id == "media-ai" ? "mediaTag" : "folderClassification";
            var spec = options[specKey] as JsonObject ?? new JsonObject();
            var analysis = spec["analysis"] as JsonObject ?? new JsonObject();
            analysis["videoFrames"] = (int)(_frames.Value ?? 8);
            analysis["recognizeNsfw"] = _nsfw.IsChecked == true; analysis["recognizeScenes"] = _scenes.IsChecked == true;
            if (spec["analysis"] is null) spec["analysis"] = analysis;
            if (id == "media-ai") spec["threshold"] = (double)(_threshold.Value ?? .5m);
            else
            {
                var rules = _rules.Children.OfType<CheckBox>().Where(box => box.IsChecked == true).Select(box => ((JsonNode)box.Tag!).DeepClone()).ToArray();
                if (rules.Length == 0) throw new InvalidOperationException("请至少选择一个分类规则。");
                spec["rules"] = new JsonArray(rules); spec["tagThreshold"] = (double)(_threshold.Value ?? .5m);
                spec["includeNsfw"] = _nsfw.IsChecked == true; spec["allowSemanticDownload"] = _downloadModels.IsChecked == true;
                spec["outputFolder"] = _output.Text?.Trim() ?? ""; spec["move"] = false;
            }
            if (options[specKey] is null) options[specKey] = spec;
        }
        return new() { ["requestId"] = Guid.NewGuid().ToString(), ["featureId"] = id, ["paths"] = new JsonArray(paths.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray()),
            ["outputFolder"] = _output.Text?.Trim(), ["options"] = options, ["recursive"] = _recursive.IsChecked == true, ["startImmediately"] = _start.IsChecked == true };
    }
    private async Task Submit()
    {
        if (!_connected) throw new InvalidOperationException("请先连接服务器。");
        if (_submitting) return;
        _submitting = true;
        try
        {
            if (_pending is null)
            {
                var draft = new JsonObject { ["method"] = _activeFeature switch { "clip" => "export_clips", "crop" or "rotate" => "export_video_batch", _ => "create_task" },
                    ["arguments"] = _activeFeature switch { "clip" => ClipSubmission(), "crop" or "rotate" => VideoBatchSubmission(), _ => Submission() } };
                BrowserHost.WritePending(draft.ToJsonString()); _pending = draft;
            }
            UpdatePending();
            var result = await RemoteApi.Call(_pending.Text("method"), _pending["arguments"] as JsonObject ?? throw new InvalidOperationException("恢复记录格式无效。"));
            BrowserHost.WritePending(""); _pending = null; UpdatePending();
            _status.Text = $"已创建 {result["tasks"].Items().Count()} 项任务";
            _inputs.Clear(); _clips.Clear(); _excludedInputs.Clear(); _rotationOverrides.Clear(); RenderInputs(); _tabs.SelectedIndex = 1;
            _state.SelectedIndex = 0;
            var page = await RemoteApi.Call("list_tasks", new() { ["limit"] = 1 });
            _taskOffset = Math.Max(0, ((page["total"]?.GetValue<int>() ?? 1) - 1) / 30 * 30);
            await RefreshTasks();
        }
        finally { _submitting = false; UpdatePending(); }
    }
    private void UpdatePending()
    {
        _form.IsEnabled = _pending is null && _connected && !_submitting; _files.IsEnabled = _pending is null && !_submitting;
        _recover.IsEnabled = _discard.IsEnabled = !_submitting;
        _recover.IsVisible = _discard.IsVisible = _pending is not null;
    }
    private async Task Guard(Func<Task> action)
    { try { await action(); } catch (Exception error) { _status.Text = error.Message; } }
    private Button Action(string name, Func<Task> action)
    {
        var button = new Button { Content = name }; AutomationProperties.SetName(button, name);
        button.Click += async (_, _) => { button.IsEnabled = false; try { await Guard(action); } finally { button.IsEnabled = true; if (button == _submit || button == _recover || button == _discard) UpdatePending(); } }; return button;
    }
    private static void AdaptColumns(Grid grid, Control second)
    {
        bool? stacked = null;
        grid.PropertyChanged += (_, args) =>
        {
            if (args.Property != BoundsProperty || grid.Bounds.Width <= 0) return;
            var narrow = grid.Bounds.Width < 900;
            if (stacked == narrow) return;
            stacked = narrow;
            grid.ColumnDefinitions = new(narrow ? "*" : second.Tag as string ?? "*,*");
            grid.RowDefinitions = new(narrow ? "*,*" : "*");
            Grid.SetColumn(second, narrow ? 0 : 1); Grid.SetRow(second, narrow ? 1 : 0);
        };
    }
    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static StackPanel Actions(params Control[] controls)
    { var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; foreach (var control in controls) panel.Children.Add(control); return panel; }
    private static void Field(Panel panel, string name, Control control)
    {
        var label = Label(name); label.IsVisible = control.IsVisible; panel.Children.Add(label); panel.Children.Add(control);
        control.PropertyChanged += (_, args) => { if (args.Property == IsVisibleProperty) label.IsVisible = control.IsVisible; };
        AutomationProperties.SetName(control, name);
    }
}
