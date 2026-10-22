using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using AvaMedia.Controls;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop.MultiStage;

/// <summary>Tool content hosted by <see cref="MultiStageTaskWindow"/>: the result slot, output line and result actions.</summary>
internal interface IMultiStageToolContent : IDisposable
{
    Control Result { get; }
    Control Output { get; }
    /// <summary>Result actions placed before 关闭 (secondary) and after it (the tool's primary result action).</summary>
    IReadOnlyList<Button> LeadingActions { get; }
    IReadOnlyList<Button> TrailingActions { get; }
    string SourceTitle { get; }
    string SourceTooltip { get; }
    /// <summary>Files or folders the task reads; draggable from the header in every state.</summary>
    IReadOnlyList<string> InputPaths { get; }
    /// <summary>Produced files or folders that exist; the window offers them as drag chips only once the task completed.</summary>
    IReadOnlyList<string> OutputPaths { get; }
    /// <summary>Optional tool-owned preview shown above the file chips on the result page (e.g. subtitles over the picture, UISPEC 10.17).</summary>
    Control? ResultExtra => null;
    Task InitializeAsync();
    /// <summary>Refresh the slot; returns header detail, section note and runtime/plan summaries for the shell.</summary>
    MultiStageContentState Update(MultiStageTaskSnapshot snapshot);
}

internal sealed record MultiStageContentState(string SourceDetail, string ResultNote, string PlanSummary,
    string ErrorTitle, string ErrorRecovery, bool HasPrimaryResult);

/// <summary>Independent multi-stage task window (UISPEC 10.16, variant B). Closing never stops the task.</summary>
internal partial class MultiStageTaskWindow : TaskLifecycleWindow
{
    protected event EventHandler? ContentRefreshed;
    private readonly IMultiStageTask _task;
    private readonly IMultiStageToolContent _content;
    private MultiStageTaskShell _shell => Shell;
    protected readonly TaskPageViewModel ProcessingPage, ResultPage;
    private readonly Panel _pageOutput = new();
    private readonly TaskResultView _resultView;
    private readonly MultiStageLogView _log = new();
    private readonly TextBlock _runtimeValue = new(), _runtimeNote = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _pause, _resume, _stop, _rerunAll, _retry, _rerun, _viewLog, _cancel, _close, _start;
    private readonly ComboBox _mode = new() { Classes = { "ac" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private MultiStageTaskSnapshot? _snapshot;
    private MultiStageContentState? _contentState;
    private MultiStageTaskState? _shownState;
    private int _refreshPosted, _logRevision;
    private bool _closed, _busy, _userChoseSection;
    private string _actionError = "";
    private DateTime _logReadUtc;
    private readonly IMediaEngine? _engine;
    private readonly MsFileThumb _sourceThumb;
    private readonly ContentControl _outputLine = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _outputChips = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private string _inputSize = "";

    public MultiStageTaskWindow(IMultiStageTask task, IMultiStageToolContent content, string toolLabel, IMediaEngine? engine = null)
    {
        _task = task; _content = content; _engine = engine;
        Title = Localization.Format($"任务 · {Localization.Key(toolLabel)}");
        Width = 1240; Height = 800; MinWidth = 1000; MinHeight = 680;
        Bind(BackgroundProperty, new DynamicResourceExtension("UiCanvas"));

        _pause = LifecycleButton("暂停", "pause", async () => await task.PauseAsync());
        _resume = LifecycleButton("继续", "play", async () => await task.ResumeAsync());
        _stop = LifecycleButton("停止", "square", async () => await task.StopAsync()); _stop.Classes.Add("danger");
        _rerunAll = LifecycleButton("重新执行全部", "rotate-ccw", async () => await task.RerunAsync());
        _retry = LifecycleButton("从阶段重试", "rotate-ccw", async () => { if (_snapshot?.Commands.RetryStageId is { Length: > 0 } id) await task.RetryFromStageAsync(id); });
        _retry.Classes.Add("primary");
        _rerun = LifecycleButton("重新执行", "rotate-ccw", async () => await task.RerunAsync());
        foreach (var button in new[] { _pause, _resume, _stop, _rerunAll, _retry, _rerun }) _shell.HeaderActions.Children.Add(button);

        _viewLog = Ui.Button("查看日志", () => { _userChoseSection = true; _shell.ShowLog = true; _ = ReadLogAsync(); });
        _cancel = Ui.Button("取消", Close);
        _close = Ui.Button("关闭", Close);
        _start = Ui.Button("开始任务", () => _ = RunAsync(async () => { if (_mode.SelectedIndex == 1) Close(); else await task.StartAsync(); }));
        _start.Classes.Add("primary");
        _mode.ItemsSource = new[] { Localization.Text("立即开始"), Localization.Text("仅加入队列") }; _mode.SelectedIndex = 0;
        _mode.Bind(MinWidthProperty, new DynamicResourceExtension("AcTaskModeWidth"));
        Avalonia.Automation.AutomationProperties.SetName(_mode, Localization.Text("任务执行方式"));
        _mode.SelectionChanged += (_, _) => _start.Content = Localization.Text(_mode.SelectedIndex == 1 ? "加入队列" : "开始任务");
        StableLayout.Reserve(_start, ["开始任务", "加入队列"]);
        foreach (var button in content.LeadingActions) _shell.FooterActions.Children.Add(button);
        foreach (var control in new Control[] { _viewLog, _cancel, _mode, _close }) _shell.FooterActions.Children.Add(control);
        foreach (var button in content.TrailingActions) _shell.FooterActions.Children.Add(button);
        _shell.FooterActions.Children.Add(_start);
        // Footer actions are text-only per the shell design; opt out of the global text-to-icon template.
        foreach (var button in _shell.FooterActions.Children.OfType<Button>()) button.Classes.Add("text-action");

        _shell.SourceTitle = content.SourceTitle;
        Localization.SetIsUserText(_shell.SourceTitleBlock, true); ToolTip.SetTip(_shell.SourceTitleBlock, content.SourceTooltip);
        _shell.Stages.Title = Localization.Text("处理阶段");
        _shell.SetSectionLabels(Localization.Text("结果"), Localization.Text("处理日志"));
        _shell.ResultContent = content.Result; _shell.LogContent = _log;
        _outputLine.Content = content.Output;
        _pageOutput.Children.Add(_outputLine); _pageOutput.Children.Add(_outputChips);
        _sourceThumb = new MsFileThumb(40, 40, engine);
        BuildSourceChip();
        _shell.Stages.Footer = BuildRuntime();
        _shell.SectionChanged += (_, _) => { _userChoseSection = true; if (_shell.ShowLog) _ = ReadLogAsync(); RefreshNote(); };
        _shell.Children.Remove(_shell.ProcessingContent);
        _shell.ProcessingContent.Margin = new Thickness(0);
        var actions = _shell.FooterActions.Children.ToArray(); _shell.FooterActions.Children.Clear();
        ProcessingPage = new("process", () => _shell.ProcessingContent) { LeadingContent = _pageOutput, AdditionalActions = actions };
        _resultView = new TaskResultView(engine) { Extra = content.ResultExtra };
        ResultPage = new("done", () => _resultView) { SecondaryAction = new("关闭", () => { Close(); return Task.CompletedTask; }) };
        ViewedPageChanged += (_, _) => { RefreshResult(); if (_snapshot is { } snapshot) RefreshHeader(snapshot); };
        SetLifecycle(new ProcessingTaskPages(task), [ProcessingPage, ResultPage]);

        task.Changed += TaskChanged;
        _clock.Tick += (_, _) => { if (!_closed) { RefreshClock(); if (_shell.ShowLog && _snapshot?.State is MultiStageTaskState.Running && DateTime.UtcNow - _logReadUtc > TimeSpan.FromSeconds(2)) _ = ReadLogAsync(); } };
        Opened += async (_, _) => { _clock.Start(); try { await content.InitializeAsync(); } catch (Exception error) { AppDiagnostics.Record("Multi-stage task content", error); } };
        Closed += (_, _) =>
        {
            _closed = true; _clock.Stop(); task.Changed -= TaskChanged;
            _resultView.Dispose(); content.Dispose(); task.Dispose();
        };
        Localization.Changed += LanguageChanged;
        Closed += (_, _) => Localization.Changed -= LanguageChanged;
        Refresh();
    }

    /// <summary>Header source as a file chip: thumbnail + name + size, draggable (copy) in every state.</summary>
    private void BuildSourceChip()
    {
        var inputs = _content.InputPaths.Where(path => path.Length > 0).ToArray();
        if (inputs.Length == 0) return;
        _sourceThumb.SetPath(inputs[0]);
        _shell.SourceLeading = _sourceThumb;
        _inputSize = inputs.Length == 1 ? MsFileChip.FileSize(inputs[0]) : Localization.Format($"{inputs.Length} 个来源");
        _shell.SourceArea.Classes.Add("ms-file-drag");
        _shell.SourceArea.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        MsFileDrag.Attach(_shell.SourceArea, () => _content.InputPaths, () => new FileDragVisual(_sourceThumb.Thumbnail, _sourceThumb.Icon));
        ToolTip.SetTip(_shell.SourceArea, Localization.Format($"拖拽源文件到工具或其他应用 · {_content.SourceTooltip}"));
    }

    /// <summary>Finished outputs replace the "输出到" line with draggable file chips.</summary>
    private void RefreshOutputChips(MultiStageTaskSnapshot snapshot)
    {
        var outputs = snapshot.State == MultiStageTaskState.Completed ? _content.OutputPaths.Where(path => File.Exists(path) || Directory.Exists(path)).Take(3).ToArray() : [];
        var chips = _outputChips.Children.OfType<MsFileChip>().ToArray();
        if (!chips.Select(chip => chip.Path).SequenceEqual(outputs))
        {
            _outputChips.Children.Clear();
            foreach (var output in outputs) { var chip = new MsFileChip(_engine); chip.SetFile(output); _outputChips.Children.Add(chip); }
        }
        _outputChips.IsVisible = outputs.Length > 0;
        _outputLine.IsVisible = outputs.Length == 0;
    }

    private void RefreshResult()
    {
        if (Lifecycle is null) return;
        var paths = _content.OutputPaths.Where(path => File.Exists(path) || Directory.Exists(path)).ToArray();
        _resultView.Update(paths, Lifecycle.Pages, _contentState?.ResultNote ?? "");
        ResultPage.Hint = paths.Length > 0 ? Localization.Text("输出文件可拖拽到其他应用") : _contentState?.ResultNote ?? "";
        ResultPage.PrimaryAction = paths.Length > 0 ? new("打开", () =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(paths[0]) { UseShellExecute = true });
            return Task.CompletedTask;
        }) : null;
        ResultPage.AdditionalActions = paths.Length > 0
            ? [_resultView.RevealAction]
            : _content.TrailingActions.Cast<Control>().ToArray();
        RefreshPageActions();
    }

    private Control BuildRuntime()
    {
        var panel = new StackPanel { Spacing = 2 };
        var title = new TextBlock { Text = Localization.Text("运行环境"), Classes = { "ms-small" } };
        _runtimeValue.Classes.Add("ms-body"); _runtimeValue.FontSize = 12; _runtimeValue.FontWeight = FontWeight.Medium;
        // Real runtimes can be long ("Whisper Base · Whisper: GPU 优先 · CUDA …"); wrap to two lines in the narrow stage column.
        _runtimeValue.TextWrapping = TextWrapping.Wrap; _runtimeValue.MaxLines = 2; _runtimeValue.TextTrimming = TextTrimming.CharacterEllipsis;
        _runtimeNote.Classes.Add("ms-small");
        panel.Children.Add(title); panel.Children.Add(_runtimeValue); panel.Children.Add(_runtimeNote);
        return panel;
    }

    private Button LifecycleButton(string text, string icon, Func<Task> action)
    {
        var button = new Button { Classes = { "ac" } };
        var glyph = new LucideIcon { Kind = icon, Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center };
        glyph.Bind(LucideIcon.ForegroundProperty, button.GetObservable(ForegroundProperty));
        var label = new TextBlock { Text = Localization.Text(text), VerticalAlignment = VerticalAlignment.Center };
        button.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { glyph, label } };
        button.Tag = label;
        button.Click += (_, _) => _ = RunAsync(action);
        return button;
    }
    private static void SetLabel(Button button, string text) { if (button.Tag is TextBlock label) label.Text = text; }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _closed) return;
        _busy = true; _actionError = ""; RefreshActions();
        try { await action(); }
        catch (Exception error) { _actionError = error.Message; }
        finally { _busy = false; if (!_closed) Refresh(); }
    }

    private void TaskChanged(object? sender, EventArgs args)
    {
        if (_closed || Interlocked.Exchange(ref _refreshPosted, 1) != 0) return;
        Dispatcher.UIThread.Post(() => { Interlocked.Exchange(ref _refreshPosted, 0); if (!_closed) Refresh(); }, DispatcherPriority.Background);
    }
    private void LanguageChanged(object? sender, EventArgs args) { _snapshot = null; Refresh(); }

    private void Refresh()
    {
        var snapshot = _task.Snapshot;
        var stateChanged = _shownState != snapshot.State;
        _snapshot = snapshot;
        var content = _contentState = _content.Update(snapshot);
        _shell.SourceDetail = Localization.Join(" · ", new[] { _inputSize, content.SourceDetail }.Where(text => text.Length > 0));
        RefreshOutputChips(snapshot);
        RefreshResult();
        RefreshHeader(snapshot);
        RefreshStages(snapshot);
        var runtime = snapshot.Runtime;
        _runtimeValue.Text = Localization.Join(" · ", new[] { runtime.Model, runtime.Backend }.Where(s => s.Length > 0));
        _runtimeValue.IsVisible = _runtimeValue.Text.Length > 0; ToolTip.SetTip(_runtimeValue, _runtimeValue.IsVisible ? _runtimeValue.Text : null);
        _runtimeNote.Text = runtime.Note.Length > 0 ? Localization.Text(runtime.Note)
            : snapshot.State == MultiStageTaskState.Configuring && runtime.Backend.Length == 0 ? Localization.Text("开始后显示实际推理后端") : "";
        _runtimeNote.IsVisible = _runtimeNote.Text.Length > 0;
        var reason = _actionError.Length > 0 ? _actionError : snapshot.State == MultiStageTaskState.Failed ? snapshot.Error : "";
        _shell.SetError(reason.Length > 0 && _actionError.Length == 0 ? content.ErrorTitle : "", reason, _actionError.Length == 0 ? content.ErrorRecovery : "");
        _shell.IsLogEnabled = snapshot.State != MultiStageTaskState.Configuring;
        if (stateChanged)
        {
            _shownState = snapshot.State;
            if (!_userChoseSection || snapshot.State == MultiStageTaskState.Configuring) _shell.ShowLog = snapshot.State == MultiStageTaskState.Cancelled;
            _userChoseSection = false;
            ++_logRevision; if (_shell.ShowLog || _log.Count > 0) _ = ReadLogAsync();
        }
        RefreshNote(content);
        RefreshClock();
        RefreshActions(content);
        ContentRefreshed?.Invoke(this, EventArgs.Empty);
        RefreshPageActions();
    }

    private void RefreshHeader(MultiStageTaskSnapshot snapshot)
    {
        var processingCompleted = ViewedPageId == "process"
            && Lifecycle?.Pages.FirstOrDefault(page => page.Page.Id == "process")?.State == PageState.Completed;
        var state = processingCompleted ? MultiStageTaskState.Completed : snapshot.State;
        var (pill, pillText) = state switch
        {
            MultiStageTaskState.Configuring => ("waiting", "待开始"),
            MultiStageTaskState.AwaitingUser => ("running", "等待你操作"),
            MultiStageTaskState.Running => ("running", "进行中"),
            MultiStageTaskState.Paused => ("paused", "已暂停"),
            MultiStageTaskState.Stopping => ("running", "正在停止"),
            MultiStageTaskState.Failed => ("failed", "失败"),
            MultiStageTaskState.Completed => ("completed", "已完成"),
            _ => ("waiting", "已取消")
        };
        _shell.SetStatus(pill, Localization.Text(pillText));
        _shell.ProgressText = state is MultiStageTaskState.Configuring or MultiStageTaskState.Completed or MultiStageTaskState.AwaitingUser ? ""
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(Math.Clamp(snapshot.Progress, 0, 100)):0}%");
    }

    private void RefreshStages(MultiStageTaskSnapshot snapshot)
    {
        var now = DateTime.UtcNow;
        var items = snapshot.Stages.Select(stage =>
        {
            var state = stage.State switch
            {
                StageState.Active => "active", StageState.Paused => "paused", StageState.Done => "done", StageState.Failed => "failed",
                StageState.Skipped => "skipped", StageState.Cancelled => "cancelled", _ => "pending"
            };
            var elapsed = stage.Elapsed is { } value ? value + (stage.IsClockRunning ? Max(now - snapshot.SnapshotUtc) : TimeSpan.Zero) : (TimeSpan?)null;
            var time = elapsed is not { } spent ? "" : stage.State == StageState.Done
                ? Localization.Format($"{Seconds(spent.TotalSeconds)} 秒")
                : string.Create(CultureInfo.InvariantCulture, $"{(int)spent.TotalMinutes:00}:{spent.Seconds:00}");
            var detail = stage.Detail;
            if (stage.State is StageState.Active or StageState.Paused && detail.Length == 0 && stage.Current is { } current && stage.Total is > 0)
                detail = Localization.Format($"{current:0} / {stage.Total:0} {stage.Unit}") + (stage.Fraction is { } f ? $" · {f:0%}" : "");
            return new StageListItem(Localization.Text(stage.Stage.Title), state, Localization.Text(detail), time)
            {
                Progress = stage.State is StageState.Active or StageState.Paused ? stage.Fraction : null,
                IsIndeterminate = stage.State == StageState.Active && stage.Fraction is null
            };
        }).ToArray();
        var finished = snapshot.Stages.Count(stage => stage.State is StageState.Done or StageState.Skipped);
        _shell.Stages.Update(items, $"{finished} / {snapshot.Stages.Count}");
    }

    private void RefreshClock()
    {
        if (_snapshot is not { } snapshot) return;
        if (Lifecycle is TaskLifecycle lifecycle)
        {
            var saved = lifecycle.Snapshot;
            var active = saved.Pages.Sum(page => page.Elapsed.TotalSeconds)
                + (saved.RunningSinceUtc is { } since ? Math.Max(0, (DateTime.UtcNow - since).TotalSeconds) : 0);
            _shell.SetClock(Localization.Text("已用时"), AiActivity.FormatElapsed(active),
                saved.AwaitingUser ? Localization.Text("等待期间不计时") : "");
            return;
        }
        var elapsed = snapshot.Elapsed + (snapshot.IsClockRunning ? Max(DateTime.UtcNow - snapshot.SnapshotUtc) : TimeSpan.Zero);
        var clock = AiActivity.FormatElapsed(elapsed.TotalSeconds);
        var interrupted = snapshot.Stages.FirstOrDefault(stage => stage.Stage.Id == snapshot.InterruptedStageId)?.Stage.Title ?? "";
        switch (snapshot.State)
        {
            case MultiStageTaskState.Configuring:
                _shell.SetClock(Localization.Text("尚未开始"), "", _contentState?.PlanSummary ?? ""); break;
            case MultiStageTaskState.Running or MultiStageTaskState.Stopping:
                _shell.SetClock(Localization.Text("已用时"), clock, snapshot.Remaining is { } left && left > TimeSpan.Zero ? Localization.Format($"剩余约 {Remaining(left)}") : ""); break;
            case MultiStageTaskState.Paused:
                _shell.SetClock(Localization.Text("已用时"), clock, Localization.Text("已暂停")); break;
            case MultiStageTaskState.Failed:
                _shell.SetClock(Localization.Text("已用时"), clock, interrupted.Length > 0 ? Localization.Format($"中断于 {Localization.Text(interrupted)}") : ""); break;
            case MultiStageTaskState.Cancelled:
                _shell.SetClock(Localization.Text("已用时"), clock, interrupted.Length > 0 ? Localization.Format($"取消于 {Localization.Text(interrupted)}") : ""); break;
            default:
                _shell.SetClock(Localization.Text("用时"), clock, snapshot.CompletedUtc is { } done ? Finished(done.ToLocalTime()) : ""); break;
        }
        if (snapshot.State is MultiStageTaskState.Running) RefreshStages(snapshot);
    }

    private void RefreshNote(MultiStageContentState? content = null)
    {
        content ??= _contentState;
        if (content is null) return;
        _shell.SectionNote = _shell.ShowLog ? Localization.Format($"{_log.Count} 条记录") : content.ResultNote;
    }

    private void RefreshActions(MultiStageContentState? content = null)
    {
        if (_snapshot is not { } snapshot) return;
        var commands = snapshot.Commands; var state = snapshot.State;
        _pause.IsVisible = state == MultiStageTaskState.Running && commands.CanPause;
        _resume.IsVisible = state == MultiStageTaskState.Paused && commands.CanResume;
        _stop.IsVisible = (state is MultiStageTaskState.Running or MultiStageTaskState.Paused or MultiStageTaskState.Stopping or MultiStageTaskState.AwaitingUser) && (commands.CanStop || state == MultiStageTaskState.Stopping);
        _stop.IsEnabled = commands.CanStop && !_busy;
        var retryStage = snapshot.Stages.FirstOrDefault(stage => stage.Stage.Id == commands.RetryStageId)?.Stage.Title ?? "";
        _retry.IsVisible = state == MultiStageTaskState.Failed && retryStage.Length > 0;
        SetLabel(_retry, Localization.Format($"从{Localization.Text(retryStage)}重试"));
        _rerunAll.IsVisible = _retry.IsVisible && commands.CanRerun;
        _rerun.IsVisible = !_retry.IsVisible && state is MultiStageTaskState.Failed or MultiStageTaskState.Cancelled or MultiStageTaskState.Completed && commands.CanRerun;
        _rerun.Classes.Set("primary", state is MultiStageTaskState.Failed or MultiStageTaskState.Cancelled);
        foreach (var button in new[] { _pause, _resume, _stop, _rerunAll, _retry, _rerun }) button.IsEnabled = !_busy && (button != _stop || commands.CanStop);
        var configuring = state == MultiStageTaskState.Configuring;
        _cancel.IsVisible = _mode.IsVisible = _start.IsVisible = configuring;
        _start.IsEnabled = _mode.IsEnabled = !_busy && commands.CanStart;
        _close.IsVisible = !configuring;
        _viewLog.IsVisible = state == MultiStageTaskState.Failed && !_shell.ShowLog;
    }

    private async Task ReadLogAsync()
    {
        var revision = ++_logRevision; _logReadUtc = DateTime.UtcNow;
        try
        {
            var entries = await _task.ReadLogAsync();
            if (_closed || revision != _logRevision) return;
            _log.Update(entries); RefreshNote(); RefreshActions();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { if (!_closed) { _actionError = error.Message; Refresh(); } }
    }

    private static TimeSpan Max(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
    internal static string Seconds(double seconds) => seconds < 10 ? seconds.ToString("0.0", CultureInfo.InvariantCulture) : seconds.ToString("0.#", CultureInfo.InvariantCulture);
    private static string Remaining(TimeSpan left) => left.TotalSeconds < 60 ? Localization.Format($"{Math.Ceiling(left.TotalSeconds):0} 秒")
        : left.TotalMinutes < 60 ? Localization.Format($"{Math.Ceiling(left.TotalMinutes):0} 分钟") : Localization.Format($"{left.TotalHours:0.#} 小时");
    private static string Finished(DateTime local)
    {
        var day = local.Date == DateTime.Today ? Localization.Text("今天") : local.Date == DateTime.Today.AddDays(-1) ? Localization.Text("昨天") : local.ToString("M/d", CultureInfo.InvariantCulture);
        return Localization.Format($"{day} {local:HH:mm} 完成");
    }
}
