using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private readonly IAppOptionsServices _optionServices;
    private readonly CancellationTokenSource _optionLifetime = new();
    private CancellationTokenSource? _completionCancellation;
    private AppSettings _appliedSettings = new();
    private bool _trayEnabled;
    private bool _startupOptionsInitialized;
    internal Task CompletionActions { get; private set; } = Task.CompletedTask;

    private void InitializeOptions()
    {
        _appliedSettings = _settings.Clone();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty && WindowState == WindowState.Minimized && _trayEnabled) Hide();
        };
        AddHandler(Button.ClickEvent, (_, _) =>
        { if (_settings.PlayOperationSound) _optionServices.PlaySound(UiSound.Operation); }, Avalonia.Interactivity.RoutingStrategies.Bubble);
        AddHandler(MenuItem.ClickEvent, (_, _) =>
        { if (_settings.PlayOperationSound) _optionServices.PlaySound(UiSound.Operation); }, Avalonia.Interactivity.RoutingStrategies.Bubble);
        Opened += async (_, _) =>
        {
            if (_startupOptionsInitialized) return;
            _startupOptionsInitialized = true;
            var failures = new List<string>();
            if (_settings.SystemContextMenu)
                try { _optionServices.SetContextMenu(true); }
                catch (Exception ex) { failures.Add("系统菜单未能启用：" + ex.Message); }
            if (_settings.MinimizeToTray && _optionServices.CanUseTray)
                try { SetTray(true); }
                catch (Exception ex) { failures.Add("托盘未能启用：" + ex.Message); }
            if (failures.Count > 0 && !_closing) SummaryText.Text = string.Join("；", failures);
            if (_settings.CheckForUpdates && Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            {
                try
                {
                    var update = await _optionServices.CheckUpdatesAsync(_optionLifetime.Token);
                    if (update.CheckSucceeded && update.HasUpdate && !_closing && IsVisible) await new UpdateWindow(update).ShowDialog(this);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("自动检查更新失败：{0}", ex.Message); }
            }
        };
        Closed += (_, _) => { _optionLifetime.Cancel(); _completionCancellation?.Cancel(); _optionServices.Dispose(); _optionLifetime.Dispose(); };
    }
    private void SetTray(bool enabled)
    {
        _optionServices.SetTray(enabled, RestoreFromTray, () => Close()); _trayEnabled = enabled;
        if (!enabled && !IsVisible) RestoreFromTray();
    }
    internal void RestoreFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }

    private void ApplyOptions()
    {
        var prior = _appliedSettings.Clone();
        try
        {
            if (_settings.SystemContextMenu != prior.SystemContextMenu) _optionServices.SetContextMenu(_settings.SystemContextMenu);
            if (_settings.MinimizeToTray != prior.MinimizeToTray) SetTray(_settings.MinimizeToTray);
            _storage.SaveSettings(_settings);
        }
        catch
        {
            try
            {
                if (_settings.SystemContextMenu != prior.SystemContextMenu) _optionServices.SetContextMenu(prior.SystemContextMenu);
                if (_settings.MinimizeToTray != prior.MinimizeToTray) SetTray(prior.MinimizeToTray);
            }
            finally { _settings.CopyFrom(prior); }
            throw;
        }
        _appliedSettings = _settings.Clone();
        OutputPath.Text = "📂 " + _settings.OutputFolder; Multithread.IsChecked = _settings.MultiThread; Notify.IsChecked = _settings.NotifyComplete;
        Motion.SetReducedMotion(_settings.ReduceMotion);
    }

    private async Task FinishQueueOptionsAsync(Job[] batch, AppSettings preferences)
    {
        var completion = QueueCompletion.From(batch);
        if (_closing || !completion.Finished) return;
        _completionCancellation?.Dispose(); _completionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_optionLifetime.Token);
        var token = _completionCancellation.Token;
        try
        {
            if (completion.Failed > 0 && preferences.PlayErrorSound) _optionServices.PlaySound(UiSound.Error);
            else if (completion.AllSucceeded && preferences.PlayCompleteSound) _optionServices.PlaySound(UiSound.Complete);
            if (preferences.OpenOutputFolderOnComplete)
                foreach (var folder in completion.OutputFolders) _optionServices.OpenFolder(folder);
            if (completion.AllSucceeded && preferences.ShutdownOnComplete)
            {
                RestoreFromTray();
                var countdown = new ShutdownCountdownWindow();
                using var registration = token.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => countdown.Close(false)));
                if (await countdown.ShowDialog<bool>(this) && !token.IsCancellationRequested) await _optionServices.ShutdownAsync(token);
                return;
            }
            if (preferences.NotifyComplete && !token.IsCancellationRequested)
            {
                RestoreFromTray();
                await Ui.Message(this, "转换完成", $"成功 {completion.Completed} 个，失败 {completion.Failed} 个。\n\n输出目录：\n" + string.Join("\n", completion.OutputFolders));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closing && IsVisible) await Ui.Message(this, "完成后操作失败", ex.Message); }
    }
    public Task ImportForConversionAsync(IEnumerable<string> paths) => Configure(Catalog.Find("mp4"), paths.Where(File.Exists).ToArray());
}
