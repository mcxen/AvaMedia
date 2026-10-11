using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.FileUi;

namespace AvaMedia.Browser;

public sealed partial class ConsoleView
{
    private readonly StackPanel _tasks = new() { Spacing = 8 }, _detail = new() { Spacing = 8 };
    private readonly TextBlock _taskPage = Label("");
    private readonly ComboBox _state = new() { ItemsSource = new[] { "全部", "Waiting", "Running", "Paused", "Stopping", "Completed", "Failed", "Cancelled" }, SelectedIndex = 0 };
    private readonly Dictionary<string, TaskRow> _taskRows = new();
    private int _taskOffset, _detailOffset, _detailVersion;
    private int? _taskNext, _detailNext;
    private string? _detailId;
    private bool _refreshing, _logs;
    private Button _tasksPrevious = null!, _tasksNext = null!;
    private JsonNode? _organization;
    private JsonObject? _organizationSubmission;
    private static readonly Dictionary<string, string> States = new() { ["Waiting"] = "等待", ["Running"] = "运行中", ["Paused"] = "已暂停", ["Stopping"] = "停止中", ["Completed"] = "已完成", ["Failed"] = "失败", ["Cancelled"] = "已停止" };
    private sealed record TaskRow(Border Container, TextBlock Name, TextBlock State, TextBlock Detail, ProgressBar Progress, StackPanel Actions)
    { public string CurrentState { get; set; } = ""; }

    private Control BuildTasks()
    {
        _tasksPrevious = Action("上一页任务", async () => { _taskOffset = Math.Max(0, _taskOffset - 30); await RefreshTasks(); });
        _tasksNext = Action("下一页任务", async () => { if (_taskNext is { } next) _taskOffset = next; await RefreshTasks(); });
        _state.SelectionChanged += async (_, _) => { _taskOffset = 0; await Guard(RefreshTasks); };
        var root = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 10, Margin = new(12) };
        root.Children.Add(Actions(_state, Action("刷新任务", RefreshTasks), _tasksPrevious, _tasksNext, _taskPage));
        var body = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 16 };
        body.Children.Add(new ScrollViewer { Content = _tasks });
        var detail = new ScrollViewer { Content = _detail }; Grid.SetColumn(detail, 1); body.Children.Add(detail); AdaptColumns(body, detail);
        Grid.SetRow(body, 1); root.Children.Add(body); return root;
    }
    private async Task RefreshTasks()
    {
        if (!_connected || _refreshing) return;
        _refreshing = true;
        try
        {
            var state = _state.SelectedIndex > 0 ? _state.SelectedItem?.ToString() : null;
            var page = await RemoteApi.Call("list_tasks", new() { ["offset"] = _taskOffset, ["limit"] = 30, ["state"] = state });
            // Ignore a response if the user has changed page/filter while it was in flight.
            if (state != (_state.SelectedIndex > 0 ? _state.SelectedItem?.ToString() : null) || page["offset"]?.GetValue<int>() != _taskOffset) return;
            var items = page["tasks"].Items().ToArray();
            var ids = items.Select(task => task.Text("id")).ToHashSet();
            foreach (var id in _taskRows.Keys.Where(id => !ids.Contains(id)).ToArray()) { _tasks.Children.Remove(_taskRows[id].Container); _taskRows.Remove(id); }
            foreach (var task in items)
            {
                var id = task.Text("id");
                if (!_taskRows.TryGetValue(id, out var row))
                {
                    var stack = new StackPanel { Spacing = 5 }; var name = Label(""); var status = Label(""); var detail = Label("");
                    var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6 }; var actions = Actions();
                    stack.Children.Add(name); stack.Children.Add(status); stack.Children.Add(progress); stack.Children.Add(detail); stack.Children.Add(actions);
                    var border = new Border { Child = stack, BorderBrush = Brushes.LightGray, BorderThickness = new(1), Padding = new(10), CornerRadius = new(5) };
                    row = new(border, name, status, detail, progress, actions); _taskRows[id] = row; _tasks.Children.Add(border);
                }
                row.Name.Text = task.Text("name"); var current = task.Text("state");
                row.State.Text = $"{States.GetValueOrDefault(current, current)} · {task["progress"]?.GetValue<double>():0.#}% · {task.Text("submittedBy")}";
                row.Detail.Text = task.Text("error") is { Length: > 0 } error ? error : task.Text("detail");
                row.Progress.Value = task["progress"]?.GetValue<double>() ?? 0;
                if (row.CurrentState != current)
                {
                    row.CurrentState = current; row.Actions.Children.Clear();
                    row.Actions.Children.Add(Action("查看详情", () => OpenDetail(id)));
                    var actions = current switch
                    {
                        "Waiting" => new[] { ("start", "开始"), ("pause", "暂停"), ("stop", "停止") },
                        "Running" => new[] { ("pause", "暂停"), ("stop", "停止") },
                        "Paused" => new[] { ("resume", "恢复"), ("stop", "停止") },
                        "Failed" or "Cancelled" => new[] { ("retry", "重试") }, _ => []
                    };
                    foreach (var (action, label) in actions) row.Actions.Children.Add(Action(label, async () =>
                    { await RemoteApi.Call("control_task", new() { ["taskId"] = id, ["action"] = action }); await RefreshTasks(); if (_detailId == id) await ReadDetail(); }));
                }
            }
            _taskNext = page["nextOffset"]?.GetValue<int>(); _tasksPrevious.IsEnabled = _taskOffset > 0; _tasksNext.IsEnabled = _taskNext is not null;
            _taskPage.Text = $"共 {page["total"]} 项 · 第 {_taskOffset / 30 + 1} 页";
        }
        finally { _refreshing = false; }
    }
    private Task OpenDetail(string id)
    { _detailId = id; _detailOffset = 0; _logs = false; _organization = null; _organizationSubmission = null; return ReadDetail(); }
    private async Task ReadDetail()
    {
        if (_detailId is not { } id) return;
        var version = ++_detailVersion;
        var args = new JsonObject { ["taskId"] = id, ["offset"] = _detailOffset };
        if (_logs) args["maxCharacters"] = 16000; else { args["limit"] = 100; args["includeResult"] = true; }
        var payload = await RemoteApi.Call(_logs ? "get_task_log" : "get_task", args);
        if (version != _detailVersion || _detailId != id) return;
        _detail.Children.Clear(); _detailNext = payload["nextOffset"]?.GetValue<int>();
        _detail.Children.Add(Actions(Action("结果", async () => { _logs = false; _detailOffset = 0; await ReadDetail(); }),
            Action("处理日志", async () => { _logs = true; _detailOffset = 0; await ReadDetail(); }), Action("刷新详情", ReadDetail)));
        if (_detailOffset > 0) _detail.Children.Add(Action("返回第一页", async () => { _detailOffset = 0; await ReadDetail(); }));
        if (_detailNext is not null) _detail.Children.Add(Action("下一页结果", async () => { if (_detailNext is { } next) _detailOffset = next; await ReadDetail(); }));
        if (_logs) { _detail.Children.Add(ReadOnly(payload.Text("text"))); return; }
        var task = payload["task"]!;
        _detail.Children.Add(Label(task.Text("name")));
        _detail.Children.Add(Label($"{States.GetValueOrDefault(task.Text("state"), task.Text("state"))} · {task["progress"]}% · {task.Text("detail")}"));
        if (task.Flag("outputIsPartial")) _detail.Children.Add(Label("当前为部分结果"));
        if (task.Text("error").Length > 0) _detail.Children.Add(Label(task.Text("error")));
        var inputs = new StackPanel { Spacing = 4 }; var index = 0;
        foreach (var input in task["inputs"].Items())
        { var i = index++; inputs.Children.Add(FileActions(id, input.ToString(), "input", i)); }
        _detail.Children.Add(new Expander { Header = $"输入文件（{index}）", Content = inputs });
        if (task.Text("output").Length > 0 && task.Text("state") is "Completed" or "Failed" or "Cancelled")
        {
            _detail.Children.Add(PathLabel(task.Text("output"), "输出 · "));
            if (payload.Flag("outputIsDirectory")) _detail.Children.Add(Action("浏览输出目录", async () =>
            { var folder = await _source.ResolveAsync(task.Text("output"), CancellationToken.None); if (folder is { IsFolder: true } directory) { await _files.NavigateAsync(directory); _tabs.SelectedIndex = 0; } }));
            else _detail.Children.Add(FileActions(id, task.Text("output"), "output", 0));
        }
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
        else _detail.Children.Add(Label("结果尚未生成，完成后刷新查看。"));
        if (task.Text("featureId") == "folder-classification" && task.Text("state") is "Completed" or "Failed" or "Cancelled")
        {
            var destination = new PathInput { Text = _output.Text }; Field(_detail, "分类复制目标目录", destination);
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
        _detail.Children.Add(new Expander { Header = "原始数据", Content = ReadOnly(payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true })) });
    }
    private Control FileActions(string id, string path, string source, int index)
    {
        var url = $"/web/tasks/{Uri.EscapeDataString(id)}/file?source={source}&index={index}";
        return Actions(Label(path.Split('/', '\\').Last()), Action("预览文件", () => { BrowserHost.Open(url); return Task.CompletedTask; }),
            Action("下载文件", () => { BrowserHost.Open(url + "&download=true"); return Task.CompletedTask; }));
    }
    private static Control PathLabel(string path, string prefix = "")
    {
        var label = Label(prefix + PathInput.ShortName(path));
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
