using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Automation;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using AvaMedia.FileUi;

namespace AvaMedia.Browser;

public sealed partial class ConsoleView
{
    private const int TaskPageSize = 30;
    private readonly StackPanel _tasks = new(), _detail = new() { Spacing = 10 };
    private readonly TextBlock _taskPage = Ui.Text("", "secondary"), _taskSubtitle = Ui.Text("", "secondary");
    private readonly StackPanel _filters = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly Dictionary<string, TaskRow> _taskRows = new();
    private readonly Dictionary<string, JsonNode> _taskData = new();
    private int _taskOffset, _detailOffset, _detailVersion;
    private int? _taskNext, _detailNext;
    private string? _detailId, _taskFilter;
    private bool _refreshing, _logs, _taskDetailOpen;
    private IReadOnlyDictionary<string, int> _counts = new Dictionary<string, int>();
    private Button _tasksPrevious = null!, _tasksNext = null!;
    private JsonNode? _organization;
    private JsonObject? _organizationSubmission;
    private Grid _tasksGrid = null!, _tasksListHeader = null!, _tasksMobileHeader = null!;
    private Border _listCard = null!, _detailCard = null!;
    private readonly ScrollViewer _detailScroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly ScrollViewer _taskScroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private static readonly Dictionary<string, string> States = new() { ["Waiting"] = "等待", ["Running"] = "运行中", ["Paused"] = "已暂停", ["Stopping"] = "停止中", ["Completed"] = "已完成", ["Failed"] = "失败", ["Cancelled"] = "已停止" };
    private static readonly (string? State, string Label)[] Filters = [(null, "全部"), ("Running", "运行中"), ("Waiting", "等待"), ("Paused", "已暂停"), ("Completed", "已完成"), ("Failed", "失败"), ("Cancelled", "已停止")];
    private sealed record TaskRow(Border Container, Grid Layout, TextBlock Name, TextBlock Sub, StackPanel State, ProgressBar Progress, TextBlock Detail, Panel Actions, Border Accent)
    { public string CurrentState { get; set; } = ""; public bool Mobile { get; set; } }

    private static (string Icon, IBrush Brush) StateLook(string state) => state switch
    {
        "Running" or "Stopping" => ("refresh-cw", Palette.Blue), "Paused" => ("pause", Palette.Orange), "Completed" => ("check", Palette.GreenText),
        "Failed" => ("circle-alert", Palette.Red), "Cancelled" => ("ban", Palette.Secondary), _ => ("clock", Palette.Secondary)
    };

    private Control BuildTasks()
    {
        _tasksPrevious = Action("上一页任务", async () => { _taskOffset = Math.Max(0, _taskOffset - TaskPageSize); await RefreshTasks(); });
        _tasksNext = Action("下一页任务", async () => { if (_taskNext is { } next) _taskOffset = next; await RefreshTasks(); });
        var title = Ui.Text("所有任务", "title"); var auto = Ui.Text("每 3 秒自动刷新", "secondary");
        var refresh = Action("刷新任务", RefreshTasks, "refresh-cw");
        var create = Action("创建任务", () => { _tabs.SelectedIndex = 0; return Task.CompletedTask; }, "plus", primary: true);
        _tasksListHeader = Header(Ui.Row(10, title, auto), Ui.Row(8, refresh, create));
        var filterScroll = new ScrollViewer { Content = _filters, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var top = new StackPanel { Spacing = 12, Margin = new(14, 14, 14, 12) }; top.Children.Add(_tasksListHeader); top.Children.Add(filterScroll);
        // Mobile large title
        var large = Ui.Text("所有任务", "largeTitle");
        _tasksMobileHeader = new Grid { RowDefinitions = new("Auto,Auto"), Margin = new(2, 4, 2, 0), IsVisible = false };
        _tasksMobileHeader.Children.Add(large); Grid.SetRow(_taskSubtitle, 1); _tasksMobileHeader.Children.Add(_taskSubtitle);
        top.Children.Insert(0, _tasksMobileHeader);
        _taskScroll.Content = _tasks;
        var footer = Header(_taskPage, Ui.Row(8, _tasksPrevious, _tasksNext)); footer.Margin = new(14, 10);
        var list = new Grid { RowDefinitions = new("Auto,*,Auto") };
        list.Children.Add(top);
        var listBody = new Border { BorderBrush = Palette.Hairline, BorderThickness = new(0, 1, 0, 0), Child = _taskScroll }; Grid.SetRow(listBody, 1); list.Children.Add(listBody);
        var footerBorder = new Border { Child = footer, BorderBrush = Palette.Hairline, BorderThickness = new(0, 1, 0, 0) }; Grid.SetRow(footerBorder, 2); list.Children.Add(footerBorder);
        _listCard = Ui.Card(list);
        _detailScroll.Content = new Border { Child = _detail, Padding = new(14) };
        _detailCard = Ui.Card(_detailScroll);
        _detail.Children.Add(Ui.Text("选择任务查看详情。", "secondary"));
        _tasksGrid = new Grid { ColumnDefinitions = new("*,316"), ColumnSpacing = 12, Margin = new(18, 2, 18, 18) };
        _tasksGrid.Children.Add(_listCard); Grid.SetColumn(_detailCard, 1); _tasksGrid.Children.Add(_detailCard);
        RenderFilters();
        return _tasksGrid;
    }
    private void LayoutTasks()
    {
        if (_tasksGrid is null) return;
        _tasksMobileHeader.IsVisible = _mobile; _tasksListHeader.IsVisible = !_mobile;
        if (_mobile)
        {
            _tasksGrid.ColumnDefinitions = new("*"); _tasksGrid.Margin = new(16, 0, 16, 0);
            _listCard.IsVisible = !_taskDetailOpen; _detailCard.IsVisible = _taskDetailOpen; Grid.SetColumn(_detailCard, 0);
            _listCard.Background = Brushes.Transparent; _listCard.BoxShadow = default;
        }
        else
        {
            _tasksGrid.ColumnDefinitions = new("*,316"); _tasksGrid.Margin = new(18, 2, 18, 18);
            _listCard.IsVisible = _detailCard.IsVisible = true; Grid.SetColumn(_detailCard, 1);
            _listCard.Background = Palette.Card; _listCard.BoxShadow = BoxShadows.Parse("0 1 2 0 #0F000000, 0 0 0 1 #0D000000");
        }
        _fab.IsVisible = _mobile && _tabs.SelectedIndex == 1 && !_taskDetailOpen;
        foreach (var row in _taskRows.Values) row.CurrentState = "";
        _tasks.Children.Clear(); _taskRows.Clear();
        if (_connected) _ = Guard(RefreshTasks);
    }
    private void RenderFilters()
    {
        _filters.Children.Clear();
        foreach (var (state, label) in Filters)
        {
            var count = _counts.GetValueOrDefault(state ?? "All") + (state == "Running" ? _counts.GetValueOrDefault("Stopping") : 0);
            if (state is "Cancelled" && count == 0 && _taskFilter != "Cancelled") continue;
            var active = _taskFilter == state;
            var text = Ui.Text(L.T(label)); text.FontSize = 12.5;
            var number = Ui.Text(count.ToString()); number.FontSize = 11.5; number.Foreground = active ? Brushes.White : Palette.Secondary;
            if (active) { text.Foreground = Brushes.White; text.FontWeight = FontWeight.Bold; }
            var button = new Button { Content = Ui.Row(5, text, number), Classes = { "chip" } };
            if (_mobile) { button.Padding = new(14, 7); button.Classes.Add("outline"); }
            if (active) button.Classes.Add("selected");
            AutomationProperties.SetName(button, label);
            button.Click += async (_, _) => { _taskFilter = state; _taskOffset = 0; RenderFilters(); await Guard(RefreshTasks); };
            _filters.Children.Add(button);
        }
    }
    private async Task RefreshTasks()
    {
        if (!_connected || _refreshing) return;
        _refreshing = true;
        try
        {
            var state = _taskFilter;
            var page = await RemoteApi.Call("list_tasks", new() { ["offset"] = _taskOffset, ["limit"] = TaskPageSize, ["state"] = state });
            // Ignore a response if the user has changed page/filter while it was in flight.
            if (state != _taskFilter || page["offset"]?.GetValue<int>() != _taskOffset) return;
            if (page["counts"] is JsonObject counts)
            {
                var next = counts.ToDictionary(pair => pair.Key, pair => pair.Value?.GetValue<int>() ?? 0);
                if (!next.SequenceEqual(_counts)) { _counts = next; RenderFilters(); }
            }
            var running = _counts.GetValueOrDefault("Running");
            _taskSubtitle.Text = L.F("{0} 个运行中 · 每 3 秒自动刷新", running);
            var items = page["tasks"].Items().ToArray();
            var ids = items.Select(task => task.Text("id")).ToList();
            foreach (var id in _taskRows.Keys.Where(id => !ids.Contains(id)).ToArray()) { _tasks.Children.Remove(_taskRows[id].Container); _taskRows.Remove(id); }
            for (var index = 0; index < items.Length; index++)
            {
                var task = items[index]; var id = task.Text("id"); _taskData[id] = task;
                if (!_taskRows.TryGetValue(id, out var row)) { row = _mobile ? MobileRow(id) : DesktopRow(id); _taskRows[id] = row; }
                var position = _tasks.Children.IndexOf(row.Container);
                if (position != index) { if (position >= 0) _tasks.Children.RemoveAt(position); _tasks.Children.Insert(Math.Min(index, _tasks.Children.Count), row.Container); }
                UpdateRow(row, task);
            }
            _taskNext = page["nextOffset"]?.GetValue<int>(); _tasksPrevious.IsEnabled = _taskOffset > 0; _tasksNext.IsEnabled = _taskNext is not null;
            _taskPage.Text = L.F("共 {0} 项 · 第 {1} 页", page["total"], _taskOffset / TaskPageSize + 1);
            if (items.Length == 0 && _tasks.Children.Count == 0) _tasks.Children.Add(new Border { Padding = new(16, 24), Child = Ui.Text("没有符合条件的任务。", "secondary") });
            else if (items.Length > 0) foreach (var empty in _tasks.Children.OfType<Border>().Where(border => border.Child is TextBlock).ToArray()) _tasks.Children.Remove(empty);
            if (_detailId is not null && _taskData.TryGetValue(_detailId, out var current)) UpdateDetailHeader(current);
        }
        finally { _refreshing = false; }
    }
    private TaskRow DesktopRow(string id)
    {
        var name = Ui.Text("", raw: true, trim: true); name.FontWeight = FontWeight.Bold; name.FontSize = 13.5;
        var sub = Ui.Text("", "caption", raw: true, trim: true);
        var left = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center }; left.Children.Add(name); left.Children.Add(sub);
        var state = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Width = 160, Height = 4, MinHeight = 4, HorizontalAlignment = HorizontalAlignment.Left };
        var detail = Ui.Text("", "caption", raw: true, trim: true);
        var middle = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Width = 180 };
        middle.Children.Add(state); middle.Children.Add(progress); middle.Children.Add(detail);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var grid = new Grid { ColumnDefinitions = new("3,*,170,286"), ColumnSpacing = 12, MinHeight = 56 };
        var accent = new Border { Background = Palette.Blue, IsVisible = false, Margin = new(0, 0, 0, 0) };
        grid.Children.Add(accent);
        Grid.SetColumn(left, 1); grid.Children.Add(left); Grid.SetColumn(middle, 2); grid.Children.Add(middle);
        Grid.SetColumn(actions, 3); actions.Margin = new(0, 0, 14, 0); grid.Children.Add(actions);
        var container = new Border { Child = grid, BorderBrush = Palette.Hairline, BorderThickness = new(0, 0, 0, 1), Background = Brushes.Transparent, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        container.PointerPressed += async (_, e) => { if (e.Source is Control control && control.FindAncestorOfType<Button>() is null && control is not Button) await Guard(() => OpenDetail(id)); };
        return new(container, grid, name, sub, state, progress, detail, actions, accent) { Mobile = false };
    }
    private TaskRow MobileRow(string id)
    {
        var name = Ui.Text("", raw: true, trim: true); name.FontWeight = FontWeight.Bold; name.FontSize = 16;
        var sub = Ui.Text("", "secondary", raw: true, trim: true); sub.FontSize = 13;
        var state = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        var head = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 }; head.Children.Add(name); Grid.SetColumn(state, 1); head.Children.Add(state);
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, MinHeight = 4 };
        var detail = Ui.Text("", "caption", raw: true, trim: true);
        var actions = new WrapStack { Spacing = 8 };
        var stack = new StackPanel { Spacing = 8, Margin = new(16, 14) };
        stack.Children.Add(head); stack.Children.Add(sub); stack.Children.Add(progress); stack.Children.Add(detail); stack.Children.Add(actions);
        var grid = new Grid(); grid.Children.Add(stack);
        var container = new Border { Child = grid, BorderBrush = Palette.Hairline, BorderThickness = new(0, 0, 0, 1), Background = Brushes.White };
        return new(container, grid, name, sub, state, progress, detail, actions, new Border()) { Mobile = true };
    }
    private string FeatureName(string id) => _features.FirstOrDefault(item => item.Text("id") == id).Text("name") is { Length: > 0 } name ? name : id;
    private static string Source(JsonNode task) => task.Text("submittedBy") is { Length: > 0 } by ? by : L.T("桌面");
    private void UpdateRow(TaskRow row, JsonNode task)
    {
        var id = task.Text("id"); var current = task.Text("state");
        var progressValue = task["progress"]?.GetValue<double>() ?? 0;
        row.Name.Text = task.Text("name");
        row.Sub.Text = row.Mobile ? $"{FeatureName(task.Text("featureId"))} · {Source(task)}" + (current is "Running" or "Paused" or "Stopping" ? $" · {progressValue:0}%" : "")
            : $"{FeatureName(task.Text("featureId"))} · {Source(task)}";
        var (icon, brush) = StateLook(current);
        row.State.Children.Clear(); row.State.Children.Add(Ui.Icon(icon, 12, brush));
        var label = Ui.Text(L.T(States.GetValueOrDefault(current, current))); label.Foreground = brush; label.FontSize = 12; row.State.Children.Add(label);
        if (!row.Mobile && current is not ("Completed" or "Cancelled")) { var percent = Ui.Text($"{progressValue:0}%", "caption"); row.State.Children.Add(percent); }
        else if (!row.Mobile && current == "Completed") row.State.Children.Add(Ui.Text("100%", "caption"));
        row.Progress.Value = progressValue;
        row.Progress.IsVisible = current is "Running" or "Paused" or "Stopping";
        row.Progress.Classes.Set("paused", current == "Paused");
        var error = task.Text("error");
        row.Detail.Text = error.Length > 0 ? error : Activity(task);
        row.Detail.Foreground = error.Length > 0 ? Palette.Red : Palette.Secondary;
        row.Detail.IsVisible = row.Detail.Text.Length > 0 && !(row.Mobile && error.Length == 0);
        var selected = _detailId == id && !row.Mobile;
        row.Accent.IsVisible = selected; row.Container.Background = selected ? Palette.BlueSoft : row.Mobile ? Brushes.White : Brushes.Transparent;
        if (row.CurrentState == current) return;
        row.CurrentState = current; row.Actions.Children.Clear();
        foreach (var button in TaskButtons(id, current)) row.Actions.Children.Add(button);
        var details = Action("详情", () => OpenDetail(id)); row.Actions.Children.Add(details);
    }
    private static string Activity(JsonNode task)
    {
        var detail = task.Text("detail");
        if (task["activity"] is { } activity && activity["current"] is not null && activity["total"] is not null && activity.Text("unit") is { Length: > 0 } unit)
            return $"{activity["current"]} / {activity["total"]} {unit}";
        return detail;
    }
    private IEnumerable<Button> TaskButtons(string id, string state)
    {
        var actions = state switch
        {
            "Waiting" => new[] { ("start", "开始", "circle-play"), ("pause", "暂停", "pause"), ("stop", "停止", "square") },
            "Running" => new[] { ("pause", "暂停", "pause"), ("stop", "停止", "square") },
            "Paused" => new[] { ("resume", "恢复", "circle-play"), ("stop", "停止", "square") },
            "Failed" or "Cancelled" => new[] { ("retry", "重试", "rotate-ccw") }, _ => []
        };
        foreach (var (action, label, icon) in actions)
            yield return Action(label, async () =>
            { await RemoteApi.Call("control_task", new() { ["taskId"] = id, ["action"] = action }); await RefreshTasks(); if (_detailId == id) await ReadDetail(); }, icon);
    }
    private Task OpenDetail(string id)
    {
        _detailId = id; _detailOffset = 0; _logs = false; _organization = null; _organizationSubmission = null;
        if (_mobile) { _taskDetailOpen = true; _listCard.IsVisible = false; _detailCard.IsVisible = true; _fab.IsVisible = false; }
        foreach (var (key, row) in _taskRows) if (_taskData.TryGetValue(key, out var data)) { row.CurrentState = ""; UpdateRow(row, data); }
        return ReadDetail();
    }
    private void CloseDetail() { _taskDetailOpen = false; _detailId = null; LayoutTasks(); }
    private readonly Dictionary<string, Bitmap?> _thumbnails = new();
    private readonly StackPanel _detailHeader = new() { Spacing = 10 };
    private async Task ReadDetail()
    {
        if (_detailId is not { } id) return;
        var version = ++_detailVersion;
        var args = new JsonObject { ["taskId"] = id, ["offset"] = _detailOffset };
        if (_logs) args["maxCharacters"] = 16000; else { args["limit"] = 100; args["includeResult"] = true; }
        var payload = await RemoteApi.Call(_logs ? "get_task_log" : "get_task", args);
        var task = _logs ? (await RemoteApi.Call("get_task", new() { ["taskId"] = id }))["task"]! : payload["task"]!;
        if (version != _detailVersion || _detailId != id) return;
        _detail.Children.Clear(); _detailNext = payload["nextOffset"]?.GetValue<int>();
        if (_mobile)
        {
            var back = new Button { Content = Ui.Content(L.T("所有任务"), "chevron-left", Palette.Blue, 16), Classes = { "link" }, HorizontalAlignment = HorizontalAlignment.Left };
            back.Click += (_, _) => CloseDetail(); _detail.Children.Add(back);
        }
        _detail.Children.Add(_detailHeader); UpdateDetailHeader(task);
        // Result / log segmented control
        var segments = new Grid { ColumnDefinitions = new("*,*") };
        var tabs = new Border { Background = Palette.Fill, CornerRadius = new(9), Padding = new(2), Child = segments };
        foreach (var (label, logs, column) in new[] { ("结果", false, 0), ("处理日志", true, 1) })
        {
            var button = new Button { Content = label, Classes = { "segmentLight" }, HorizontalAlignment = HorizontalAlignment.Stretch };
            if (_logs == logs) button.Classes.Add("active");
            button.Click += async (_, _) => { _logs = logs; _detailOffset = 0; await Guard(ReadDetail); };
            Grid.SetColumn(button, column); segments.Children.Add(button);
        }
        _detail.Children.Add(tabs);
        if (_detailOffset > 0) _detail.Children.Add(Action("返回第一页", async () => { _detailOffset = 0; await ReadDetail(); }));
        if (_detailNext is not null) _detail.Children.Add(Action("下一页结果", async () => { if (_detailNext is { } next) _detailOffset = next; await ReadDetail(); }));
        if (_logs) { _detail.Children.Add(LogBox(payload.Text("text"))); return; }
        var facts = new StackPanel { Spacing = 6 };
        var inputs = task["inputs"].Items().ToArray();
        for (var index = 0; index < inputs.Length && index < 20; index++)
            facts.Children.Add(Fact(index == 0 ? "输入文件" : "", FileActions(id, inputs[index].ToString(), "input", index)));
        if (inputs.Length > 20) facts.Children.Add(Fact("", Ui.Text(L.F("另有 {0} 个文件", inputs.Length - 20), "caption")));
        if (task.Text("output").Length > 0)
        {
            var finished = task.Text("state") is "Completed" or "Failed" or "Cancelled";
            Control output = finished && !payload.Flag("outputIsDirectory") ? FileActions(id, task.Text("output"), "output", 0) : PathLabel(task.Text("output"));
            facts.Children.Add(Fact("输出", output));
            if (finished && payload.Flag("outputIsDirectory")) facts.Children.Add(Fact("", Action("浏览输出目录", async () =>
            { var folder = await _source.ResolveAsync(task.Text("output"), CancellationToken.None); if (folder is { IsFolder: true } directory) { await _files.NavigateAsync(directory); _workspaceTabs.SelectedIndex = 0; _tabs.SelectedIndex = 0; } }, "folder-closed")));
        }
        var format = _features.FirstOrDefault(item => item.Text("id") == task.Text("featureId")).Text("format");
        if (format.Length > 0) facts.Children.Add(Fact("格式", Ui.Text(format.ToUpperInvariant(), raw: true)));
        if (task.Flag("outputIsPartial") && task.Text("state") != "Completed") facts.Children.Add(Fact("", Ui.Text("当前为部分结果", "caption")));
        if (task.Text("error").Length > 0) { var error = Ui.Text(task.Text("error"), raw: true); error.Foreground = Palette.Red; error.TextWrapping = TextWrapping.Wrap; error.FontSize = 12.5; facts.Children.Add(Fact("错误", error)); }
        _detail.Children.Add(facts);
        if (payload["result"] is { } result)
        {
            if (result is JsonObject && result["nsfw"] is { } nsfw)
            {
                _detail.Children.Add(Label("NSFW 内容识别"));
                _detail.Children.Add(Label($"平均分 {nsfw["average"]?.GetValue<double>():P1} · 最高分 {nsfw["maximum"]?.GetValue<double>():P1} · 阈值 {nsfw["threshold"]?.GetValue<double>():P1}"));
                _detail.Children.Add(Label(nsfw.Text("model") + " · " + nsfw.Text("backend")));
                foreach (var frame in nsfw["frames"].Items()) _detail.Children.Add(Label($"{frame["seconds"]?.GetValue<double>():0.0} 秒 · {frame["score"]?.GetValue<double>():P1}"));
            }
            var budget = 1400; _detail.Children.Add(RenderResult(result, ref budget));
        }
        else
        {
            var waiting = Ui.Text(task.Text("state") == "Completed" ? "该任务没有结构化结果；输出见上方文件。" : "结果尚未生成，完成后自动刷新。", "secondary"); waiting.TextWrapping = TextWrapping.Wrap; _detail.Children.Add(waiting);
            if (task.Text("state") is "Running" or "Paused" or "Waiting")
            {
                // Recent log lines while the task is active.
                try
                {
                    var log = await RemoteApi.Call("get_task_log", new() { ["taskId"] = id, ["maxCharacters"] = 600 });
                    var lines = log.Text("text").Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(6);
                    if (version == _detailVersion && lines.Any()) { _detail.Children.Add(Ui.Text("处理日志（最近）", "section")); _detail.Children.Add(LogBox(string.Join("\n", lines))); }
                }
                catch (Exception) { }
            }
        }
        if (task.Text("featureId") == "folder-classification" && task.Text("state") is "Completed" or "Failed" or "Cancelled")
        {
            var destination = new FilePathChoice { Text = _output.Text }; Field(_detail, "分类复制目标目录", destination);
            _detail.Children.Add(Action("选择分类目标文件夹…", async () =>
            {
                _tabs.SelectedIndex = 0;
                await PickServerFile("分类目标目录", true, selected => { destination.Text = selected; _tabs.SelectedIndex = 1; });
            }));
            _detail.Children.Add(Action("预览分类文件夹", async () =>
            {
                _organization = await RemoteApi.Call("preview_organization", new() { ["taskId"] = id, ["outputFolder"] = destination.Text?.Trim() });
                _organizationSubmission = null;
                var plan = _organization["plan"].Items().ToArray();
                _detail.Children.Add(Label($"将复制 {plan.Length} 个文件，保留源文件。"));
                foreach (var file in plan.Take(100))
                {
                    _detail.Children.Add(PathLabel(file.Text("target"), PathInput.ShortName(file.Text("source")) + " → "));
                    if (file.Text("reportPath").Length > 0) _detail.Children.Add(PathLabel(file.Text("reportPath"), "标签报告："));
                }
                if (plan.Length > 100) _detail.Children.Add(Label($"另有 {plan.Length - 100} 项，完整目标见预览数据。"));
                _detail.Children.Add(new Expander { Header = "完整预览数据", Content = ReadOnly(_organization.ToJsonString(new JsonSerializerOptions { WriteIndented = true })) });
                var snapshot = _organization;
                _detail.Children.Add(Action("按此预览复制文件", async () =>
                {
                    if (!ReferenceEquals(snapshot, _organization)) throw new InvalidOperationException("预览已经更新，请使用最新预览。");
                    _organizationSubmission ??= new() { ["requestId"] = Guid.NewGuid().ToString(), ["preview"] = snapshot.DeepClone(), ["startImmediately"] = true };
                    if (_pending is not null) throw new InvalidOperationException("请先恢复或清除上次提交记录。");
                    var draft = new JsonObject { ["method"] = "apply_organization", ["arguments"] = _organizationSubmission.DeepClone() };
                    BrowserHost.WritePending(draft.ToJsonString()); _pending = draft;
                    await Submit(); _organization = null;
                    _status.Text = "已下发分类复制任务，源文件保留。"; await RefreshTasks();
                }));
            }));
        }
        _detail.Children.Add(new OptionSection("原始数据", new StackPanel { Children = { ReadOnly(payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true })) } }, false));
    }
    private static Grid Fact(string label, Control value)
    {
        var grid = new Grid { ColumnDefinitions = new("64,*"), ColumnSpacing = 8 };
        var name = Ui.Text(label, "secondary"); name.VerticalAlignment = VerticalAlignment.Top; name.Margin = new(0, 3, 0, 0); grid.Children.Add(name);
        Grid.SetColumn(value, 1); grid.Children.Add(value); return grid;
    }
    private static Border LogBox(string text)
    {
        var block = Ui.Text(text.Length == 0 ? "（暂无日志）" : text, "mono", raw: true); block.TextWrapping = TextWrapping.Wrap; block.FontSize = 11.5;
        return new Border { Background = new SolidColorBrush(Color.Parse("#F5F5F7")), CornerRadius = new(9), Padding = new(12, 10), Child = new ScrollViewer { Content = block, MaxHeight = 420 } };
    }
    private void UpdateDetailHeader(JsonNode task)
    {
        var id = task.Text("id"); var state = task.Text("state"); var progress = task["progress"]?.GetValue<double>() ?? 0;
        _detailHeader.Children.Clear();
        var thumb = new Border { Width = 58, Height = 40, CornerRadius = new(7), ClipToBounds = true, Background = new LinearGradientBrush
        { StartPoint = new(0, 0, RelativeUnit.Relative), EndPoint = new(1, 1, RelativeUnit.Relative), GradientStops = { new(Color.Parse("#5AC8FA"), 0), new(Color.Parse("#1D3E8A"), 1) } } };
        if (_thumbnails.TryGetValue(id, out var bitmap) && bitmap is not null) thumb.Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
        else if (!_thumbnails.ContainsKey(id)) _ = LoadThumbnail(id, task["inputs"].Items().FirstOrDefault()?.ToString());
        var name = Ui.Text(task.Text("name"), "strong", raw: true, trim: true); name.FontSize = 14.5;
        var sub = Ui.Text($"{FeatureName(task.Text("featureId"))} · {Source(task)}", "caption", raw: true);
        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center }; titles.Children.Add(name); titles.Children.Add(sub);
        var top = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10 }; top.Children.Add(thumb); Grid.SetColumn(titles, 1); top.Children.Add(titles);
        _detailHeader.Children.Add(top);
        var (icon, brush) = StateLook(state);
        var stateLabel = Ui.Text(L.T(States.GetValueOrDefault(state, state))); stateLabel.Foreground = brush; stateLabel.FontSize = 12.5;
        var activity = Ui.Text(state is "Running" or "Paused" ? $"{progress:0}%" + (Activity(task) is { Length: > 0 } detail ? " · " + detail : "") : "", "caption", raw: true, trim: true);
        activity.HorizontalAlignment = HorizontalAlignment.Right;
        _detailHeader.Children.Add(Header(Ui.Row(5, Ui.Icon(icon, 13, brush), stateLabel), activity));
        if (state is "Running" or "Paused" or "Stopping")
        { var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = progress, Height = 5, MinHeight = 5 }; bar.Classes.Set("paused", state == "Paused"); _detailHeader.Children.Add(bar); }
        var buttons = new WrapStack { Spacing = 6 }; foreach (var button in TaskButtons(id, state)) buttons.Children.Add(button);
        if (buttons.Children.Count > 0) _detailHeader.Children.Add(buttons);
    }
    private async Task LoadThumbnail(string id, string? path)
    {
        _thumbnails[id] = null;
        if (path is null || !Ui.FileIcon(path, false).StartsWith("video") && Ui.FileIcon(path, false) != "image-file") return;
        try
        {
            var (image, _) = await RemotePreview.Read(path, 1, 640, 0, 0, CancellationToken.None);
            using var stream = new MemoryStream(image); _thumbnails[id] = new Bitmap(stream);
            if (_detailId == id && _taskData.TryGetValue(id, out var task)) UpdateDetailHeader(task);
        }
        catch (Exception) { }
    }
    private Control FileActions(string id, string path, string source, int index)
    {
        var url = $"/web/tasks/{Uri.EscapeDataString(id)}/file?source={source}&index={index}";
        var name = PathLabel(path); 
        var open = new Button { Content = Ui.Icon("eye", 13, Palette.Secondary), Classes = { "plain", "icon" }, Width = 24, Height = 24 }; ToolTip.SetTip(open, L.T("预览文件"));
        open.Click += (_, _) => BrowserHost.Open(url); AutomationProperties.SetName(open, "预览文件");
        var download = new Button { Content = Ui.Icon("download", 13, Palette.Secondary), Classes = { "plain", "icon" }, Width = 24, Height = 24 }; ToolTip.SetTip(download, L.T("下载文件"));
        download.Click += (_, _) => BrowserHost.Open(url + "&download=true"); AutomationProperties.SetName(download, "下载文件");
        var grid = new Grid { ColumnDefinitions = new("*,Auto,Auto") }; grid.Children.Add(name);
        Grid.SetColumn(open, 1); grid.Children.Add(open); Grid.SetColumn(download, 2); grid.Children.Add(download); return grid;
    }
    private static Control PathLabel(string path, string prefix = "")
    {
        var label = Ui.Text(prefix + path, raw: true); label.FontSize = 12.5;
        label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(label, path); return label;
    }
    private static TextBox ReadOnly(string text) => new() { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 400 };
    private static Control RenderResult(JsonNode? node, ref int budget, int depth = 0)
    {
        if (--budget < 0 || depth > 8) return Label("更多内容见原始数据或下一页。");
        if (node is JsonValue || node is null) return Label(node?.ToString() ?? "—");
        var panel = new StackPanel { Spacing = 4, Margin = new(8, 2) };
        if (node is JsonArray array)
        {
            foreach (var item in array) { if (budget < 0) break; panel.Children.Add(RenderResult(item, ref budget, depth + 1)); }
        }
        else if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj)
            {
                if (budget < 0) break;
                if (value is null || key == "nsfw") continue;
                if (value is JsonValue) panel.Children.Add(key is "path" or "output" or "source" or "target" or "reportPath"
                    ? PathLabel(value.ToString(), ResultLabels.GetValueOrDefault(key, key) + "：")
                    : Label(ResultLabels.GetValueOrDefault(key, key) + "：" + value));
                else panel.Children.Add(new Expander { Header = ResultLabels.GetValueOrDefault(key, key), IsExpanded = depth < 1, Content = RenderResult(value, ref budget, depth + 1) });
            }
        }
        return panel;
    }
    private static readonly Dictionary<string, string> ResultLabels = new()
    {
        ["path"] = "文件", ["files"] = "文件结果", ["decisions"] = "分类结果", ["tags"] = "标签依据", ["labels"] = "内容标签",
        ["label"] = "标签", ["score"] = "分数", ["category"] = "分类", ["backend"] = "推理后端", ["model"] = "模型",
        ["caption"] = "画面描述", ["sampledFrames"] = "采样帧数", ["inferredFrames"] = "推理帧数", ["error"] = "错误",
        ["pending"] = "待确认", ["completed"] = "已完成", ["failed"] = "失败", ["total"] = "总数", ["sections"] = "总结",
        ["text"] = "内容", ["title"] = "标题", ["isPartial"] = "部分结果", ["durationSeconds"] = "时长（秒）"
    };
}
