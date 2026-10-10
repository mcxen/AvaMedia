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
        Notify.IsCheckedChanged += (_, _) => _settings.NotifyComplete = Notify.IsChecked == true;
        InitializeBackground();
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
                catch (Exception ex) { failures.Add(Localization.Format($"系统菜单未能启用：{ex.Message}")); }
            if (!IsCaptureSession && WantsTray(_settings) && _optionServices.CanUseTray)
                try { SetTray(true); }
                catch (Exception ex) { failures.Add(Localization.Format($"托盘未能启用：{ex.Message}")); }
            if (failures.Count > 0 && !_closing) SummaryText.Text = Localization.Join("；", failures);
            if (!IsCaptureSession && !FirstRunSetup.Failed)
                await ApplicationUpdater.Shared.StartupAsync(this, _settings, _optionServices.CheckUpdatesAsync, _optionLifetime.Token);
        };
        Closed += (_, _) => { _optionLifetime.Cancel(); _completionCancellation?.Cancel(); _optionServices.Dispose(); _optionLifetime.Dispose(); };
    }
    private void SetTray(bool enabled)
    {
        enabled = enabled && !IsCaptureSession;
        _optionServices.SetTray(enabled, RestoreFromTray, RequestExit); _trayEnabled = enabled;
        if (enabled) ConfigureTaskTray();
        if (!enabled && !IsVisible) RestoreFromTray();
    }
    internal void RestoreFromTray() => RestoreBackgroundWindow();

    private void ApplyOptions()
    {
        var prior = _appliedSettings.Clone();
        try
        {
            if (_settings.SystemContextMenu != prior.SystemContextMenu) _optionServices.SetContextMenu(_settings.SystemContextMenu);
            if (WantsTray(_settings) != WantsTray(prior)) SetTray(WantsTray(_settings));
            _storage.SaveSettings(_settings);
        }
        catch
        {
            try
            {
                if (_settings.SystemContextMenu != prior.SystemContextMenu) _optionServices.SetContextMenu(prior.SystemContextMenu);
                if (WantsTray(_settings) != WantsTray(prior)) SetTray(WantsTray(prior));
            }
            finally { _settings.CopyFrom(prior); }
            throw;
        }
        if (prior.EnableNsfwContent && !_settings.EnableNsfwContent)
            foreach (var job in _jobs.Where(job => job.Options.MediaTag is not null))
            {
                job.Activity = null;
                if (MediaPrivacy.IsSensitiveText(job.ProgressDetail)) job.ProgressDetail = "";
            }
        _ = ConfigureMcpAsync();
        ApplicationUpdater.Shared.PreferencesChanged(_settings);
        _appliedSettings = _settings.Clone();
        RefreshOutputPath(); Multithread.IsChecked = _settings.MultiThread; Notify.IsChecked = _settings.NotifyComplete;
        Motion.SetReducedMotion(_settings.ReduceMotion);
        if(prior.EnableBetaFeatures != _settings.EnableBetaFeatures)
        {
            if(!_settings.EnableBetaFeatures && Catalog.IsBeta(_last))_last=Catalog.Find("clip");
            ShowCategory(_category);
        }
        if(prior.AutoDownloadRepairModel != _settings.AutoDownloadRepairModel)
        {
            if(_settings.AutoDownloadRepairModel)_=ModelInstallation.StartAsync();
            else ModelInstallation.Cancel();
        }
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
            if (preferences.NotifyComplete && !token.IsCancellationRequested) NotifyQueueResults(batch);
            if (completion.AllSucceeded && preferences.ShutdownOnComplete && !_jobs.Any(j => j.State is JobState.Waiting or JobState.Running or JobState.Paused or JobState.Stopping))
            {
                if (await Notifications.ShutdownNotifications.WaitAsync(this, token) && !token.IsCancellationRequested) await _optionServices.ShutdownAsync(token);
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closing && IsVisible) await Ui.Message(this, "完成后操作失败", ex.Message); }
    }
    public Task ImportForConversionAsync(IEnumerable<string> paths) => Configure(Catalog.Find("mp4"), paths.Where(File.Exists).ToArray());
}
