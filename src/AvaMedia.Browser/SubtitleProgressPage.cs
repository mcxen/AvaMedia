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
    private readonly Grid _layout = new() { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 12, Margin = new(18, 2, 18, 18) };
    private readonly Grid _body = new() { ColumnDefinitions = new("*,260"), ColumnSpacing = 12, RowSpacing = 12 };
    private readonly StackPanel _heading = new() { Spacing = 12 }, _stages = new() { Spacing = 8 };
    private readonly WrapStack _actions = new() { Spacing = 8 };
    private readonly TextBlock _name = Ui.Text("", "title", raw: true, trim: true), _meta = Ui.Text("", "secondary", raw: true),
        _percent = Ui.Text("", "largeTitle", raw: true), _activity = Ui.Text("", raw: true), _queue = Ui.Text("", "secondary", raw: true),
        _count = Ui.Text("", "secondary", raw: true), _error = Ui.Text("", raw: true), _log = Ui.Text("", raw: true),
        _empty = Ui.Text("暂无字幕。", "secondary");
    private readonly SpeechTimeline _timeline = new();
    private readonly ListBox _transcript;
    private readonly CheckBox _follow = new() { Content = "跟随最新字幕", IsChecked = true };
    private readonly Border _resultCard, _logCard;
    private readonly ScrollViewer _headingScroll;
    private JsonNode? _task;
    private AiTaskProgressDto? _progress;
    private string _state = "", _stageKey = "", _actionsKey = "";
    private bool _loading, _mobile, _stacked, _waveformRequested;
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
            ItemTemplate = new FuncDataTemplate<Cue>((cue, _) => CueRow(cue!)) };
        _transcript.Styles.Add(new Style(s => s.OfType<ListBoxItem>())
        { Setters = { new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        // ListBox's default VirtualizingStackPanel realizes only the visible cue rows.
        var result = new Grid { RowDefinitions = new("Auto,Auto,Auto,*"), RowSpacing = 8 };
        result.Children.Add(Ui.Row(10, Ui.Text("字幕结果", "strong"), _count));
        Grid.SetRow(_follow, 1); result.Children.Add(_follow);
        Grid.SetRow(_empty, 2); result.Children.Add(_empty);
        Grid.SetRow(_transcript, 3); result.Children.Add(_transcript);
        _resultCard = Ui.Card(result, new(14));
        _log.TextWrapping = TextWrapping.Wrap; _log.FontFamily = new("monospace"); _log.FontSize = 12;
        var logPanel = new StackPanel { Spacing = 12 };
        logPanel.Children.Add(Ui.Text("处理日志（最近）", "strong")); logPanel.Children.Add(_log);
        _logCard = Ui.Card(new ScrollViewer { Content = logPanel }, new(14));
        _body.Children.Add(_resultCard); Grid.SetColumn(_logCard, 1); _body.Children.Add(_logCard);
        var title = new StackPanel { Spacing = 4 }; title.Children.Add(_name); title.Children.Add(_meta);
        _meta.TextWrapping = TextWrapping.Wrap;
        var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        header.Children.Add(title); Grid.SetColumn(_percent, 1); header.Children.Add(_percent);
        _heading.Children.Add(Button("所有任务", () => { _back(); return Task.CompletedTask; }, "chevron-left"));
        _heading.Children.Add(header); _heading.Children.Add(_stages);
        _activity.TextWrapping = _queue.TextWrapping = _error.TextWrapping = TextWrapping.Wrap;
        _heading.Children.Add(new Border { Background = Palette.BlueSoft, CornerRadius = new(9), Padding = new(12, 8), Child = _activity });
        _heading.Children.Add(_timeline); _heading.Children.Add(_queue); _heading.Children.Add(_error);
        _error.Foreground = Palette.Red; _error.IsVisible = _queue.IsVisible = false;
        _headingScroll = new ScrollViewer { Content = Ui.Card(_heading, new(14)), MaxHeight = 330 };
        _layout.Children.Add(_headingScroll); Grid.SetRow(_body, 1); _layout.Children.Add(_body);
        var footer = new Border { Child = _actions, Padding = new(14, 10), Background = Palette.Card, CornerRadius = new(14) };
        Grid.SetRow(footer, 2); _layout.Children.Add(footer); Content = _layout;
        _timer.Tick += async (_, _) => await Refresh();
        AttachedToVisualTree += async (_, _) =>
        {
            _lifetime = new(); _generation++; _lastRefresh = default;
            if (_timeline.Envelope is null) _waveformRequested = false;
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
        if (_mobile == mobile && _stacked == stacked && _body.RowDefinitions.Count > 0) return;
        _mobile = mobile; _stacked = stacked;
        _layout.Margin = new(0);
        _body.ColumnDefinitions = new(stacked ? "*" : "*,240");
        _body.RowDefinitions = new(stacked ? "3*,*" : "*");
        Grid.SetColumn(_logCard, stacked ? 0 : 1); Grid.SetRow(_logCard, stacked ? 1 : 0);
        _headingScroll.MaxHeight = mobile ? 310 : 330;
        // Recreate only the realized templates when crossing the responsive breakpoint.
        _transcript.ItemTemplate = new FuncDataTemplate<Cue>((cue, _) => CueRow(cue!));
    }

    private Control CueRow(Cue cue)
    {
        var time = Ui.Text(Time(cue.Start) + " – " + Time(cue.End), "caption", raw: true);
        time.FontFamily = new("monospace"); time.VerticalAlignment = VerticalAlignment.Top;
        var text = Ui.Text(cue.Text, raw: true); text.TextWrapping = TextWrapping.Wrap;
        var row = new Grid { ColumnDefinitions = new(_mobile ? "*" : "172,*"), RowDefinitions = new(_mobile ? "Auto,Auto" : "Auto"),
            ColumnSpacing = 12, RowSpacing = 4, Margin = new(0, 9) };
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
            if (result.Cues.Length > 0 && _follow.IsChecked == true && _cues.Count > 0) _transcript.ScrollIntoView(_cues[^1]);
            _empty.IsVisible = _cues.Count == 0;
            _count.Text = L.F("已识别 {0} 条", _total);
            var log = (await logTask).Text("text");
            _log.Text = log.Length > 0 ? log : L.T("暂无日志。");
            _error.IsVisible = false;
            RenderProgress();
            // Request the envelope once, independently of progress polling.
            if (!_waveformRequested) { _waveformRequested = true; _ = LoadWaveform(generation, ct); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation == _generation && !ct.IsCancellationRequested)
            { _error.Text = L.F("进度暂时无法刷新：{0}", error.Message); _error.IsVisible = true; }
        }
        finally { _loading = false; _lastRefresh = DateTime.UtcNow; }
    }

    private async Task LoadWaveform(long generation, CancellationToken ct)
    {
        try
        {
            using var response = await Client.GetAsync(Base + "waveform?buckets=600", ct);
            if (!response.IsSuccessStatusCode) return;
            var envelope = await response.Content.ReadFromJsonAsync(SubtitleJsonContext.Default.AudioEnvelope, ct);
            if (generation != _generation || ct.IsCancellationRequested) return;
            _timeline.Envelope = envelope; _timeline.InvalidateVisual();
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException) { }
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
        _name.Text = _task.Text("name");
        _meta.Text = string.Join(" · ", new[] { L.T("自动字幕"), _task?["activity"].Text("model"), progress.Backend,
            L.T(StateName(_state)), _task.Text("submittedBy") }.Where(s => !string.IsNullOrWhiteSpace(s)));
        _percent.Text = progress.Percent.ToString("0", CultureInfo.InvariantCulture) + "%";
        var speech = progress.Speech;
        _activity.Text = L.T(speech?.SubStage ?? progress.Stage);
        if (!string.IsNullOrWhiteSpace(progress.Detail)) _activity.Text += " · " + L.T(progress.Detail);
        if (speech is not null)
        {
            var recognized = speech.Segments.Sum(s => Math.Clamp(s.RecognizedSeconds, 0, Math.Max(0, s.EndSeconds - s.StartSeconds)));
            _activity.Text += " · " + Time(recognized) + " / " + Time(speech.DurationSeconds);
            _activity.Text += " · " + L.F("检测到 {0} 个语音区间", speech.VoiceIntervals.Length);
            if (speech.CurrentSegment >= 0) _activity.Text += " · " + L.F("第 {0}/{1} 段", speech.CurrentSegment + 1, speech.Segments.Length);
        }
        _timeline.Speech = speech; _timeline.IsVisible = speech is not null || _timeline.Envelope is not null; _timeline.InvalidateVisual();
        _queue.Text = L.F("语音识别：{0} 个运行中，{1} 个等待中。", progress.Queue.SubtitleJobsRunning, progress.Queue.SubtitleJobsWaiting);
        _queue.IsVisible = progress.Queue.SubtitleJobsWaiting > 0;
        if (_task.Text("error") is { Length: > 0 } error) { _error.Text = error; _error.IsVisible = true; }
        var key = JsonSerializer.Serialize(progress.Nodes, SubtitleJsonContext.Default.AiStageNodeDtoArray);
        if (_stageKey != key)
        {
            _stageKey = key; _stages.Children.Clear();
            var grid = new Grid { ColumnDefinitions = new(string.Join(",", progress.Nodes.Select(_ => "*"))), ColumnSpacing = 8 };
            foreach (var (node, index) in progress.Nodes.Select((node, index) => (node, index)))
            {
                var stage = new StackPanel { Spacing = 6 };
                stage.Children.Add(new Border { Height = 5, CornerRadius = new(3), Background = SpeechTimeline.StateBrush(node.State) });
                var title = Ui.Text(node.Title); title.FontSize = 12; title.TextWrapping = TextWrapping.Wrap;
                title.Foreground = node.State == "pending" ? Palette.Secondary : Palette.Text;
                if (node.State == "running") title.FontWeight = FontWeight.Bold;
                stage.Children.Add(title);
                if (node.ElapsedSeconds is { } elapsed) stage.Children.Add(Ui.Text(L.F("{0:0.0} 秒", elapsed), "caption"));
                ToolTip.SetTip(stage, string.Join("\n", node.SubSteps.Select(L.T).Append(L.T(node.Detail))));
                Grid.SetColumn(stage, index); grid.Children.Add(stage);
            }
            _stages.Children.Add(grid);
            var current = progress.Nodes.FirstOrDefault(n => n.State == "running");
            if (current?.SubSteps is { Length: > 0 })
                _stages.Children.Add(Ui.Text(string.Join(" · ", current.SubSteps.Select(L.T)), "caption"));
        }
        RenderActions();
    }

    private void RenderActions()
    {
        var key = _state + ":" + (_cues.Count > 0 && _cues.Count == _total) + ":" + _task.Text("output");
        if (key == _actionsKey) return; _actionsKey = key; _actions.Children.Clear();
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
            _actions.Children.Add(button);
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
    }
    private static string SrtTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
    private static string StateName(string state) => state switch { "Waiting" => "等待", "Running" => "运行中", "Paused" => "已暂停", "Stopping" => "停止中", "Completed" => "已完成", "Failed" => "失败", "Cancelled" => "已停止", _ => state };
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
[JsonSerializable(typeof(AudioEnvelope))]
[JsonSerializable(typeof(AiStageNodeDto[]))]
internal partial class SubtitleJsonContext : JsonSerializerContext;

/// <summary>Bounded drawing work: actual envelope peaks, voice intervals and recognition segments.</summary>
internal sealed class SpeechTimeline : Control
{
    public AudioEnvelope? Envelope { get; set; }
    public SpeechProgressSnapshot? Speech { get; set; }
    public SpeechTimeline() { Height = 76; ClipToBounds = true; }
    internal static IBrush StateBrush(string state) => state switch
    { "done" => Palette.Green, "running" => Palette.Blue, "failed" => Palette.Red, "empty" => Palette.FillHover, _ => Palette.Hairline };
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var duration = Speech?.DurationSeconds ?? Envelope?.DurationSeconds ?? 0;
        if (duration <= 0 || Bounds.Width <= 0) return;
        double X(double seconds) => Math.Clamp(seconds / duration, 0, 1) * Bounds.Width;
        if (Speech is { } speech)
        {
            foreach (var interval in speech.VoiceIntervals)
                context.FillRectangle(Palette.BlueSoft, new Rect(X(interval.StartSeconds), 0, Math.Max(0, X(interval.EndSeconds) - X(interval.StartSeconds)), 48));
            foreach (var segment in speech.Segments)
            {
                var left = X(segment.StartSeconds); var width = Math.Max(0, X(segment.EndSeconds) - left - 2);
                context.FillRectangle(StateBrush(segment.State), new Rect(left, 56, width, 6));
                if (segment.State == "running") context.FillRectangle(Palette.Green, new Rect(left, 56,
                    width * Math.Clamp(segment.RecognizedSeconds / Math.Max(.001, segment.EndSeconds - segment.StartSeconds), 0, 1), 6));
            }
        }
        if (Envelope is { Peaks.Length: > 0 } envelope)
        {
            var bars = Math.Min(envelope.Peaks.Length, Math.Max(1, (int)(Bounds.Width / 3)));
            for (var i = 0; i < bars; i++)
            {
                var peak = 0f;
                for (var j = i * envelope.Peaks.Length / bars; j < (i + 1) * envelope.Peaks.Length / bars; j++) peak = Math.Max(peak, envelope.Peaks[j]);
                var height = Math.Max(1, Math.Clamp(peak, 0, 1) * 44);
                context.FillRectangle(Palette.Blue, new Rect(i * Bounds.Width / bars, (48 - height) / 2, Math.Max(1, Bounds.Width / bars - 1), height));
            }
        }
    }
}
