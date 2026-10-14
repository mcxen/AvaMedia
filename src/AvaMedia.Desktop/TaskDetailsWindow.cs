using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using AvaMedia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

/// <summary>Persistent task information and logs, independent of notification history.</summary>
public sealed partial class TaskDetailsWindow : Window
{
    private readonly Job _job;
    private readonly Func<Task> _run;
    private readonly Func<bool> _canRun;
    private readonly Func<Task> _edit;
    private readonly Func<bool> _canEdit;
    private readonly Func<Task>? _viewTask;
    private readonly StatusPill _state = new();
    private readonly TextBlock _name = Ui.ComponentText("", "heading");
    private readonly TextBlock _activity = Ui.ComponentText("", "caption");
    private readonly TextBlock _actionStatus = Ui.ComponentText("", "caption");
    private readonly ProgressBar _progress = new() { Classes = { "ac" }, Maximum = 100 };
    private readonly SegmentedControl _sections = new();
    private readonly StackPanel _result = Rows();
    private readonly StackPanel _files = Rows();
    private readonly StackPanel _parameters = Rows();
    private readonly TextBox _log = ReadOnlyText();
    private readonly TextBlock _logStatus = Ui.ComponentText("", "caption");
    private readonly Button _copyLog;
    private readonly Button _refreshLog;
    private readonly Button _runButton;
    private readonly Button _editButton;
    private readonly Button _outputButton;
    private readonly Button _sourceButton;
    private readonly Button _taskButton;
    private readonly Control[] _pages;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _closed;
    private bool _readingLog;
    private bool _acting;
    private int _refreshPosted;
    private int _refreshDetails;
    private string _logText = "";

    public TaskDetailsWindow(Job job, Func<Task> run, Func<bool> canRun, Func<Task> edit,
        Func<bool> canEdit, Func<Task>? viewTask = null)
    {
        _job = job; _run = run; _canRun = canRun; _edit = edit; _canEdit = canEdit; _viewTask = viewTask;
        Title = Localization.Text("任务详情"); Width = 860; Height = 700; MinWidth = 600; MinHeight = 480;
        Localization.SetIsUserText(_name, true);
        Localization.SetIsUserText(_log, true);
        _log.Bind(TextBox.FontFamilyProperty, new DynamicResourceExtension("UiNumericFont"));
        ScrollViewer.SetVerticalScrollBarVisibility(_log, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_log, ScrollBarVisibility.Disabled);
        Avalonia.Automation.AutomationProperties.SetName(_log, Localization.Text("处理日志"));
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto") };
        root.Bind(MarginProperty, new DynamicResourceExtension("UiPagePadding"));
        root.Bind(Grid.RowSpacingProperty, new DynamicResourceExtension("AcGap"));
        var header = new Grid { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("Auto,Auto,Auto") };
        header.Bind(Grid.ColumnSpacingProperty, new DynamicResourceExtension("AcRowGap"));
        header.Bind(Grid.RowSpacingProperty, new DynamicResourceExtension("AcRowGap"));
        header.Children.Add(new Controls.FeatureIcon { Kind = Catalog.Find(job.FeatureId).Icon, Width = 40, Height = 40 });
        Grid.SetRowSpan(header.Children[0], 2);
        header.Children.Add(_name); Grid.SetColumn(_name, 1);
        header.Children.Add(_state); Grid.SetColumn(_state, 2); _state.VerticalAlignment = VerticalAlignment.Top;
        header.Children.Add(_activity); Grid.SetColumn(_activity, 1); Grid.SetRow(_activity, 1); Grid.SetColumnSpan(_activity, 2);
        header.Children.Add(_progress); Grid.SetColumn(_progress, 1); Grid.SetRow(_progress, 2); Grid.SetColumnSpan(_progress, 2);
        root.Children.Add(header);
        _sections.ItemsSource = new[] { Localization.Text("结果"), Localization.Text("文件与参数"), Localization.Text("处理日志") };
        _sections.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(_sections); Grid.SetRow(_sections, 1);
        var information = Rows();
        information.Children.Add(Ui.ComponentCard(_files)); information.Children.Add(Ui.ComponentCard(_parameters));
        var logPage = new Grid { RowDefinitions = new("Auto,*") };
        logPage.Bind(Grid.RowSpacingProperty, new DynamicResourceExtension("AcRowGap"));
        var logToolbar = new Grid { ColumnDefinitions = new("*,Auto,Auto") };
        logToolbar.Bind(Grid.ColumnSpacingProperty, new DynamicResourceExtension("AcRowGap"));
        logToolbar.Children.Add(_logStatus);
        _refreshLog = ActionButton("刷新日志", RefreshLogAsync);
        _copyLog = ActionButton("复制日志", CopyLogAsync);
        logToolbar.Children.Add(_refreshLog); Grid.SetColumn(_refreshLog, 1);
        logToolbar.Children.Add(_copyLog); Grid.SetColumn(_copyLog, 2);
        logPage.Children.Add(logToolbar); logPage.Children.Add(_log); Grid.SetRow(_log, 1);
        _pages = [new ScrollViewer { Content = Ui.ComponentCard(_result), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            new ScrollViewer { Content = information, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, logPage];
        var body = new Grid();
        foreach (var page in _pages) { page.IsVisible = false; body.Children.Add(page); }
        root.Children.Add(body); Grid.SetRow(body, 2);
        var footer = Rows();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Bind(StackPanel.SpacingProperty, new DynamicResourceExtension("AcRowGap"));
        _runButton = ActionButton("开始任务", _run); _runButton.Classes.Remove("secondary"); _runButton.Classes.Add("primary");
        _editButton = ActionButton("编辑任务…", _edit);
        _sourceButton = ActionButton("打开来源", () => OpenAsync(_job.Inputs.FirstOrDefault(path => File.Exists(path) || Directory.Exists(path))));
        _outputButton = ActionButton("打开输出文件", () => OpenAsync(_job.UserOutput));
        _taskButton = ActionButton("打开任务界面…", () => _viewTask?.Invoke() ?? Task.CompletedTask);
        foreach (var button in new[] { _sourceButton, _outputButton, _taskButton, _editButton, _runButton, Ui.DialogButton("关闭", Close) })
            actions.Children.Add(button);
        footer.Children.Add(_actionStatus); footer.Children.Add(actions);
        root.Children.Add(footer); Grid.SetRow(footer, 3);
        Content = Controls.WindowChrome.WrapDialog(this, root);
        _sections.SelectionChanged += (_, _) => SelectSection(); _sections.SelectedIndex = 0;
        job.PropertyChanged += JobChanged;
        Localization.Changed += LanguageChanged;
        _logTimer.Tick += async (_, _) =>
        { if (IsVisible && _sections.SelectedIndex == 2 && _job.State is JobState.Running or JobState.Stopping) await RefreshLogAsync(); };
        Opened += async (_, _) =>
        {
            _logTimer.Start();
            if (job.Options.VlRename is not null && job.VlRenameResult is null)
            {
                try { job.VlRenameResult = await AiTaskResults.LoadAsync<VlRenameTaskResult>(job.Output, _lifetime.Token); }
                catch (OperationCanceledException) { }
                catch (Exception error) { AppDiagnostics.Record("Read VL task result", error); }
            }
            if (!_closed) await RefreshLogAsync();
        };
        Closed += (_, _) =>
        {
            _closed = true; _logTimer.Stop(); _lifetime.Cancel();
            _job.PropertyChanged -= JobChanged; Localization.Changed -= LanguageChanged;
        };
        Refresh();
    }

    public void ShowLog() { _sections.SelectedIndex = 2; _ = RefreshLogAsync(); }
    private void SelectSection()
    {
        for (var index = 0; index < _pages.Length; index++) _pages[index].IsVisible = index == _sections.SelectedIndex;
        if (_sections.SelectedIndex == 2) _ = RefreshLogAsync();
    }
    private void LanguageChanged(object? sender, EventArgs args)
    {
        var selected = _sections.SelectedIndex;
        Title = Localization.Text("任务详情");
        _sections.ItemsSource = new[] { Localization.Text("结果"), Localization.Text("文件与参数"), Localization.Text("处理日志") };
        _sections.SelectedIndex = selected; Refresh();
    }
    private void JobChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(Job.Progress) or nameof(Job.Status) or nameof(Job.Estimate)
            or nameof(Job.RemainingTimeText) or nameof(Job.Activity))) Interlocked.Exchange(ref _refreshDetails, 1);
        if (Interlocked.Exchange(ref _refreshPosted, 1) != 0) return;
        Dispatcher.UIThread.Post(async () =>
        {
            Interlocked.Exchange(ref _refreshPosted, 0);
            if (_closed) return;
            if (Interlocked.Exchange(ref _refreshDetails, 0) != 0) Refresh(); else RefreshStatus();
            if (_job.State is not (JobState.Running or JobState.Stopping)) await RefreshLogAsync();
        });
    }
    private bool Unchanged => _job.CompletionKind == JobCompletionKind.Unchanged;
    private bool HasOutput => !Unchanged && !_job.HasInternalOutput && _job.State == JobState.Completed
        && (File.Exists(_job.UserOutput) || Directory.Exists(_job.UserOutput));
    private void RefreshStatus()
    {
        _state.State = Unchanged ? "waiting" : _job.State.ToString().ToLowerInvariant();
        _state.Text = Localization.Text(Unchanged ? "无需瘦身" : _job.State switch
        {
            JobState.Completed => "已完成", JobState.Failed => "失败", JobState.Cancelled => "已取消",
            JobState.Paused => "已暂停", JobState.Running => "进行中", JobState.Stopping => "正在停止", _ => "等待"
        });
        _activity.Text = _job.State is JobState.Running or JobState.Stopping
            ? Localization.Text(_job.Status) : Localization.Text(_job.Options.VlRename is not null ? "VL 命名分析" : FeatureNavigation.Label(Catalog.Find(_job.FeatureId)));
        _progress.IsVisible = _job.State is JobState.Running or JobState.Stopping;
        _progress.Value = Math.Clamp(_job.Progress, 0, 100);
        _runButton.IsVisible = !_acting && _canRun();
        _runButton.Content = Localization.Text(_job.State switch
        { JobState.Failed => "重试任务", JobState.Completed => "重新执行", JobState.Paused or JobState.Cancelled => "继续任务", _ => "开始任务" });
        _editButton.IsVisible = _canEdit(); _editButton.IsEnabled = !_acting;
        _sourceButton.IsEnabled = _job.Inputs.Any(path => File.Exists(path) || Directory.Exists(path));
        _outputButton.IsVisible = HasOutput; _taskButton.IsVisible = _viewTask is not null;
    }
    private void Refresh()
    {
        if (_closed) return;
        _name.Text = _job.Name; ToolTip.SetTip(_name, _job.Source); RefreshStatus();
        _result.Children.Clear(); _files.Children.Clear(); _parameters.Children.Clear();
        _result.Children.Add(Ui.ComponentText("处理结果", "heading"));
        var message = Unchanged ? _job.CompletionMessage : _job.State switch
        {
            JobState.Failed => string.IsNullOrWhiteSpace(_job.Error) ? Localization.Text("任务执行失败，请查看处理日志。") : _job.Error,
            JobState.Completed => string.IsNullOrWhiteSpace(_job.ProgressDetail) ? Localization.Text("处理已完成。") : _job.ProgressDetail,
            JobState.Cancelled => Localization.Text("任务已停止，处理记录保留在日志中。"),
            JobState.Waiting => Localization.Text("任务等待执行。"),
            JobState.Paused => Localization.Text("任务已暂停。"), _ => Localization.Text(_job.Status)
        };
        var reason = ValueText(Localization.Text(message)); if (_job.State == JobState.Failed) reason.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiDanger"));
        _result.Children.Add(reason);
        if (Unchanged) _result.Children.Add(Ui.ComponentText("保留原视频，未生成新文件。"));
        else if (_job.State == JobState.Completed && !_job.HasInternalOutput && !HasOutput)
            _result.Children.Add(Ui.ComponentText("输出文件缺失或不可访问。", "caption"));
        Row(_result, "任务 ID", _job.Id.ToString());
        if (_job.Duration > 0) Row(_result, "时长", MediaTime.Format(_job.Duration));
        if (_job.Options.VlRename is not null)
        {
            if (_job.VlRenameResult is { } preview)
            {
                Row(_result, "处理进度", Localization.Format($"成功 {preview.Completed} 个，失败 {preview.Failed} 个，共 {preview.Files.Length} 个。"));
                Row(_result, "视觉模型", preview.Model);
                Row(_result, "推理后端", preview.Backend);
            }
        }
        else AddSlimmingResult();
        _files.Children.Add(Ui.ComponentText("文件", "heading"));
        foreach (var path in _job.Inputs) Row(_files, "来源", path);
        if (_job.Inputs.Length == 0) _files.Children.Add(Ui.ComponentText("未指定源文件", "caption"));
        if (Unchanged) Row(_files, "产出", Localization.Text("保留原视频，未生成新文件。"));
        else if (_job.HasInternalOutput) Row(_files, "产出", Localization.Text("任务结果"));
        else Row(_files, _job.State == JobState.Completed ? "产出" : "计划输出", _job.UserOutput);
        AddParameters();
    }
    private async Task RefreshLogAsync()
    {
        if (_closed || _readingLog) return;
        _readingLog = true; _refreshLog.IsEnabled = false;
        if (_logText.Length == 0) _logStatus.Text = Localization.Text("正在读取日志…");
        try
        {
            var text = await _job.ReadLogAsync(_lifetime.Token);
            if (_closed) return;
            if (text != _logText) { _logText = text; _log.Text = text; }
            _logStatus.Text = Localization.Text(text.Length == 0 ? "暂无处理日志。" : "完整处理日志");
            _copyLog.IsEnabled = text.Length > 0;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        { if (!_closed) _logStatus.Text = Localization.Format($"日志读取失败：{error.Message}"); }
        finally { _readingLog = false; if (!_closed) _refreshLog.IsEnabled = true; }
    }
    private async Task CopyLogAsync()
    {
        if (Clipboard is not { } clipboard || _logText.Length == 0) return;
        await clipboard.SetTextAsync(_logText); _logStatus.Text = Localization.Text("日志已复制。");
    }
    private Button ActionButton(string label, Func<Task> action)
    {
        var button = new Button { Content = Localization.Text(label), Classes = { "ac", "secondary" } };
        button.Click += async (_, _) =>
        {
            if (_acting) return;
            _acting = true; button.IsEnabled = false; _actionStatus.Text = "";
            try { await action(); }
            catch (Exception error) { _actionStatus.Text = error.Message; }
            finally { _acting = false; button.IsEnabled = true; Refresh(); }
        };
        return button;
    }
    private static Task OpenAsync(string? path)
    {
        if (Directory.Exists(path)) PlatformServices.OpenFolder(path!);
        else if (File.Exists(path)) Process.Start(new ProcessStartInfo(path!) { UseShellExecute = true });
        return Task.CompletedTask;
    }
    private static StackPanel Rows()
    {
        var rows = new StackPanel(); rows.Bind(StackPanel.SpacingProperty, new DynamicResourceExtension("AcRowGap")); return rows;
    }
    private static TextBox ReadOnlyText() => new()
    {
        Classes = { "ac", "multiline" }, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private static SelectableTextBlock ValueText(string value)
    {
        var text = new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        Localization.SetIsUserText(text, true);
        text.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension("AcBodyFont")); return text;
    }
    private static void Row(StackPanel parent, string label, string? value)
    {
        var row = new Grid { ColumnDefinitions = new("Auto,*") };
        row.Bind(Grid.ColumnSpacingProperty, new DynamicResourceExtension("AcGap"));
        var caption = Ui.ComponentText(label, "caption"); caption.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(caption);
        var text = ValueText(string.IsNullOrWhiteSpace(value) ? "—" : value);
        Grid.SetColumn(text, 1); row.Children.Add(text); parent.Children.Add(row);
    }
    private static string Size(long bytes)
    {
        var value = (double)bytes; var units = new[] { "B", "KB", "MB", "GB", "TB" }; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
