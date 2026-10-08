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
    public void OpenModelManagement() => SettingsTabs.SelectedItem = ModelsTab;

    private sealed class ModelRow
    {
        public required TextBlock Status { get; init; }
        public required TextBlock Error { get; init; }
        public required StackPanel ProgressPanel { get; init; }
        public required TextBlock ProgressText { get; init; }
        public required ProgressBar ProgressBar { get; init; }
        public required Button Download { get; init; }
        public required Button Verify { get; init; }
        public required Button Delete { get; init; }
        public required Button Cancel { get; init; }
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
            var status = Ui.Text("读取状态…", "caption"); status.TextWrapping = TextWrapping.NoWrap;
            var name = Ui.Text(model.Name, "settingsHeading");
            name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(name, model.Name);
            var identity = new Grid { ColumnDefinitions = new("*,Auto"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 12, RowSpacing = 3 };
            identity.Children.Add(name); Grid.SetColumn(status, 1); identity.Children.Add(status);
            var metadata = Ui.FormattedText($"{Localization.Key(model.Purpose)} · {ModelSize(model.DownloadSize)} · {model.License}", "caption");
            metadata.TextWrapping = TextWrapping.NoWrap; metadata.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(metadata, Localization.Format($"{Localization.Key(model.Purpose)} · {ModelSize(model.DownloadSize)} · {model.License}"));
            Grid.SetRow(metadata, 1); Grid.SetColumnSpan(metadata, 2); identity.Children.Add(metadata);

            var download = Ui.Button("下载", () => _ = RunModelActionAsync(model, "download"));
            var verify = Ui.Button("校验", () => _ = RunModelActionAsync(model, "verify"));
            var delete = Ui.Button("删除", () => _ = RunModelActionAsync(model, "delete"));
            var cancel = Ui.Button("取消下载", () => CancelModelDownload(model));
            cancel.IsVisible = false; download.Classes.Add("primary");
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            foreach (var button in new[] { download, cancel, verify, delete })
            { button.Classes.Add("field-action"); buttons.Children.Add(button); }

            var progressText = Ui.Text("", "caption");
            var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, MinHeight = 4 };
            var progressPanel = new StackPanel { Spacing = 4, Margin = new(0, 6, 0, 0), IsVisible = false };
            progressPanel.Children.Add(progressText); progressPanel.Children.Add(progressBar);
            var error = Ui.Text("", "settingError"); error.IsVisible = false; error.Margin = new(0, 6, 0, 0);
            var panel = new Grid { ColumnDefinitions = new("*,Auto"), RowDefinitions = new("Auto,Auto,Auto"), ColumnSpacing = 16 };
            panel.Children.Add(identity); Grid.SetColumn(buttons, 1); panel.Children.Add(buttons);
            Grid.SetRow(progressPanel, 1); Grid.SetColumnSpan(progressPanel, 2); panel.Children.Add(progressPanel);
            Grid.SetRow(error, 2); Grid.SetColumnSpan(error, 2); panel.Children.Add(error);
            ModelList.Children.Add(new Border { Classes = { "settingSection", "modelRow" }, Child = panel });
            _modelRows.Add(model.Id, new()
            {
                Status = status, Error = error, ProgressPanel = progressPanel, ProgressText = progressText, ProgressBar = progressBar,
                Download = download, Verify = verify, Delete = delete, Cancel = cancel
            });
        }
        ModelStatus.IsVisible = false;
        Opened += async (_, _) => await RefreshModelsSafelyAsync();
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
        var progress = background ? new ModelDownloadProgress(ModelInstallation.Received, ModelInstallation.Total, ModelInstallation.Stage,
            ModelInstallation.RetryAttempt, ModelInstallation.MaxAttempts) : row.Progress;
        var status = !model.Supported ? "当前平台不可用" : background ? ModelInstallation.Stage
            : active ? progress?.Stage ?? "处理中…" : row.Busy ? "使用中" : row.Outcome ?? (backgroundFailure ? "下载失败" : row.Installed ? "已下载" : "未下载");
        row.Status.Text = Localization.Text(status);
        row.Download.IsVisible = !downloading; row.Cancel.IsVisible = downloading;
        row.Download.IsEnabled = model.Supported && !busy;
        row.Cancel.IsEnabled = row.Cancellation?.IsCancellationRequested != true && (!background || !ModelInstallation.CancellationRequested);
        row.Download.Content = Localization.Text(row.Downloaded > 0 && !row.Installed ? "继续下载"
            : row.DownloadFailed || backgroundFailure ? "重试下载" : row.Installed ? "修复下载" : "下载");
        row.Verify.IsEnabled = row.Installed && !busy;
        row.Delete.IsEnabled = !busy && _modelStore.HasLocalData(model.Id);
        row.Error.Text = Localization.Text(row.ErrorMessage ?? (backgroundFailure ? ModelInstallation.Error ?? "图片修复模型安装失败，请检查网络后重试。" : ""));
        row.Error.IsVisible = !string.IsNullOrEmpty(row.Error.Text);

        if (!active && !background)
            progress = row.Downloaded > 0 && !row.Installed ? new(row.Downloaded, model.DownloadSize, "下载未完成") : null;
        row.ProgressPanel.IsVisible = progress is not null;
        if (progress is null) return;
        row.ProgressText.Text = Localization.Text(progress.Stage);
        if (progress.Total > 0)
            row.ProgressText.Text += $" · {ModelSize(progress.Received)} / {ModelSize(progress.Total)} · {progress.Percent}%";
        if (progress.Attempt > 0)
            row.ProgressText.Text += " · " + Localization.Format($"重试 {progress.Attempt}/{progress.MaxAttempts}");
        row.ProgressBar.IsIndeterminate = active || background
            ? progress.Stage is not ("下载" or "等待重试" or "切换下载源") : false;
        row.ProgressBar.Value = progress.Percent;
    }

    private void CancelModelDownload(DownloadableModel model)
    {
        var row = _modelRows[model.Id];
        if (row.Cancellation is not null) row.Cancellation.Cancel();
        else if (model.Id == ModelCatalog.LamaId) ModelInstallation.Cancel();
        row.Cancel.IsEnabled = false;
        row.Status.Text = Localization.Text("正在停止…");
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
        row.Cancellation = cancellation; row.Action = action;
        row.Outcome = row.ErrorMessage = null; row.DownloadFailed = false;
        row.Progress = new(0, 0, action == "delete" ? "删除中…" : "校验模型");
        try
        {
            ModelStatus.IsVisible = false;
            UpdateModelRow(model, row);
            if (action == "download")
            {
                var progress = new Progress<ModelDownloadProgress>(value =>
                {
                    if (_modelsClosed || cancellation.IsCancellationRequested || row.Cancellation != cancellation) return;
                    try { row.Progress = value; UpdateModelRow(model, row); }
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
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            row.Outcome = "已停止";
        }
        catch (Exception error)
        {
            row.Outcome = action == "download" ? "下载失败" : action == "verify" ? "校验失败" : "删除失败";
            row.DownloadFailed = action == "download"; row.ErrorMessage = error.Message;
            AppDiagnostics.Record("Model management " + action + " " + model.Id, error);
        }
        finally
        {
            row.Cancellation = null; row.Action = null; row.Progress = null;
            if (!_modelsClosed) await RefreshModelsSafelyAsync();
        }
    }
}
