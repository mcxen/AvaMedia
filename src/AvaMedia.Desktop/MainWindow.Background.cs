using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private volatile bool _backgroundWindowVisible;
    private int _queueDirty, _refreshPosted;
    private bool _exitRequested, _exitFinished;
    private Task? _exitTask;
    private WindowState _restoreState = WindowState.Normal;
    private QueueCompletion? _lastCompletion;
    private long _lastQueueUiRefresh;
    private long _displayedElapsedSecond = -1;
    private bool IsCaptureSession => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
        (desktop.Args?.Contains("--capture") == true || desktop.Args?.Contains(SetupPreviewExporter.Argument) == true);
    private static bool WantsTray(AppSettings settings) => settings.MinimizeToTray || settings.CloseToTray;

    private void InitializeBackground()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                if (WindowState != WindowState.Minimized) _restoreState = WindowState;
                else if (_trayEnabled && _settings.MinimizeToTray) MoveToBackground();
            }
            if (e.Property == IsVisibleProperty || e.Property == WindowStateProperty)
            {
                var visible = IsQueuePresentationVisible;
                if (_backgroundWindowVisible != visible)
                { _backgroundWindowVisible = visible; JobPresentationChanged?.Invoke(visible); }
                if (_backgroundWindowVisible && !_closing) { Refresh(); DrainQueueChanges(); }
            }
        };
        Closing += BackgroundClosing;
    }
    internal void MoveToBackground()
    {
        if (_closing || !_trayEnabled || IsCaptureSession) return;
        if (WindowState != WindowState.Minimized) _restoreState = WindowState;
        ShowInTaskbar = false; Hide(); Save(); RefreshTaskState();
    }
    internal void RestoreBackgroundWindow()
    {
        if (_closing) return;
        ShowInTaskbar = true; WindowState = _restoreState; Skin.RestoreWindow(this); Show(); Activate(); Refresh();
    }
    private void BackgroundClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exitFinished) return;
        e.Cancel = true;
        if (_closing) return;
        if (!_exitRequested && !IsCaptureSession && _settings.CloseToTray && _trayEnabled &&
            e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined)
        { MoveToBackground(); return; }
        RequestExit();
    }
    internal void RequestExit()
    {
        _exitRequested = true;
        if (_exitTask is not { IsCompleted: false }) _exitTask = ExitBackgroundAsync();
    }
    private async Task ExitBackgroundAsync()
    {
        _closing = true; RefreshTaskState(); _completionCancellation?.Cancel(); _queue.Stop();
        try
        {
            if (_wifiTransferWindow is { } transfer) await transfer.ShutdownAsync();
            await _running;
            foreach (var window in _classificationWindows.Values.Distinct().ToArray()) await window.FlushTaskEditsAsync();
            Save(); await _queueSave; _optionLifetime.Cancel(); _timer.Stop(); _exitFinished = true; Close();
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                ReferenceEquals(desktop.MainWindow, this) && desktop.Windows.Count > 0) desktop.Shutdown();
        }
        catch (Exception ex)
        {
            _closing = false; _exitRequested = false; _exitTask = null;
            RestoreBackgroundWindow(); await Ui.Message(this, "退出失败", ex.Message);
        }
    }

    // Collapse decoder progress bursts into one queued UI update. Hidden windows update once per second.
    private void QueueJobChanged(Job job)
    {
        _downloadSpeedTracker.Observe(job);
        Interlocked.Exchange(ref _queueDirty, 1);
        if (!_backgroundWindowVisible || _closing || Interlocked.CompareExchange(ref _refreshPosted, 1, 0) != 0) return;
        Dispatcher.UIThread.Post(DrainQueueChanges, DispatcherPriority.Background);
    }
    private void DrainQueueChanges()
    {
        Interlocked.Exchange(ref _refreshPosted, 0);
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_backgroundWindowVisible && now - _lastQueueUiRefresh < System.Diagnostics.Stopwatch.Frequency / 10) return;
        if (_closing || Interlocked.Exchange(ref _queueDirty, 0) == 0) return;
        _lastQueueUiRefresh = now; Refresh(false);
        if (DateTime.UtcNow - _lastSave > TimeSpan.FromSeconds(1)) { Save(); _lastSave = DateTime.UtcNow; }
    }
    private void BackgroundTick()
    {
        if (_closing) return;
        SampleDownloadSpeedMonitor();
        if (_backgroundWindowVisible) UpdateElapsed();
        DrainQueueChanges();
    }
    private void UpdateElapsed()
    {
        var elapsed = _elapsed.Elapsed;
        var second = elapsed.Ticks / TimeSpan.TicksPerSecond;
        if (_displayedElapsedSecond == second) return;
        _displayedElapsedSecond = second;
        Localization.SetText(ElapsedText, $"耗时: {elapsed.ToString(@"hh\:mm\:ss")}");
    }
    private void ConfigureTaskTray()
    {
        if (_optionServices is not IBackgroundTaskTray tray) return;
        tray.SetTaskActions(new(() => _ = InvokeTrayActionAsync(StartQueueAsync), () => { _queue.Stop(); Save(); Refresh(); },
            () => _ = InvokeTrayActionAsync(OpenBackgroundOutputAsync), () => _ = InvokeTrayActionAsync(ShowLastCompletionAsync), MoveToBackground));
        RefreshTaskState();
    }
    private async Task InvokeTrayActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closing) { RestoreFromTray(); await Ui.Message(this, "托盘操作失败", ex.Message); } }
    }
    private Task OpenBackgroundOutputAsync()
    {
        var folders = _lastCompletion?.OutputFolders;
        if (folders?.Count > 0) foreach (var folder in folders) _optionServices.OpenFolder(folder);
        else { Directory.CreateDirectory(_settings.OutputFolder); _optionServices.OpenFolder(_settings.OutputFolder); }
        return Task.CompletedTask;
    }
    private void RefreshTaskState()
    {
        if (!_trayEnabled || _optionServices is not IBackgroundTaskTray tray) return;
        var waiting = _jobs.Count(j => j.State == JobState.Waiting);
        var active = _jobs.Where(j => j.State is JobState.Running or JobState.Stopping).ToArray();
        var completed = _jobs.Count(j => j.State == JobState.Completed); var failed = _jobs.Count(j => j.State == JobState.Failed);
        var summary = _closing ? "正在退出…" : _queue.IsRunning
            ? Localization.Format($"处理中 {active.Length} 个 · {(active.Length > 0 ? active.Average(j => j.Progress) : 0):0}% · 等待 {waiting} 个")
            : Localization.Format($"等待 {waiting} 个 · 完成 {completed} 个 · 失败 {failed} 个");
        tray.UpdateTaskState(new(summary, _jobs.Any(CanStartTask), !_closing && _queue.IsRunning && !_queue.IsStopping, _lastCompletion is not null, !IsVisible));
    }
    private readonly List<Job> _runningBatch = [];
    private Task StartQueueAsync() => StartQueueAsync(_jobs.Where(CanStartTask).ToArray());
    private async Task StartQueueAsync(Job[] requested)
    {
        if (!CanManageTasks || _queue.IsStopping) return;
        var batch = requested.Where(job => _jobs.Contains(job) && CanStartTask(job)).Distinct().ToArray(); if (batch.Length == 0) return;
        if (_queue.IsRunning)
        {
            var added = _queue.Enqueue(batch);
            _runningBatch.AddRange(added.Where(job => !_runningBatch.Contains(job)));
            _queue.ReorderPending(_jobs); Save(); Refresh(); return;
        }
        _runningBatch.Clear(); _runningBatch.AddRange(batch);
        if (batch.Any(j => j.FeatureId == "download")) ResetDownloadSpeedMonitor();
        _completionCancellation?.Cancel(); _lastCompletion = null; Save(); _elapsed.Restart();
        _timer.Start();
        try
        {
            _running = _queue.Run(batch, _settings.MultiThread ? _settings.ParallelJobs : 1); Refresh();
            await _running;
        }
        finally { _elapsed.Stop(); _timer.Stop(); _running = Task.CompletedTask; SampleDownloadSpeedMonitor(); }
        if (_closing) return;
        Interlocked.Exchange(ref _queueDirty, 0);
        var finished = _runningBatch.Where(_jobs.Contains).Distinct().ToArray(); _runningBatch.Clear();
        Save(); _lastCompletion = QueueCompletion.From(finished); Refresh();
        CompletionActions = FinishQueueOptionsAsync(finished, _settings.Clone()); await CompletionActions;
    }
    private async Task ShowLastCompletionAsync()
    {
        if (_closing || _lastCompletion is not { } completion) return;
        await Ui.Message(this, "任务结果", CompletionMessage(completion));
    }
    private static string CompletionMessage(QueueCompletion completion) =>
        Localization.Format($"成功 {completion.Completed} 个，失败 {completion.Failed} 个，停止 / 未执行 {completion.Cancelled} 个。\n\n输出目录：\n{string.Join("\n", completion.OutputFolders)}");
}
