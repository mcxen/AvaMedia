using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed class AiTaskWindow : Window
{
    private readonly Job _job;
    private readonly TextBlock _status = Ui.Text(""), _error = Ui.Text("", "caption");
    private readonly AiActivityView _activity = new();
    private readonly Button _result, _stop, _run;
    private readonly Func<bool> _hasResult, _canRun;
    private JobState? _shownLogState;
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private bool _closed;
    private int _refreshPosted;

    public AiTaskWindow(Job job, Func<bool> hasResult, Func<Task> showResult, Action stop, Func<Task> run, Func<bool> canRun, Action newTask)
    {
        _job = job; _hasResult = hasResult; _canRun = canRun;
        Title = Localization.Format($"任务 · {Localization.Key(Catalog.Find(job.FeatureId).Label)}");
        Width = 860; Height = 640; MinWidth = 650; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Margin = new(20), RowSpacing = 12 };
        var state = new StackPanel { Spacing = 6 };
        var source = Ui.Text(string.Join("\n", job.Inputs.Select(Path.GetFileName)), "settingsHeading");
        Localization.SetIsUserText(source, true); state.Children.Add(source); state.Children.Add(_status); state.Children.Add(_error); root.Children.Add(state);
        Grid.SetRow(_activity, 1); root.Children.Add(_activity);
        Grid.SetRow(_log, 2); root.Children.Add(_log); Localization.SetIsUserText(_log, true);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(Ui.Button("新建任务", newTask));
        _run = Ui.Button("开始任务", async () => await RunActionAsync(run)); actions.Children.Add(_run);
        _stop = Ui.Button("停止任务", stop); actions.Children.Add(_stop);
        _result = Ui.Button("查看结果", async () => await RunActionAsync(showResult)); _result.Classes.Add("primary"); actions.Children.Add(_result);
        actions.Children.Add(Ui.DialogButton("关闭", Close)); Grid.SetRow(actions, 3); root.Children.Add(actions); Content = root;
        job.PropertyChanged += Changed;
        Opened += async (_, _) => await ReadLogAsync();
        Closed += (_, _) => { _closed = true; job.PropertyChanged -= Changed; };
        Refresh();
    }

    private async Task ReadLogAsync()
    { try { var log = await _job.ReadLogAsync(); if (!_closed) _log.Text = log; } catch (Exception error) { if (!_closed) _error.Text = error.Message; } }

    private async Task RunActionAsync(Func<Task> action)
    { try { await action(); } catch (Exception error) { if (!_closed) _error.Text = error.Message; } }

    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (_closed || Interlocked.Exchange(ref _refreshPosted, 1) != 0) return;
        Dispatcher.UIThread.Post(() => { Interlocked.Exchange(ref _refreshPosted, 0); if (!_closed) Refresh(); });
    }
    private void Refresh()
    {
        _status.Text = _job.Status;
        _error.Text = _job.Error; _error.IsVisible = _job.Error.Length > 0;
        _activity.Update(_job.Activity);
        _result.IsEnabled = _hasResult();
        _result.Content = Localization.Text(_job.Options.Orientation is not null ? "确认方向" : _job.FeatureId == "auto-subtitle" ? "校对字幕" : _job.FeatureId == "person-clip" ? "调整片段" : "查看结果");
        _stop.IsVisible = _job.State is JobState.Waiting or JobState.Paused or JobState.Running;
        _run.IsEnabled = _canRun();
        _run.IsVisible = _job.State is not (JobState.Running or JobState.Stopping);
        _run.Content = Localization.Text(_job.State == JobState.Waiting ? "开始任务" : _job.State == JobState.Paused ? "继续任务" : "重新执行");
        if (_shownLogState != _job.State) { _shownLogState = _job.State; _ = ReadLogAsync(); }
    }
}
