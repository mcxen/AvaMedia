using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaMedia.FileUi;

namespace AvaMedia.Browser;

public sealed partial class ConsoleView : UserControl
{
    private readonly RemoteFiles _source = new();
    private readonly FileBrowserView _files;
    /// <summary>Pages: 0 create, 1 tasks, 2 server. The strip is hidden; navigation is the segmented control / tab bar.</summary>
    private readonly TabControl _tabs = new();
    private readonly StackPanel _form = new() { Spacing = 10 };
    private readonly ComboBox _feature = new() { IsVisible = false };
    private readonly FilePathChoice _output = new();
    private readonly TextBox _urls = new() { AcceptsReturn = true, MinHeight = 60, TextWrapping = TextWrapping.Wrap };
    private readonly VisualNumber _frames = new() { Minimum = 1, Maximum = 120, Value = 8 },
        _threshold = new() { Minimum = 0, Maximum = 1, Increment = .05m, Value = .5m };
    private readonly CheckBox _recursive = new() { Content = "包括子文件夹", IsChecked = true }, _nsfw = new() { Content = "NSFW 内容识别" },
        _scenes = new() { Content = "场景识别" }, _downloadModels = new() { Content = "允许下载语义模型" };
    private readonly ToggleSwitch _start = new() { Content = "立即执行", IsChecked = true };
    private readonly StackPanel _analysis = new() { Spacing = 6 }, _rules = new() { Spacing = 6 }, _selected = new();
    private readonly TextBlock _status = new() { IsVisible = false }, _selection = Ui.Text("", "secondary");
    private readonly Dictionary<string, FileBrowserItem> _inputs = new(StringComparer.Ordinal);
    private readonly Button _submit, _recover, _discard;
    private JsonObject? _pending;
    private JsonNode? _runtime;
    private bool _connected, _submitting, _mobile;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };

    // Shell
    private readonly Grid _root = new() { RowDefinitions = new("Auto,*,Auto") };
    private readonly Border _connection = new();
    private readonly StackPanel _navigation = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly Grid _tabBar = new() { ColumnDefinitions = new("*,*,*"), Height = 64 };
    private readonly Border _toast = new() { IsVisible = false };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly Button _fab;
    private Grid _header = null!;
    private Control _desktopHeaderTitle = null!, _reconnect = null!, _desktopNavigation = null!;

    public ConsoleView()
    {
        _files = new(_source, new("服务器文件", Multiple: true, AllowFolders: true, Embedded: true, Compact: true), L.T, new Glyphs());
        _files.Selected += AddInputs;
        _files.FooterActions.Children.Add(Action("添加当前文件夹", () => { if (_files.CurrentFolder is { } folder) AddInputs([folder]); return Task.CompletedTask; }));
        _feature.ItemTemplate = new FuncDataTemplate<JsonNode>((item, _) => Label(item.Text("name")));
        _feature.SelectionChanged += (_, _) => UpdateFeature();
        Field(_analysis, "视频采样帧数", _frames); Field(_analysis, "标签阈值", _threshold);
        _analysis.Children.Add(_nsfw); _analysis.Children.Add(_scenes);
        _submit = Action("创建任务", Submit, "circle-play", primary: true); _submit.Classes.Add("large");
        _recover = Action("恢复上次提交", Submit, primary: true);
        _discard = Action("放弃恢复记录", async () =>
        {
            BrowserHost.WritePending(""); _pending = null; UpdatePending(); await RefreshTasks();
            _status.Text = "已清除恢复记录；服务器已接收的任务仍保留在所有任务中。";
        });
        BuildForm();
        _workspaceTabs.Items.Add(new TabItem { Header = "服务器文件", Content = BuildCreateColumns() });
        _workspaceTabs.Items.Add(new TabItem { Header = "视频编辑 / 预览", Content = BuildEditor() });
        Bare(_workspaceTabs); Bare(_tabs);
        _tabs.Items.Add(new TabItem { Header = "文件与任务下发", Content = BuildCreatePage() });
        _tabs.Items.Add(new TabItem { Header = "所有任务", Content = BuildTasks() });
        _tabs.Items.Add(new TabItem { Header = "服务器", Content = BuildServer() });
        _tabs.SelectedIndex = 1;
        _tabs.SelectionChanged += (_, e) => { if (e.Source == _tabs) { RenderNavigation(); UpdateLayoutMode(); } };
        _workspaceTabs.SelectionChanged += (_, e) => { if (e.Source == _workspaceTabs) PlaceForm(); };

        _fab = Action("创建任务", () => { _tabs.SelectedIndex = 0; return Task.CompletedTask; }, "plus", primary: true);
        _fab.Classes.Add("large"); _fab.HorizontalAlignment = HorizontalAlignment.Right; _fab.VerticalAlignment = VerticalAlignment.Bottom;
        _fab.Margin = new(0, 0, 18, 18);
        Background = Palette.Page;
        _root.Children.Add(BuildHeader());
        var body = new Grid(); body.Children.Add(_tabs); body.Children.Add(_fab); body.Children.Add(BuildToast());
        Grid.SetRow(body, 1); _root.Children.Add(body);
        BuildTabBar(); Grid.SetRow(_tabBar, 2); _root.Children.Add(_tabBar);
        Content = _root;
        _status.PropertyChanged += (_, args) => { if (args.Property == TextBlock.TextProperty) ShowToast(_status.Text ?? ""); };
        _poll.Tick += async (_, _) => { if (_connected) await Guard(RefreshTasks); };
        PropertyChanged += (_, args) => { if (args.Property == BoundsProperty) UpdateLayoutMode(); };
        AttachedToVisualTree += async (_, _) =>
        {
            try { var saved = BrowserHost.ReadPending(); if (saved.Length > 0) _pending = JsonNode.Parse(saved) as JsonObject; }
            catch (Exception error) { _status.Text = "恢复记录无法读取：" + error.Message; }
            UpdatePending(); await Guard(Connect); _poll.Start();
        };
        DetachedFromVisualTree += (_, _) => { _poll.Stop(); ClearPreview(); };
        RenderNavigation(); SetConnection(null);
    }

    /// <summary>Hides a TabControl's strip; pages are switched by our own navigation.</summary>
    private static void Bare(TabControl tabs)
    {
        tabs.Padding = new(0); tabs.Background = Brushes.Transparent;
        tabs.Template = new FuncControlTemplate<TabControl>((control, scope) =>
        {
            var grid = new Grid();
            var items = new ItemsPresenter { Name = "PART_ItemsPresenter", Height = 0, ClipToBounds = true, IsHitTestVisible = false }.RegisterInNameScope(scope);
            var content = new ContentPresenter { Name = "PART_SelectedContentHost" }.RegisterInNameScope(scope);
            content.Bind(ContentPresenter.ContentProperty, control.GetObservable(TabControl.SelectedContentProperty));
            grid.Children.Add(items); grid.Children.Add(content); return grid;
        });
    }

    // ---------- Header / navigation ----------
    private Control BuildHeader()
    {
        _header = new Grid { ColumnDefinitions = new("*,Auto,*"), Margin = new(18, 10, 18, 10), MinHeight = 40 };
        var logo = new Border { Width = 28, Height = 28, CornerRadius = new(7), Child = Ui.Icon("video", 16, Brushes.White),
            Background = new LinearGradientBrush { StartPoint = new(0, 0, RelativeUnit.Relative), EndPoint = new(0, 1, RelativeUnit.Relative),
                GradientStops = { new(Color.Parse("#5AC8FA"), 0), new(Color.Parse("#007AFF"), 1) } } };
        var title = Ui.Text("AvaMedia 远程管理", "strong"); title.FontSize = 15;
        _desktopHeaderTitle = Ui.Row(12, logo, title);
        var left = Ui.Row(12, _desktopHeaderTitle, _connection); _header.Children.Add(left);
        var nav = new Border { Background = Brushes.White, CornerRadius = new(999), Padding = new(3), Child = _navigation,
            BoxShadow = BoxShadows.Parse("0 1 2 0 #12000000, 0 0 0 1 #0F000000") };
        _desktopNavigation = nav; Grid.SetColumn(nav, 1); _header.Children.Add(nav);
        var reconnect = Action("重新连接", Connect, "rotate-ccw"); reconnect.Classes.Add("outline");
        reconnect.HorizontalAlignment = HorizontalAlignment.Right; _reconnect = reconnect;
        Grid.SetColumn(reconnect, 2); _header.Children.Add(reconnect);
        return _header;
    }
    private static readonly (string Label, string Icon, int Page)[] Pages = [("所有任务", "list", 1), ("文件与任务下发", "plus", 0), ("服务器", "cpu", 2)];
    private void RenderNavigation()
    {
        _navigation.Children.Clear();
        foreach (var (label, icon, page) in Pages)
        {
            var button = new Button { Content = Ui.Content(L.T(label), icon, Palette.Text, 13), Classes = { "segment" } };
            if (_tabs.SelectedIndex == page) button.Classes.Add("active");
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => _tabs.SelectedIndex = page;
            _navigation.Children.Add(button);
        }
        _tabBar.Children.Clear();
        var index = 0;
        foreach (var (label, icon, page) in new[] { ("所有任务", "list", 1), ("任务下发", "plus", 0), ("服务器", "cpu", 2) })
        {
            var active = _tabs.SelectedIndex == page; var brush = active ? Palette.Blue : Palette.Secondary;
            var stack = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(Ui.Icon(icon, 22, brush)); var text = Ui.Text(label); text.FontSize = 11; text.Foreground = brush;
            text.HorizontalAlignment = HorizontalAlignment.Center; stack.Children.Add(text);
            var button = new Button { Content = stack, Classes = { "plain" }, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, CornerRadius = new(0) };
            button.Click += (_, _) => _tabs.SelectedIndex = page; AutomationProperties.SetName(button, label);
            Grid.SetColumn(button, index++); _tabBar.Children.Add(button);
        }
    }
    private void BuildTabBar()
    {
        _tabBar.Background = new SolidColorBrush(Color.FromArgb(245, 249, 249, 251));
        _tabBar.Children.Add(new Border { BorderBrush = Palette.Hairline, BorderThickness = new(0, 1, 0, 0), IsHitTestVisible = false });
    }
    private void SetConnection(string? address)
    {
        _connection.Child = address is null
            ? Ui.Pill(Ui.Dot(Palette.Tertiary, L.T("正在连接…"), Palette.Secondary), Palette.Fill)
            : Ui.Pill(Ui.Dot(Palette.Green, (_mobile ? L.T("已连接") : L.T("已连接") + " · " + address), Palette.GreenText), Palette.GreenSoft);
    }
    private Control BuildToast()
    {
        _toast.Background = Palette.Dark; _toast.CornerRadius = new(999); _toast.Padding = new(16, 8);
        _toast.HorizontalAlignment = HorizontalAlignment.Center; _toast.VerticalAlignment = VerticalAlignment.Bottom; _toast.Margin = new(16, 0, 16, 96);
        _toast.MaxWidth = 640; _toast.IsHitTestVisible = false;
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast.IsVisible = false; };
        return _toast;
    }
    private void ShowToast(string text)
    {
        if (text.Length == 0) return;
        var label = Ui.Text(L.T(text)); label.Foreground = Brushes.White; label.FontSize = 13; label.TextWrapping = TextWrapping.Wrap;
        _toast.Child = label; _toast.IsVisible = true; _toastTimer.Stop(); _toastTimer.Start();
    }

    private void UpdateLayoutMode()
    {
        if (Bounds.Width <= 0) return;
        var mobile = Bounds.Width < 760;
        var changed = mobile != _mobile; _mobile = mobile;
        var subtitleDetail = mobile && _tabs.SelectedIndex == 1 && _subtitlePage is not null;
        _tabBar.IsVisible = mobile && !subtitleDetail; _fab.IsVisible = mobile && _tabs.SelectedIndex == 1 && !_taskDetailOpen;
        _desktopNavigation.IsVisible = !mobile; _desktopHeaderTitle.IsVisible = !mobile;
        _header.Margin = mobile ? new(16, 12, 16, 4) : new(18, 10, 18, 10);
        _header.IsVisible = !(mobile && _tabs.SelectedIndex == 0) && !subtitleDetail;
        if (changed) { SetConnection(_runtime is null ? null : Address()); LayoutCreate(); LayoutTasks(); LayoutEditor(); }
    }
    private static string Address() { var origin = BrowserHost.Origin(); return origin.Replace("http://", "").Replace("https://", ""); }

    // ---------- Connection ----------
    private async Task Connect()
    {
        SetConnection(null);
        var capabilities = await RemoteApi.Call("capabilities"); _runtime = await RemoteApi.Call("get_runtime");
        var selectedId = (_feature.SelectedItem as JsonNode).Text("id");
        _features = capabilities["features"].Items().Where(item => item.Flag("canCreateTask") || item.Text("id") is "player" or "info").ToArray();
        _feature.ItemsSource = _features;
        _feature.SelectedItem = _features.FirstOrDefault(item => item.Text("id") == selectedId) ?? _features.FirstOrDefault(item => item.Text("id") == "video-compress") ?? _features.FirstOrDefault();
        RenderToolTree();
        if (string.IsNullOrWhiteSpace(_output.Text)) _output.Text = _runtime.Text("outputFolder");
        _nsfw.IsVisible = _runtime.Flag("nsfwEnabled"); if (!_nsfw.IsVisible) _nsfw.IsChecked = false;
        _rules.Children.Clear();
        foreach (var rule in capabilities["classificationPresets"].Items())
            _rules.Children.Add(new CheckBox { Content = rule.Text("name"), Tag = rule, IsChecked = rule.Text("id") == "scenery" });
        RenderServer(); await _files.InitializeAsync(); _connected = true; UpdatePending();
        await RefreshTasks(); SetConnection(Address());
    }

    // ---------- Inputs ----------
    private void AddInputs(IReadOnlyList<FileBrowserItem> items)
    {
        if (_pending is not null) return;
        foreach (var item in items) _inputs[_source.DisplayAddress(item)] = item;
        _files.ClearSelection(); RenderInputs();
    }
    private void RenderInputs()
    {
        InvalidateVideoBatch();
        _selected.Children.Clear();
        _selection.Text = L.F("已添加 {0} 项 · 勾选 {1} 项", _inputs.Count, IncludedInputs.Count());
        foreach (var (path, item) in _inputs)
        {
            var include = new CheckBox { IsChecked = !_excludedInputs.Contains(path) };
            include.IsCheckedChanged += (_, _) =>
            {
                if (include.IsChecked == true) _excludedInputs.Remove(path); else _excludedInputs.Add(path);
                _selection.Text = L.F("已添加 {0} 项 · 勾选 {1} 项", _inputs.Count, IncludedInputs.Count());
                InvalidateVideoBatch(); BuildToolOptions(); UpdateSummary();
            };
            var name = Ui.Text(item.Name, raw: true, trim: true); name.FontSize = 12.5;
            var row = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto"), ColumnSpacing = 8, Height = 32, Margin = new(10, 0, 4, 0) };
            row.Children.Add(include);
            var icon = Ui.Icon(Ui.FileIcon(item.Name, item.IsFolder), 14, Palette.Secondary); Grid.SetColumn(icon, 1); row.Children.Add(icon);
            Control title = name;
            if (!item.IsFolder)
            {
                var open = new Button { Content = name, Classes = { "plain" }, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(2, 0) };
                ToolTip.SetTip(open, L.T("在视频编辑 / 预览中打开"));
                open.Click += async (_, _) => { _previewInput.SelectedItem = item; _workspaceTabs.SelectedIndex = 1; await Guard(RefreshPreview); };
                title = open;
            }
            Grid.SetColumn(title, 2); row.Children.Add(title);
            var remove = new Button { Content = Ui.Icon("x", 12, Palette.Secondary), Classes = { "plain", "icon" }, Width = 24, Height = 24 };
            AutomationProperties.SetName(remove, "移除");
            remove.Click += (_, _) => { _inputs.Remove(path); _excludedInputs.Remove(path); _rotationOverrides.Remove(path); RenderInputs(); };
            Grid.SetColumn(remove, 3); row.Children.Add(remove);
            ToolTip.SetTip(row, path);
            if (_selected.Children.Count > 0) _selected.Children.Add(Ui.Hairline(36));
            _selected.Children.Add(row);
        }
        _selectedGroup.IsVisible = _inputs.Count > 0 && _workspaceTabs.SelectedIndex != 1 && !SubtitleSubmission;
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
        RenderFileChips(); RenderMerge(); RenderSubtitleInputs(); UpdateSummary();
    }
    private void UpdateFeature()
    {
        var id = (_feature.SelectedItem as JsonNode).Text("id");
        _analysis.IsVisible = id is "media-ai" or "folder-classification";
        _rulesGroup.IsVisible = id == "folder-classification";
        _downloadModels.IsVisible = id == "folder-classification";
        _urlsGroup.IsVisible = id == "download";
        BuildToolOptions();
        if (_submit is not null) _submit.IsVisible = id is not ("player" or "info");
        _start.IsVisible = id is not ("player" or "info");
        RenderToolSelection(); UpdateEditorChrome(); UpdateSummary(); LayoutCreate();
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
                    ["arguments"] = _activeFeature switch { "clip" => ClipSubmission(), "crop" or "rotate" => VideoBatchSubmission(), _ => Submission() },
                    ["summary"] = SummaryText() };
                BrowserHost.WritePending(draft.ToJsonString()); _pending = draft;
            }
            UpdatePending();
            var result = await RemoteApi.Call(_pending.Text("method"), _pending["arguments"] as JsonObject ?? throw new InvalidOperationException("恢复记录格式无效。"));
            BrowserHost.WritePending(""); _pending = null; UpdatePending();
            _status.Text = L.F("已创建 {0} 项任务", result["tasks"].Items().Count());
            _inputs.Clear(); _clips.Clear(); _excludedInputs.Clear(); _rotationOverrides.Clear(); _delogoRegions.Clear(); RenderInputs();
            _workspaceTabs.SelectedIndex = 0; _createStep = 0; LayoutCreate(); _tabs.SelectedIndex = 1;
            _taskFilter = null; _taskOffset = 0;
            var page = await RemoteApi.Call("list_tasks", new() { ["limit"] = 1 });
            _taskOffset = Math.Max(0, ((page["total"]?.GetValue<int>() ?? 1) - 1) / TaskPageSize * TaskPageSize);
            await RefreshTasks();
            if (result["tasks"].Items().FirstOrDefault(task => task.Text("featureId") == "auto-subtitle") is { } subtitle)
                await OpenSubtitle(subtitle.Text("id"));
        }
        finally { _submitting = false; UpdatePending(); }
    }
    private void UpdatePending()
    {
        _form.IsEnabled = _pending is null && _connected && !_submitting; _files.IsEnabled = _pending is null && !_submitting;
        _toolTree.IsEnabled = _pending is null && !_submitting;
        if (_subtitleSubmit is not null) _subtitleSubmit.IsEnabled = _pending is null && !_submitting && _connected;
        _recover.IsEnabled = _discard.IsEnabled = !_submitting;
        _banner.IsVisible = _pending is not null;
        if (_pending is not null)
            _bannerText.Text = L.F("上次提交未确认送达（{0}）。恢复前表单已锁定。", _pending.Text("summary") is { Length: > 0 } summary ? summary : _pending.Text("method"));
        _submit.IsEnabled = _pending is null && _connected && !_submitting;
    }

    // ---------- Helpers ----------
    private async Task Guard(Func<Task> action)
    { try { await action(); } catch (Exception error) { _status.Text = ""; _status.Text = error.Message; } }
    private Button Action(string name, Func<Task> action, string? icon = null, bool primary = false)
    {
        var button = new Button { Content = Ui.Content(L.T(name), icon, primary ? Brushes.White : Palette.Text, 13) };
        if (primary) button.Classes.Add("primary");
        AutomationProperties.SetName(button, name);
        button.Click += async (_, _) => { button.IsEnabled = false; try { await Guard(action); } finally { button.IsEnabled = true; if (button == _submit || button == _recover || button == _discard) UpdatePending(); } };
        return button;
    }
    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
    private static StackPanel Actions(params Control[] controls)
    { var panel = new WrapStack(); foreach (var control in controls) panel.Children.Add(control); return panel; }
    /// <summary>Compact controls sit right of the label; wide ones go below it.</summary>
    private static void Field(Panel panel, string name, Control control)
    {
        AutomationProperties.SetName(control, name);
        if (control is VisualNumber or ComboBox or ToggleSwitch)
        {
            if (control is ComboBox combo) { combo.HorizontalAlignment = HorizontalAlignment.Right; combo.MinWidth = 120; combo.MaxWidth = 220; }
            var line = Ui.Line(name, control); line.IsVisible = control.IsVisible; panel.Children.Add(line);
            control.PropertyChanged += (_, args) => { if (args.Property == IsVisibleProperty) line.IsVisible = control.IsVisible; };
            return;
        }
        var label = Label(name); label.Foreground = Palette.Secondary; label.FontSize = 12; label.IsVisible = control.IsVisible;
        panel.Children.Add(label); panel.Children.Add(control);
        control.PropertyChanged += (_, args) => { if (args.Property == IsVisibleProperty) label.IsVisible = control.IsVisible; };
    }
}

/// <summary>Horizontal flow of controls that wraps.</summary>
internal sealed class WrapStack : StackPanel
{
    public WrapStack() { Orientation = Orientation.Horizontal; Spacing = 8; }
    public double RowSpacing { get; set; } = 8;
    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, line = 0, width = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width) { y += line + RowSpacing; x = 0; line = 0; }
            x += size.Width + Spacing; line = Math.Max(line, size.Height); width = Math.Max(width, x - Spacing);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), y + line);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, line = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width) { y += line + RowSpacing; x = 0; line = 0; }
            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width + Spacing; line = Math.Max(line, size.Height);
        }
        return finalSize;
    }
}

internal sealed class Glyphs : IFileBrowserGlyphs
{
    public Control Glyph(string name) => name switch
    {
        "plus-white" => Ui.Icon("plus", 13, Brushes.White),
        "chevron-right-muted" => Ui.Icon("chevron-right", 14, Palette.Tertiary),
        "hard-drive-muted" => Ui.Icon("hard-drive", 13, Palette.Secondary),
        _ => Ui.Icon(name, 15, Palette.Text)
    };
    public Control Entry(FileBrowserItem item) => Ui.Icon(Ui.FileIcon(item.Name, item.IsFolder), 16, Palette.Secondary);
    public Control Place(FileBrowserLocation location) => Ui.Icon(location.Name switch
    {
        "个人" or "主目录" or "Home" => "house", "影片" or "视频" or "Movies" or "Videos" => "video", "图片" or "Pictures" => "image-file",
        "下载" or "Downloads" => "download", "音乐" or "Music" => "music", "桌面" or "Desktop" => "monitor", "文稿" or "文档" or "Documents" => "file-text",
        _ => "hard-drive"
    }, 15, Palette.Blue);
}
