using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class SettingsWindow
{
    private readonly ModelStore _modelStore = new();
    private readonly Dictionary<string, ModelRow> _modelRows = [];
    private bool _modelsClosed, _modelsRefreshing, _modelRefreshRequested, _repairWasInstalling;
    private string? _modelTarget;
    private bool _onlineTarget;
    public void OpenModelManagement(string? modelId = null)
    {
        _modelTarget = modelId; _onlineTarget = false; SettingsTabs.SelectedItem = ModelsTab;
        if (IsVisible) FocusModelTarget();
    }
    public void OpenOnlineAiSettings()
    {
        _modelTarget = null; _onlineTarget = true; SettingsTabs.SelectedItem = ProvidersTab;
        if (IsVisible) FocusModelTarget();
    }
    private void FocusModelTarget() => Dispatcher.UIThread.Post(() =>
    {
        if (_modelsClosed) return;
        if (_onlineTarget) { OnlineEndpointInput.BringIntoView(); OnlineEndpointInput.Focus(); }
        else if (_modelTarget is { } id && _modelRows.TryGetValue(id, out var row))
        { row.Container.BringIntoView(); row.Download.Focus(); }
    }, DispatcherPriority.Loaded);

    private sealed class ModelRow
    {
        public required Control Container { get; init; }
        public required TextBlock Status { get; init; }
        public required TextBlock Error { get; init; }
        public required StackPanel ProgressPanel { get; init; }
        public required TextBlock ProgressText { get; init; }
        public required ProgressBar ProgressBar { get; init; }
        public required Button Download { get; init; }
        public required Button Verify { get; init; }
        public required Button Delete { get; init; }
        public CancellationTokenSource? Cancellation { get; set; }
        public string? Action { get; set; }
        public string? Outcome { get; set; }
        public string? ErrorMessage { get; set; }
        public bool DownloadFailed { get; set; }
        public bool Installed { get; set; }
        public bool Busy { get; set; }
        public long Downloaded { get; set; }
        public ModelDownloadProgress? Progress { get; set; }
    }

    private void InitializeModelManagement()
    {
        foreach (var model in ModelCatalog.All)
        {
            var status = Ui.Text("读取状态…", "caption");
            status.TextWrapping = TextWrapping.NoWrap; status.TextTrimming = TextTrimming.CharacterEllipsis;
            var name = Ui.Text(model.Name, "settingsHeading");
            name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(name, model.Name);
            var identity = new Grid { ColumnDefinitions = new("*,144"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 12, RowSpacing = 3 };
            identity.Children.Add(name); Grid.SetColumn(status, 1); identity.Children.Add(status);
            var metadata = Ui.FormattedText($"{Localization.Key(model.Purpose)} · {ModelSize(model.DownloadSize)} · {model.License}", "caption");
            metadata.TextWrapping = TextWrapping.NoWrap; metadata.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(metadata, Localization.Format($"{Localization.Key(model.Purpose)} · {ModelSize(model.DownloadSize)} · {model.License}"));
            Grid.SetRow(metadata, 1); Grid.SetColumnSpan(metadata, 2); identity.Children.Add(metadata);

            var download = Ui.Button("下载", () => DownloadOrCancelModel(model));
            var verify = Ui.Button("校验", () => _ = RunModelActionAsync(model, "verify"));
            var delete = Ui.Button("删除", () => _ = RunModelActionAsync(model, "delete"));
            download.Classes.Add("primary");
            // Keep the action slot and focused control stable when download becomes cancel.
            var buttons = new Grid { ColumnDefinitions = new("136,68,68"), ColumnSpacing = 6, VerticalAlignment = VerticalAlignment.Center };
            var actions = new[] { download, verify, delete };
            for (var column = 0; column < actions.Length; column++)
            { actions[column].Classes.Add("field-action"); Grid.SetColumn(actions[column], column); buttons.Children.Add(actions[column]); }

            var progressText = Ui.Text("", "caption");
            progressText.TextWrapping = TextWrapping.NoWrap; progressText.TextTrimming = TextTrimming.CharacterEllipsis;
            var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, MinHeight = 4 };
            var progressPanel = new StackPanel { Spacing = 4, IsVisible = false };
            progressPanel.Children.Add(progressText); progressPanel.Children.Add(progressBar);
            var error = Ui.Text("", "settingError"); error.IsVisible = false; error.VerticalAlignment = VerticalAlignment.Top;
            error.TextWrapping = TextWrapping.NoWrap; error.TextTrimming = TextTrimming.CharacterEllipsis;
            // Progress and errors share a reserved area so actions never resize a model row.
            var details = new Grid { Height = 30, Margin = new(0, 6, 0, 0), ClipToBounds = true };
            details.Children.Add(progressPanel); details.Children.Add(error);
            var panel = new Grid { ColumnDefinitions = new("*,284"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 16 };
            panel.Children.Add(identity); Grid.SetColumn(buttons, 1); panel.Children.Add(buttons);
            Grid.SetRow(details, 1); Grid.SetColumnSpan(details, 2); panel.Children.Add(details);
            var container = new Border { Classes = { "settingSection", "modelRow" }, Child = panel };
            ModelList.Children.Add(container);
            _modelRows.Add(model.Id, new()
            {
                Status = status, Error = error, ProgressPanel = progressPanel, ProgressText = progressText, ProgressBar = progressBar,
                Download = download, Verify = verify, Delete = delete, Container = container
            });
        }
        ModelStatus.IsVisible = false;
        Opened += async (_, _) => { FocusModelTarget(); await RefreshModelsSafelyAsync(); };
        ModelInstallation.Changed += RepairModelChanged;
        Localization.Changed += ModelsLanguageChanged;
        Closed += (_, _) =>
        {
            _modelsClosed = true;
            ModelInstallation.Changed -= RepairModelChanged;
            Localization.Changed -= ModelsLanguageChanged;
        };
    }

    private void ModelsLanguageChanged(object? sender, EventArgs args)
    {
        foreach (var model in ModelCatalog.All) UpdateModelRow(model, _modelRows[model.Id]);
    }

    private void RepairModelChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_modelsClosed) return;
        try
        {
            var installing = ModelInstallation.Installing;
            var row = _modelRows[ModelCatalog.LamaId];
            if (installing && !_repairWasInstalling && row.Cancellation is null)
            { row.Outcome = row.ErrorMessage = null; row.DownloadFailed = false; }
            UpdateModelRow(ModelCatalog.Find(ModelCatalog.LamaId), row);
            // Byte progress updates only this row. Re-read local state when installation starts or ends.
            if (installing != _repairWasInstalling || ModelInstallation.Failed) _ = RefreshModelsSafelyAsync();
            _repairWasInstalling = installing;
        }
        catch (Exception error) { AppDiagnostics.Record("Repair model management status", error); }
    });

    private async Task RefreshModelsSafelyAsync()
    {
        if (_modelsClosed) return;
        _modelRefreshRequested = true;
        if (_modelsRefreshing) return;
        _modelsRefreshing = true;
        try
        {
            do
            {
                _modelRefreshRequested = false;
                await RefreshModelsAsync();
            } while (_modelRefreshRequested && !_modelsClosed);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            AppDiagnostics.Record("Read model management status", error);
            if (!_modelsClosed) { ModelStatus.Text = Localization.Text(error.Message); ModelStatus.IsVisible = true; }
        }
        finally { _modelsRefreshing = false; }
    }

    private static string ModelSize(long bytes) => bytes >= 1024 * 1024 * 1024
        ? $"{bytes / 1073741824d:0.00} GiB" : $"{bytes / 1048576d:0.0} MiB";

    private async Task RefreshModelsAsync()
    {
        foreach (var model in ModelCatalog.All)
        {
            if (_modelsClosed) return;
            var row = _modelRows[model.Id];
            if (row.Cancellation is not null) continue;
            var installed = await _modelStore.IsInstalledAsync(model.Id, ct: _lifetime.Token);
            if (_modelsClosed) return;
            if (row.Cancellation is not null) continue;
            row.Installed = installed; row.Busy = _modelStore.IsBusy(model.Id);
            row.Downloaded = _modelStore.DownloadedBytes(model.Id);
            UpdateModelRow(model, row);
        }
    }

    private void UpdateModelRow(DownloadableModel model, ModelRow row)
    {
        var background = model.Id == ModelCatalog.LamaId && ModelInstallation.Installing;
        var backgroundFailure = model.Id == ModelCatalog.LamaId && ModelInstallation.Failed && !row.Installed;
        var active = row.Cancellation is not null;
        var downloading = background || active && row.Action == "download";
        var busy = active || background || row.Busy;
        var progress = row.Progress;
        if (background)
            progress = (ModelInstallation.Progress ?? new(ModelInstallation.Received, ModelInstallation.Total, ModelInstallation.Stage,
                ModelInstallation.RetryAttempt, ModelInstallation.MaxAttempts)) with { Stage = ModelInstallation.Stage };
        var status = !model.Supported ? "当前平台不可用" : background ? ModelInstallation.Stage
            : active ? progress?.Stage ?? "处理中…" : row.Busy ? "使用中" : row.Outcome ?? (backgroundFailure ? "下载失败" : row.Installed ? "已下载" : "未下载");
        row.Status.Text = Localization.Text(status);
        ToolTip.SetTip(row.Status, row.Status.Text);
        row.Download.IsEnabled = downloading
            ? row.Cancellation?.IsCancellationRequested != true && (!background || !ModelInstallation.CancellationRequested)
            : model.Supported && !busy;
        row.Download.Content = Localization.Text(downloading ? "取消下载" : row.Downloaded > 0 && !row.Installed ? "继续下载"
            : row.DownloadFailed || backgroundFailure ? "重试下载" : row.Installed ? "修复下载" : "下载");
        row.Verify.IsEnabled = row.Installed && !busy;
        row.Delete.IsEnabled = !busy && _modelStore.HasLocalData(model.Id);
        row.Error.Text = Localization.Text(row.ErrorMessage ?? (backgroundFailure ? ModelInstallation.Error ?? "图片修复模型安装失败，请检查网络后重试。" : ""));
        row.Error.IsVisible = !string.IsNullOrEmpty(row.Error.Text);
        ToolTip.SetTip(row.Error, row.Error.IsVisible ? row.Error.Text : null);

        if (!active && !background)
            progress = row.Downloaded > 0 && !row.Installed ? new(row.Downloaded, model.DownloadSize, "下载未完成") : null;
        row.ProgressPanel.IsVisible = progress is not null && !row.Error.IsVisible;
        if (progress is null) return;
        row.ProgressText.Text = Localization.Text(progress.Stage);
        if (progress.Total > 0)
            row.ProgressText.Text += $" · {ModelSize(progress.Received)} / {ModelSize(progress.Total)} · {progress.Percent}%";
        if (progress.Attempt > 0)
            row.ProgressText.Text += " · " + Localization.Format($"重试 {progress.Attempt}/{progress.MaxAttempts}");
        if (progress.Source.Length > 0)
            row.ProgressText.Text += " · " + Localization.Format($"下载源 {progress.SourceIndex}/{progress.SourceCount} · {new Uri(progress.Source).Host}");
        ToolTip.SetTip(row.ProgressText, row.ProgressText.Text + (progress.Source.Length > 0 ? "\n" + progress.Source : ""));
        row.ProgressBar.IsIndeterminate = active || background
            ? progress.Stage is not ("下载" or "等待重试" or "切换下载源") : false;
        row.ProgressBar.Value = progress.Percent;
    }

    private void DownloadOrCancelModel(DownloadableModel model)
    {
        var row = _modelRows[model.Id];
        if (row.Cancellation is not null && row.Action == "download" || model.Id == ModelCatalog.LamaId && ModelInstallation.Installing)
            CancelModelDownload(model);
        else _ = RunModelActionAsync(model, "download");
    }

    private void CancelModelDownload(DownloadableModel model)
    {
        var row = _modelRows[model.Id];
        if (row.Cancellation is not null) row.Cancellation.Cancel();
        else if (model.Id == ModelCatalog.LamaId) ModelInstallation.Cancel();
        row.Download.IsEnabled = false;
        row.Status.Text = Localization.Text("正在停止…");
        ToolTip.SetTip(row.Status, row.Status.Text);
    }

    private async Task RunModelActionAsync(DownloadableModel model, string action)
    {
        if (_modelsClosed) return;
        var row = _modelRows[model.Id];
        if (row.Cancellation is not null || _modelStore.IsBusy(model.Id) || model.Id == ModelCatalog.LamaId && ModelInstallation.Installing) return;
        if (action == "delete" && model.Id == ModelCatalog.LamaId && _settings.AutoDownloadRepairModel)
        {
            row.ErrorMessage = "请先关闭自动下载图片修复模型并应用，再删除。";
            UpdateModelRow(model, row); return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var notificationKey = "model:" + model.Id + ":" + Guid.NewGuid().ToString("N");
        row.Cancellation = cancellation; row.Action = action;
        row.Outcome = row.ErrorMessage = null; row.DownloadFailed = false;
        row.Progress = new(0, 0, action == "delete" ? "删除中…" : "校验模型");
        void NotifyProgress(ModelDownloadProgress value) => Notifications.NotificationCenter.Shared.Publish(this,
            new(notificationKey, "下载模型", (FormattableString)$"{model.Name}\n{Localization.Key(value.Stage)} · {value.Received / 1048576d:0.0} / {value.Total / 1048576d:0.0} MB",
                Notifications.NotificationKind.Progress,
                [new("停止下载", () => { CancelModelDownload(model); return Task.CompletedTask; }, Enabled: () => row.Cancellation is not null && !row.Cancellation.IsCancellationRequested)],
                value.Total > 0 ? value.Percent : null, value.Total <= 0));
        if (action == "download") NotifyProgress(row.Progress);
        try
        {
            ModelStatus.IsVisible = false;
            UpdateModelRow(model, row);
            if (action == "download")
            {
                var progress = new Progress<ModelDownloadProgress>(value =>
                {
                    if (_modelsClosed || cancellation.IsCancellationRequested || row.Cancellation != cancellation) return;
                    try { row.Progress = value; UpdateModelRow(model, row); NotifyProgress(value); }
                    catch (Exception error) { AppDiagnostics.Record("Model download progress", error); }
                });
                await Task.Run(() => _modelStore.DownloadAsync(model.Id, progress, cancellation.Token), cancellation.Token);
            }
            else if (action == "verify")
            {
                using var lease = await Task.Run(() => _modelStore.AcquireAsync(model.Id, cancellation.Token), cancellation.Token);
            }
            else await _modelStore.DeleteAsync(model.Id, cancellation.Token);
            if (model.Id == ModelCatalog.LamaId && action != "delete") ModelInstallation.ClearFailure();
            row.Outcome = action == "verify" ? "校验通过" : action == "delete" ? "未下载" : "已下载";
            Notifications.NotificationCenter.Shared.Publish(this, new(notificationKey, "模型操作完成",
                (FormattableString)$"{model.Name} · {Localization.Key(row.Outcome)}", Notifications.NotificationKind.Success,
                [new("模型管理", Notifications.ModelNotifications.OpenManagementAsync)]));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            row.Outcome = "已停止";
            Notifications.NotificationCenter.Shared.Publish(this, new(notificationKey, "模型操作已停止", model.Name), show: false);
        }
        catch (Exception error)
        {
            row.Outcome = action == "download" ? "下载失败" : action == "verify" ? "校验失败" : "删除失败";
            row.DownloadFailed = action == "download"; row.ErrorMessage = error.Message;
            AppDiagnostics.Record("Model management " + action + " " + model.Id, error);
            Notifications.NotificationCenter.Shared.Publish(this, new(notificationKey, "模型操作失败",
                (FormattableString)$"{model.Name}\n{error.Message}", Notifications.NotificationKind.Error, [
                    new("重试", () => { _ = RunModelActionAsync(model, action); return Task.CompletedTask; }, Primary: true, DismissOnSuccess: true,
                        Enabled: () => !_modelsClosed && row.Cancellation is null && !_modelStore.IsBusy(model.Id)),
                    new("模型管理", Notifications.ModelNotifications.OpenManagementAsync)]));
        }
        finally
        {
            row.Cancellation = null; row.Action = null; row.Progress = null;
            if (!_modelsClosed) await RefreshModelsSafelyAsync();
        }
    }
}
