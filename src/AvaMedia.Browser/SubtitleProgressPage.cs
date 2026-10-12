using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.Controls.Primitives;
using AvaMedia.Core;

namespace AvaMedia.Browser;

/// <summary>Incremental, virtualized subtitle view. Network work is async; each poll publishes once.</summary>
internal sealed class SubtitleProgressPage : UserControl
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly string _id;
    private readonly Action _back;
    private readonly Func<bool> _isMobile;
    private readonly Func<Task> _details;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private CancellationTokenSource? _lifetime;
    private readonly ObservableCollection<Cue> _cues = [];
    private readonly Grid _layout = new() { RowDefinitions = new("Auto,*,Auto") };
    private readonly Grid _content = new() { RowDefinitions = new("Auto,*"), RowSpacing = 14 };
    private readonly Grid _body = new() { ColumnDefinitions = new("*,260"), ColumnSpacing = 12, RowSpacing = 12 };
    private readonly StackPanel _heading = new() { Spacing = 14 }, _stages = new();
    private readonly Grid _header = new() { ColumnDefinitions = new("Auto,*,Auto,Auto"), ColumnSpacing = 12 };
    private readonly Grid _actions = new() { ColumnSpacing = 8 };
    private readonly ContentControl _headerActions = new();
    private readonly StackPanel _pills = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBlock _name = Ui.Text("", "title", raw: true, trim: true), _meta = Ui.Text("", "caption", raw: true, trim: true),
        _percent = Ui.Text("", "largeTitle", raw: true), _activity = Ui.Text("", raw: true, trim: true),
        _time = Ui.Text("", "caption", raw: true), _queue = Ui.Text("", "secondary", raw: true),
        _count = Ui.Text("", "secondary", raw: true), _error = Ui.Text("", raw: true), _log = Ui.Text("", "secondary", raw: true),
        _empty = Ui.Text("暂无字幕。", "secondary");
    private readonly MediaThumbnail _thumb = new(64, 44);
    private readonly ListBox _transcript;
    private readonly CheckBox _follow = new() { Content = "跟随最新字幕", IsChecked = true, IsVisible = false };
    private readonly Border _resultCard, _logCard, _headingCard, _surface, _footer;
    private readonly Grid _navigation = new() { ColumnDefinitions = new("*,Auto,*"), Height = 64 };
    private readonly ScrollViewer _mobileScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private JsonNode? _task;
    private AiTaskProgressDto? _progress;
    private string _state = "", _stageKey = "", _actionsKey = "";
    private bool _loading, _mobile, _stacked, _laidOut;
    private int _total;
    private long _generation;
    private DateTime _lastRefresh;
    private int _resultsEpoch;
    private string Base => BrowserHost.Origin() + "/web/tasks/" + Uri.EscapeDataString(_id) + "/ai/";
    internal sealed record Cue(double Start, double End, string Text);

    public SubtitleProgressPage(string id, Func<bool> isMobile, Action back, Func<Task> details)
    {
        _id = id; _isMobile = isMobile; _back = back; _details = details;
        _transcript = new ListBox { ItemsSource = _cues, Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<Cue>((cue, _) => cue is null ? null : CueRow(cue)) };
        _transcript.Styles.Add(new Style(s => s.OfType<ListBoxItem>())
        { Setters = { new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        // ListBox's default VirtualizingStackPanel realizes only the visible cue rows.
        var result = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 8 };
        var resultHeader = new Grid { ColumnDefinitions = new("*,Auto") };
        resultHeader.Children.Add(Ui.Row(10, Ui.Text("字幕结果", "strong"), _count));
        var follow = new Button { Content = Ui.Icon("arrow-down", 12, Palette.Blue), Classes = { "plain", "icon" }, Width = 22, Height = 22 };
        ToolTip.SetTip(follow, L.T("跟随最新字幕")); Avalonia.Automation.AutomationProperties.SetName(follow, L.T("跟随最新字幕"));
        follow.Click += (_, _) =>
        {
            _follow.IsChecked = _follow.IsChecked != true;
            follow.Content = Ui.Icon("arrow-down", 12, _follow.IsChecked == true ? Palette.Blue : Palette.Secondary);
            if (_follow.IsChecked == true && _cues.Count > 0) _transcript.ScrollIntoView(_cues[^1]);
        };
        Grid.SetColumn(follow, 1); resultHeader.Children.Add(follow); result.Children.Add(resultHeader);
        Grid.SetRow(_empty, 1); result.Children.Add(_empty);
        Grid.SetRow(_transcript, 1); result.Children.Add(_transcript);
        _resultCard = Ui.Card(result, new(14));
        _log.TextWrapping = TextWrapping.Wrap; _log.FontFamily = new("monospace"); _log.FontSize = 11; _log.LineHeight = 20;
        var logPanel = new StackPanel { Spacing = 6 };
        logPanel.Children.Add(Ui.Text("处理日志（最近）", "strong")); logPanel.Children.Add(_log);
        _logCard = Ui.Card(new ScrollViewer { Content = logPanel }, new(14));
        foreach (var card in new[] { _resultCard, _logCard })
        { card.BorderBrush = Palette.Hairline; card.BorderThickness = new(1); }
        _body.Children.Add(_resultCard); Grid.SetColumn(_logCard, 1); _body.Children.Add(_logCard);
        var title = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(_name); title.Children.Add(_pills); title.Children.Add(_meta);
        _header.Children.Add(_thumb); Grid.SetColumn(title, 1); _header.Children.Add(title);
        Grid.SetColumn(_percent, 2); _header.Children.Add(_percent);
        Grid.SetColumn(_headerActions, 3); _header.Children.Add(_headerActions);
        _heading.Children.Add(_header); _heading.Children.Add(_stages);
        _activity.FontSize = 12; _activity.FontWeight = FontWeight.Bold;
        _time.FontFamily = new("monospace");
        var activity = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10 };
        activity.Children.Add(_activity); Grid.SetColumn(_time, 1); activity.Children.Add(_time);
        _heading.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#F1F4FA")), CornerRadius = new(9), Padding = new(12, 9), Child = activity });
        _queue.TextWrapping = _error.TextWrapping = TextWrapping.Wrap;
        _heading.Children.Add(_queue); _heading.Children.Add(_error);
        _error.Foreground = Palette.Red; _error.IsVisible = _queue.IsVisible = false;
        _headingCard = Ui.Card(_heading);
        _content.Children.Add(_headingCard); Grid.SetRow(_body, 1); _content.Children.Add(_body);
        _surface = Ui.Card(_content, new(18));
        var backButton = Button("所有任务", () => { _back(); return Task.CompletedTask; }, "chevron-left");
        backButton.Classes.Add("link"); backButton.FontSize = 15;
        backButton.Content = Ui.Content(L.T("所有任务"), "chevron-left", Palette.Blue, 16);
        _navigation.Children.Add(backButton);
        var detailTitle = Ui.Text("任务详情", "title"); Grid.SetColumn(detailTitle, 1); _navigation.Children.Add(detailTitle);
        _layout.Children.Add(_navigation); Grid.SetRow(_surface, 1); _layout.Children.Add(_surface);
        _footer = new Border { Padding = new(14, 10, 14, 42), Background = Palette.Card,
            BorderBrush = Palette.Hairline, BorderThickness = new(0, 1, 0, 0) };
        Grid.SetRow(_footer, 2); _layout.Children.Add(_footer); Content = _layout;
        _timer.Tick += async (_, _) => await Refresh();
        AttachedToVisualTree += async (_, _) =>
        {
            _lifetime = new(); _generation++; _lastRefresh = default;
            _timer.Start(); await Refresh();
        };
        DetachedFromVisualTree += (_, _) => { _timer.Stop(); _generation++; _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null; };
        PropertyChanged += (_, args) => { if (args.Property == BoundsProperty) Layout(); };
    }

    private void Layout()
    {
        if (Bounds.Width <= 0) return;
        var mobile = _isMobile();
        var stacked = mobile || Bounds.Width < 720;
        if (_laidOut && _mobile == mobile && _stacked == stacked) return;
        _laidOut = true; _mobile = mobile; _stacked = stacked;
        _headerActions.Content = null; _footer.Child = null;
        _surface.Child = null; _mobileScroll.Content = null;
        _navigation.IsVisible = _footer.IsVisible = mobile;
        _surface.Padding = mobile ? new(14, 0, 14, 14) : new(18);
        _surface.Background = mobile ? Brushes.Transparent : Palette.Card;
        _surface.BoxShadow = mobile ? default : BoxShadows.Parse("0 1 2 0 #0F000000, 0 0 0 1 #0D000000");
        _surface.ClipToBounds = false;
        _headingCard.Padding = mobile ? new(14) : new(0);
        _headingCard.BoxShadow = mobile ? BoxShadows.Parse("0 0 0 1 #0D000000") : default;
        _header.ColumnDefinitions = new(mobile ? "Auto,*,Auto" : "Auto,*,Auto,Auto");
        _header.ColumnSpacing = mobile ? 10 : 12; _headerActions.IsVisible = !mobile;
        _thumb.Width = mobile ? 58 : 64; _thumb.Height = mobile ? 40 : 44;
        _name.FontSize = mobile ? 15 : 17; _percent.FontSize = mobile ? 24 : 28;
        _pills.IsVisible = !mobile; _meta.IsVisible = mobile;
        _body.ColumnDefinitions = new(stacked ? "*" : "*,280");
        _body.RowDefinitions = new(stacked ? "Auto,Auto" : "*");
        Grid.SetColumn(_logCard, stacked ? 0 : 1); Grid.SetRow(_logCard, stacked ? 1 : 0);
        _transcript.Height = stacked ? (mobile ? 244 : 320) : double.NaN;
        _logCard.MaxHeight = stacked ? 136 : double.PositiveInfinity;
        _logCard.VerticalAlignment = mobile ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        _content.RowDefinitions = new(mobile ? "Auto,Auto" : "Auto,*");
        if (mobile) { _mobileScroll.Content = _content; _surface.Child = _mobileScroll; _footer.Child = _actions; }
        else { _surface.Child = _content; _headerActions.Content = _actions; }
        _stageKey = _actionsKey = "";
        _transcript.ItemTemplate = new FuncDataTemplate<Cue>((cue, _) => cue is null ? null : CueRow(cue));
        RenderProgress();
    }

    private Control CueRow(Cue cue)
    {
        var time = Ui.Text(Time(cue.Start) + " – " + Time(cue.End), "caption", raw: true);
        time.FontFamily = new("monospace"); time.VerticalAlignment = VerticalAlignment.Top;
        time.FontSize = _mobile ? 10.5 : 11;
        var text = Ui.Text(cue.Text, raw: true); text.FontSize = 13; text.TextWrapping = TextWrapping.Wrap;
        var row = new Grid { ColumnDefinitions = new(_mobile ? "*" : "116,*"), RowDefinitions = new(_mobile ? "Auto,Auto" : "Auto"),
            ColumnSpacing = 12, RowSpacing = 2, Margin = new(0, 7) };
        row.Children.Add(time); Grid.SetColumn(text, _mobile ? 0 : 1); Grid.SetRow(text, _mobile ? 1 : 0); row.Children.Add(text);
        return new Border { Child = row, BorderBrush = Palette.Hairline, BorderThickness = new(0, 0, 0, 1) };
    }

    public async Task Refresh()
    {
        if (_loading || _lifetime is null || DateTime.UtcNow - _lastRefresh < TimeSpan.FromMilliseconds(250)) return;
        _loading = true; var generation = _generation; var epoch = _resultsEpoch; var ct = _lifetime.Token;
        try
        {
            var progressTask = Client.GetFromJsonAsync(Base + "progress", SubtitleJsonContext.Default.AiTaskProgressDto, ct);
            var detailTask = RemoteApi.Call("get_task", new() { ["taskId"] = _id }, ct);
            var resultsTask = ReadCues(_cues.Count, ct);
            var logTask = RemoteApi.Call("get_task_log", new() { ["taskId"] = _id, ["maxCharacters"] = 6000 }, ct);
            await Task.WhenAll(progressTask, detailTask, resultsTask, logTask);
            ct.ThrowIfCancellationRequested(); if (generation != _generation || epoch != _resultsEpoch) return;
            _progress = await progressTask; _task = (await detailTask)["task"];
            _state = _task.Text("state");
            _timer.Interval = TimeSpan.FromSeconds(_state is "Running" or "Stopping" ? 1.5 : 6);
            var result = await resultsTask;
            if (result.Total < _cues.Count)
            {
                // Retry/restart replaced the server transcript. Reset the incremental cursor.
                _cues.Clear(); result = await ReadCues(0, ct);
                ct.ThrowIfCancellationRequested(); if (generation != _generation || epoch != _resultsEpoch) return;
            }
            _total = result.Total;
            foreach (var cue in result.Cues) _cues.Add(cue);
            if (result.Cues.Length > 0 && _follow.IsChecked == true && _cues.Count > 0)
                Dispatcher.UIThread.Post(() => { if (_cues.Count > 0) _transcript.ScrollIntoView(_cues[^1]); }, DispatcherPriority.Loaded);
            _empty.IsVisible = _cues.Count == 0;
            _count.Text = L.F("{0} 条", _total);
            var log = (await logTask).Text("text");
            _log.Text = log.Length > 0 ? string.Join("\n", log.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(8)) : L.T("暂无日志。");
            _error.IsVisible = false;
            RenderProgress();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation == _generation && !ct.IsCancellationRequested)
            { _error.Text = L.F("进度暂时无法刷新：{0}", error.Message); _error.IsVisible = true; }
        }
        finally { _loading = false; _lastRefresh = DateTime.UtcNow; }
    }

    private async Task<(int Total, Cue[] Cues)> ReadCues(int after, CancellationToken ct)
    {
        using var response = await Client.GetAsync(Base + "results?after=" + after, ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = json.RootElement;
        var cues = root.GetProperty("cues").EnumerateArray().Select(c => new Cue(
            Seconds(c.GetProperty("start")), Seconds(c.GetProperty("end")), c.GetProperty("text").GetString() ?? "")).ToArray();
        return (root.GetProperty("total").GetInt32(), cues);
    }
    private static double Seconds(JsonElement value) => value.ValueKind == JsonValueKind.Number ? value.GetDouble()
        : TimeSpan.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var span) ? span.TotalSeconds : 0;
    private static string Time(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    private void RenderProgress()
    {
        if (_progress is not { } progress) return;
        var model = _task?["activity"].Text("model") ?? "";
        model = model switch { "Whisper Base" => L.T("Whisper · 标准"), "Whisper Tiny" => L.T("Whisper · 轻量"), "Whisper Small" => L.T("Whisper · Small"), _ => model };
        _name.Text = _task.Text("name") + (_mobile ? "" : " · " + L.T("自动字幕"));
        _thumb.Path = _task?["inputs"].Items().FirstOrDefault()?.ToString();
        _meta.Text = string.Join(" · ", new[] { L.T("自动字幕"), model, progress.Backend }.Where(s => !string.IsNullOrWhiteSpace(s)));
        _pills.Children.Clear();
        if (model.Length > 0) _pills.Children.Add(Ui.Pill(Ui.Text(model, "caption", raw: true), Palette.Fill));
        if (!string.IsNullOrWhiteSpace(progress.Backend))
        {
            var backend = Ui.Text(progress.Backend, "caption", raw: true);
            var accelerated = progress.Backend.StartsWith("GPU", StringComparison.OrdinalIgnoreCase);
            backend.Foreground = accelerated ? Palette.GreenText : Palette.Secondary;
            _pills.Children.Add(Ui.Pill(backend, accelerated ? Palette.GreenSoft : Palette.Fill));
        }
        _percent.Text = progress.Percent.ToString("0", CultureInfo.InvariantCulture) + "%";
        var speech = progress.Speech;
        _activity.Text = L.T(progress.Stage);
        _time.Text = "";
        if (speech is not null)
        {
            var recognized = speech.Segments.Sum(s => Math.Clamp(s.RecognizedSeconds, 0, Math.Max(0, s.EndSeconds - s.StartSeconds)));
            _time.Text = ShortTime(recognized) + " / " + ShortTime(speech.DurationSeconds);
            if (!_mobile) _activity.Text += " · " + L.F("检测到 {0} 个语音区间", (speech.Segments.FirstOrDefault(s => s.Index == speech.CurrentSegment)?.VoiceIntervalCount ?? 0));
        }
        ToolTip.SetTip(_activity, string.Join(" · ", new[] { speech?.SubStage, progress.Detail }.Where(s => !string.IsNullOrWhiteSpace(s))));
        _queue.Text = L.F("语音识别：{0} 个运行中，{1} 个等待中。", progress.Queue.SubtitleJobsRunning, progress.Queue.SubtitleJobsWaiting);
        ToolTip.SetTip(_percent, _queue.Text);
        if (_task.Text("error") is { Length: > 0 } error) { _error.Text = error; _error.IsVisible = true; }
        var key = JsonSerializer.Serialize(progress.Nodes, SubtitleJsonContext.Default.AiStageNodeDtoArray);
        if (_stageKey != key)
        {
            _stageKey = key; _stages.Children.Clear();
            var grid = new Grid { ColumnDefinitions = new(string.Join(",", progress.Nodes.Select(_ => "*"))), ColumnSpacing = 8 };
            foreach (var (node, index) in progress.Nodes.Select((node, index) => (node, index)))
            {
                var stage = new StackPanel { Spacing = 6 };
                stage.Children.Add(new Border { Height = 5, CornerRadius = new(3), Background = StageBrush(node.State) });
                var title = Ui.Text(node.Title); title.FontSize = _mobile ? 10.5 : 11.5; title.TextWrapping = TextWrapping.Wrap;
                title.Foreground = node.State == "pending" ? Palette.Secondary : Palette.Text;
                if (node.State == "running") title.FontWeight = FontWeight.Bold;
                stage.Children.Add(title);
                ToolTip.SetTip(stage, string.Join("\n", node.SubSteps.Select(L.T).Append(L.T(node.Detail))));
                Grid.SetColumn(stage, index); grid.Children.Add(stage);
            }
            _stages.Children.Add(grid);
        }
        RenderActions();
    }

    private void RenderActions()
    {
        var key = _mobile + ":" + _state + ":" + (_cues.Count > 0 && _cues.Count == _total) + ":" + _task.Text("output");
        if (key == _actionsKey) return; _actionsKey = key; _actions.Children.Clear(); _actions.ColumnDefinitions.Clear();
        var actions = _state switch
        {
            "Waiting" => new[] { ("start", "开始", "circle-play"), ("pause", "暂停", "pause"), ("stop", "停止", "square") },
            "Running" => new[] { ("pause", "暂停", "pause"), ("stop", "停止", "square") },
            "Paused" => new[] { ("resume", "恢复", "circle-play"), ("stop", "停止", "square") },
            "Failed" or "Cancelled" => new[] { ("retry", "重试", "rotate-ccw") }, _ => []
        };
        foreach (var (action, label, icon) in actions)
        {
            var button = Button(label, async () =>
            {
                await RemoteApi.Call("control_task", new() { ["taskId"] = _id, ["action"] = action });
                if (action == "retry") { _resultsEpoch++; _cues.Clear(); _total = 0; }
                await Refresh();
            }, icon);
            if (action == "stop")
            {
                button.Content = Ui.Row(6, Ui.Icon(icon, 14, Palette.Red), new TextBlock { Text = L.T(label), Foreground = Palette.Red });
            }
            button.HorizontalAlignment = _mobile ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            button.VerticalAlignment = VerticalAlignment.Center; button.Height = _mobile ? 44 : 28;
            if (_mobile) { button.Height = 44; button.FontSize = 15; }
            _actions.ColumnDefinitions.Add(new ColumnDefinition(_mobile ? GridLength.Star : GridLength.Auto));
            Grid.SetColumn(button, _actions.Children.Count); _actions.Children.Add(button);
        }
        if (_state is "Completed" or "Failed" or "Cancelled")
        {
            if (_cues.Count > 0 && _cues.Count == _total)
                _actions.Children.Add(Button("下载 SRT", () =>
                {
                    var srt = new StringBuilder(); var index = 0;
                    foreach (var cue in _cues)
                        srt.Append(++index).Append('\n').Append(SrtTime(cue.Start)).Append(" --> ").Append(SrtTime(cue.End)).Append('\n').Append(cue.Text).Append("\n\n");
                    BrowserHost.DownloadText(Path.GetFileNameWithoutExtension(_task.Text("name")) + ".srt", srt.ToString());
                    return Task.CompletedTask;
                }, "download"));
            if (_task.Text("output").Length > 0)
                _actions.Children.Add(Button("下载输出文件", () => { BrowserHost.Open("/web/tasks/" + Uri.EscapeDataString(_id) + "/file?source=output&index=0&download=true"); return Task.CompletedTask; }, "download"));
            _actions.Children.Add(Button("查看任务结果", _details, "file-text"));
        }
        while (_actions.ColumnDefinitions.Count < _actions.Children.Count)
            _actions.ColumnDefinitions.Add(new ColumnDefinition(_mobile ? GridLength.Star : GridLength.Auto));
        for (var i = 0; i < _actions.Children.Count; i++) Grid.SetColumn(_actions.Children[i], i);
    }
    private static string ShortTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
    private static string SrtTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
    private static IBrush StageBrush(string state) => state switch
    { "done" => Palette.Green, "running" => Palette.Blue, "failed" => Palette.Red, _ => Palette.Hairline };
    private Button Button(string label, Func<Task> action, string icon)
    {
        var button = new Button { Content = Ui.Content(L.T(label), icon), HorizontalAlignment = HorizontalAlignment.Left };
        Avalonia.Automation.AutomationProperties.SetName(button, L.T(label));
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); }
            catch (Exception error) { _error.Text = error.Message; _error.IsVisible = true; }
            finally { button.IsEnabled = true; }
        };
        return button;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AiTaskProgressDto))]
[JsonSerializable(typeof(AiStageNodeDto[]))]
internal partial class SubtitleJsonContext : JsonSerializerContext;
