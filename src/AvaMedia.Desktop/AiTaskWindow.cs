using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed partial class AiTaskWindow : Window
{
    private readonly Job _job;
    private readonly TextBlock _status = Ui.Text("", "caption"), _error = Ui.Text("", "caption");
    private readonly TextBlock _summary = Ui.Text("", "caption");
    private readonly AiActivityView _activity = new() { Compact = true };
    private readonly Button _result, _stop, _run, _pause;
    private readonly Func<bool> _hasResult, _canRun;
    private readonly RotationTaskView? _rotation;
    private readonly Expander _records = new() { Header = "处理记录", IsExpanded = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly AiActivityView _details = new() { DetailHeight = 180 };
    private readonly TextBox _log = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        MinHeight = 64, MaxHeight = 120, BorderThickness = new(0), Background = Brushes.Transparent
    };
    private JobState? _shownLogState;
    private bool _closed, _busy;
    private int _refreshPosted, _logRevision;
    private string _actionError = "";
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public AiTaskWindow(Job job, Func<bool> hasResult, Func<Task> showResult, Action stop, Func<Task> run,
        Func<bool> canRun, Action newTask, RotationTaskView? rotation = null, Action? pause = null, Func<AiQueueSummary>? queueSummary = null,
        IMediaEngine? engine = null)
    {
        _job = job; _hasResult = hasResult; _canRun = canRun; _rotation = rotation; _queueSummary = queueSummary;
        Title = Localization.Format($"任务 · {Localization.Key(Catalog.Find(job.FeatureId).Label)}");
        Width = rotation is null ? 800 : 1060; Height = rotation is null ? 520 : 720;
        MinWidth = rotation is null ? 650 : 800; MinHeight = rotation is null ? 400 : 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Margin = new(12), RowSpacing = 8 };
        var heading = new Grid { RowDefinitions = new("Auto,Auto"), ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, RowSpacing = 4 };
        var source = Ui.Text(string.Join(" · ", job.Inputs.Select(Path.GetFileName)), "title");
        source.TextWrapping = TextWrapping.NoWrap; source.TextTrimming = TextTrimming.CharacterEllipsis;
        Localization.SetIsUserText(source, true); ToolTip.SetTip(source, string.Join("\n", job.Inputs));
        heading.Children.Add(source);
        var badge = new Border { Child = _status, Padding = new(10, 4), VerticalAlignment = VerticalAlignment.Center };
        badge.Bind(Border.BackgroundProperty, new DynamicResourceExtension("UiSurface"));
        badge.Bind(Border.CornerRadiusProperty, new DynamicResourceExtension("UiControlRadius"));
        Grid.SetColumn(badge, 1); heading.Children.Add(badge);
        Grid.SetRow(_error, 1); Grid.SetColumnSpan(_error, 2); heading.Children.Add(_error);
        _error.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiDanger"));
        root.Children.Add(heading);

        Grid.SetRow(_activity, 1); root.Children.Add(_activity);
        var body = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 8 };
        Grid.SetRow(body, 2); root.Children.Add(body);
        if (rotation is not null)
        {
            body.Children.Add(rotation);
            rotation.ReviewChanged += ReviewChanged;
        }
        else
        {
            var outcome = new StackPanel { Spacing = 6 };
            outcome.Children.Add(_summary); outcome.Children.Add(_details);
            if (job.FeatureId == "video-summary") BuildLongSummaryProgress(outcome);
            body.Children.Add(new ScrollViewer { Content = outcome, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        }
        var records = new StackPanel { Spacing = 8 };
        if (rotation is not null) records.Children.Add(_details);
        records.Children.Add(_log);
        _records.Content = new ScrollViewer { Content = records, MaxHeight = 200,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Localization.SetIsUserText(_log, true);
        _records.PropertyChanged += (_, args) =>
        {
            if (args.Property != Expander.IsExpandedProperty || !_records.IsExpanded) return;
            _rotation?.CollapseSettings(); _ = ReadLogAsync();
        };
        Grid.SetRow(_records, 1); body.Children.Add(_records);

        var footer = new Border { Padding = new(0, 8, 0, 0), BorderThickness = new(0, 1, 0, 0) };
        footer.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("UiDivider"));
        var actions = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        var taskActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        taskActions.Children.Add(Ui.Button("新建任务", newTask));
        _run = Ui.Button("开始任务", async () => await RunActionAsync(run)); taskActions.Children.Add(_run);
        _pause = Ui.Button("临时停止", () => pause?.Invoke()); taskActions.Children.Add(_pause);
        _pause.Tag = pause;
        _stop = Ui.Button("结束任务", stop); taskActions.Children.Add(_stop); actions.Children.Add(taskActions);
        var finishActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        finishActions.Children.Add(Ui.Button("关闭", Close));
        _result = Ui.Button("查看结果", async () => await RunActionAsync(showResult, closeOnSuccess: _rotation?.IsReview == true));
        _result.Classes.Add("primary"); _result.IsDefault = true; finishActions.Children.Add(_result);
        Grid.SetColumn(finishActions, 1); actions.Children.Add(finishActions);
        footer.Child = actions; Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        if (job.FeatureId == "person-clip" && engine is not null)
        {
            body.Children.Remove(_records); heading.Children.Remove(_error); root.Children.Remove(footer);
            BuildPersonProgress(engine, footer);
        }
        if (Catalog.Find(job.FeatureId).Operation == Operation.Transcribe) BuildSpeechProgress();
        job.PropertyChanged += Changed;
        Localization.Changed += LanguageChanged;
        Opened += async (_, _) => { if (_rotation is not null) await _rotation.InitializeAsync(); if (_personView is not null) await _personView.InitializeAsync(); };
        Closed += (_, _) =>
        {
            _closed = true; CloseSpeechProgress(); _personView?.Dispose(); _refreshTimer.Stop(); job.PropertyChanged -= Changed; Localization.Changed -= LanguageChanged;
            if (_rotation is not null) { _rotation.ReviewChanged -= ReviewChanged; _rotation.Dispose(); }
        };
        StableLayout.Reserve(_status, "运行中 100%", "Running 100%", "等待中", "完成", "失败", "已暂停", "正在终止", "已停止");
        _status.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
        _status.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        _status.Bind(ToolTip.TipProperty, new Avalonia.Data.Binding(nameof(TextBlock.Text)) { Source = _status });
        _status.FontFeatures = new Avalonia.Media.FontFeatureCollection { Avalonia.Media.FontFeature.Parse("tnum") };
        var executionLabels = new[] { "开始任务", "继续任务", "临时停止", "重新执行", "重新检测" };
        if (_speechRoot is null) { StableLayout.Reserve(_run, executionLabels); StableLayout.Reserve(_pause, executionLabels); }
        _refreshTimer.Tick += (_, _) => { if (!_closed && (Interlocked.Exchange(ref _refreshPosted, 0) != 0 || _speechRoot is not null || _personView is not null)) Refresh(); };
        _refreshTimer.Start();
        Refresh();
    }

    private async Task ReadLogAsync()
    {
        var revision = ++_logRevision;
        try
        {
            var log = await _job.ReadLogAsync();
            if (_closed || revision != _logRevision) return;
            _log.Text = log; _log.IsVisible = !string.IsNullOrWhiteSpace(log);
        }
        catch (Exception error) { if (!_closed && revision == _logRevision) { _actionError = error.Message; RefreshError(); } }
    }

    private async Task RunActionAsync(Func<Task> action, bool closeOnSuccess = false)
    {
        if (_busy) return;
        _busy = true; _actionError = ""; RefreshError(); RefreshActions();
        try { await action(); if (closeOnSuccess && !_closed) Close(); }
        catch (Exception error) { if (!_closed) { _actionError = error.Message; RefreshError(); } }
        finally { _busy = false; if (!_closed) RefreshActions(); }
    }

    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (!_closed) Interlocked.Exchange(ref _refreshPosted, 1);
    }
    private void LanguageChanged(object? sender, EventArgs args) => Refresh();
    private void ReviewChanged(object? sender, EventArgs args)
    { if (_closed) return; if (_rotation?.SettingsExpanded == true) _records.IsExpanded = false; RefreshActions(); }
    private void Refresh()
    {
        _rotation?.Update(); RefreshLongSummaryProgress();
        _status.Text = _job.State == JobState.Running ? Localization.Format($"运行中 {_job.Progress:0}%")
            : Localization.Text(_job.State switch
            { JobState.Waiting => "等待中", JobState.Completed => "完成", JobState.Failed => "失败",
                JobState.Paused => "已暂停", JobState.Stopping => "正在终止", _ => "已停止" });
        _status.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(_job.State switch
        { JobState.Completed => "UiSuccess", JobState.Failed => "UiDanger", _ => "UiTextSecondary" }));
        _summary.Text = Localization.Text(_job.ProgressDetail); _summary.IsVisible = _job.ProgressDetail.Length > 0;
        if (_shownLogState != _job.State)
        {
            _shownLogState = _job.State; _actionError = "";
            _log.Text = ""; _log.IsVisible = false; ++_logRevision;
            if (_records.IsExpanded) _ = ReadLogAsync();
        }
        RefreshError();
        if (_personView is not null) _personView.Update();
        else if (_speechRoot is not null) RefreshSpeechProgress();
        else { _activity.Update(_job.Activity); _details.Update(_job.Activity); }
        RefreshActions();
    }
    private void RefreshError()
    { var error = _actionError.Length > 0 ? _actionError : _job.Error; _error.Text = error; _error.IsVisible = error.Length > 0; }
    private void RefreshActions()
    {
        _result.IsEnabled = !_busy && _hasResult();
        if (_speechOutput is not null) _speechOutput.IsEnabled = !_busy && _speechOutputAvailable;
        _result.Content = Localization.Text(_rotation?.ActionLabel ?? (_job.FeatureId == "auto-subtitle" ? "校对字幕"
            : _job.FeatureId == "person-clip" ? "调整片段" : "查看结果"));
        _pause.IsVisible = _pause.Tag is not null && _job.State is JobState.Waiting or JobState.Running;
        _stop.IsVisible = _job.State is JobState.Waiting or JobState.Paused or JobState.Running;
        _run.IsEnabled = !_busy && _canRun();
        _run.IsVisible = _job.State is not (JobState.Running or JobState.Stopping);
        _run.Content = Localization.Text(_job.State == JobState.Waiting ? "开始任务" : _job.State == JobState.Paused ? "继续任务"
            : _rotation?.IsReview == true ? "重新检测" : "重新执行");
        if (_personView is not null)
        {
            _result.IsVisible = _job.State == JobState.Completed && _personView.HasCompleteResult;
            _pause.IsVisible = false;
            _stop.Content = Localization.Text("停止");
            _stop.IsEnabled = !_busy;
        }
    }
}
