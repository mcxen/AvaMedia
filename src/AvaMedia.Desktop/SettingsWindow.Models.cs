using Avalonia.Controls;
using Avalonia.Layout;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class SettingsWindow
{
    private readonly ModelStore _modelStore = new();
    private readonly Dictionary<string, ModelRow> _modelRows = [];
    public void OpenModelManagement() => SettingsTabs.SelectedItem = ModelsTab;
    private sealed class ModelRow
    {
        public required TextBlock Status { get; init; }
        public required ProgressBar Progress { get; init; }
        public required Button Download { get; init; }
        public required Button Verify { get; init; }
        public required Button Delete { get; init; }
        public required Button Cancel { get; init; }
        public CancellationTokenSource? Cancellation { get; set; }
    }
    private void InitializeModelManagement()
    {
        ModelList.Children.Add(new Border { Classes = { "settingSection" }, Child = Ui.Text("YuNet · 人脸方向检测 · 内置") });
        foreach (var model in ModelCatalog.All)
        {
            var status = Ui.Text("读取状态…", "caption");
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, IsVisible = false, Height = 6 };
            var download = Ui.Button("下载", () => _ = RunModelActionAsync(model, "download"));
            var verify = Ui.Button("校验", () => _ = RunModelActionAsync(model, "verify"));
            var delete = Ui.Button("删除", () => _ = RunModelActionAsync(model, "delete"));
            var cancel = Ui.Button("取消下载", () => _modelRows[model.Id].Cancellation?.Cancel());
            cancel.IsVisible = false;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var button in new[] { download, verify, delete, cancel }) { button.Classes.Add("field-action"); buttons.Children.Add(button); }
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(Ui.Text(model.Name, "settingsHeading"));
            panel.Children.Add(Ui.Text(Localization.Text(model.Purpose) + " · " + ModelSize(model.DownloadSize) + " · " + model.License, "caption"));
            panel.Children.Add(status); panel.Children.Add(bar); panel.Children.Add(buttons);
            ModelList.Children.Add(new Border { Classes = { "settingSection" }, Child = panel });
            _modelRows.Add(model.Id, new() { Status = status, Progress = bar, Download = download, Verify = verify, Delete = delete, Cancel = cancel });
        }
        Opened += async (_, _) => await RefreshModelsSafelyAsync();
        ModelInstallation.Changed += RepairModelChanged;
        Closed += (_, _) => ModelInstallation.Changed -= RepairModelChanged;
    }
    private void RepairModelChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(async () => await RefreshModelsSafelyAsync());
    private async Task RefreshModelsSafelyAsync()
    {
        if (_lifetime.IsCancellationRequested) return;
        try { await RefreshModelsAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_lifetime.IsCancellationRequested) ModelStatus.Text = error.Message; }
    }
    private static string ModelSize(long bytes) => bytes >= 1024 * 1024 * 1024
        ? $"{bytes / 1073741824d:0.00} GiB" : $"{bytes / 1048576d:0.0} MiB";
    private async Task RefreshModelsAsync()
    {
        foreach (var model in ModelCatalog.All)
        {
            var row = _modelRows[model.Id];
            if (row.Cancellation is not null) continue;
            var installed = await _modelStore.IsInstalledAsync(model.Id, ct: _lifetime.Token);
            var busy = _modelStore.IsBusy(model.Id);
            row.Status.Text = Localization.Text(!model.Supported ? "当前平台不可用" : busy ? "模型正在使用或下载" : installed ? "已下载" : "未下载");
            row.Download.IsEnabled = model.Supported && !busy; row.Download.Content = Localization.Text(installed ? "修复下载" : "下载");
            row.Verify.IsEnabled = installed && !busy;
            row.Delete.IsEnabled = _modelStore.HasLocalData(model.Id) && !busy;
        }
    }
    private async Task RunModelActionAsync(DownloadableModel model, string action)
    {
        var row = _modelRows[model.Id];
        if (row.Cancellation is not null) return;
        if (action == "delete" && model.Id == ModelCatalog.LamaId && _settings.AutoDownloadRepairModel)
        { ModelStatus.Text = Localization.Text("请先关闭自动下载图片修复模型并应用，再删除。"); return; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        row.Cancellation = cancellation;
        row.Download.IsEnabled = row.Verify.IsEnabled = row.Delete.IsEnabled = false;
        row.Cancel.IsVisible = action == "download"; row.Progress.IsVisible = action != "delete";
        row.Progress.IsIndeterminate = action == "verify"; ModelStatus.Text = "";
        try
        {
            if (action == "download")
            {
                var progress = new Progress<ModelDownloadProgress>(value =>
                {
                    if (_lifetime.IsCancellationRequested || row.Cancellation != cancellation) return;
                    row.Progress.Value = value.Percent;
                    row.Status.Text = Localization.Text(value.Stage) + " · " + value.Percent + "%";
                });
                await Task.Run(() => _modelStore.DownloadAsync(model.Id, progress, cancellation.Token), cancellation.Token);
            }
            else if (action == "verify")
            {
                row.Status.Text = Localization.Text("校验中…");
                using var lease = await Task.Run(() => _modelStore.AcquireAsync(model.Id, cancellation.Token), cancellation.Token);
                ModelStatus.Text = Localization.Text("模型校验通过。");
            }
            else await _modelStore.DeleteAsync(model.Id, cancellation.Token);
        }
        catch (OperationCanceledException) { if (!_lifetime.IsCancellationRequested) ModelStatus.Text = Localization.Text("下载已取消，重新下载可继续。"); }
        catch (Exception error) { if (!_lifetime.IsCancellationRequested) ModelStatus.Text = error.Message; }
        finally
        {
            row.Cancellation = null; row.Cancel.IsVisible = row.Progress.IsVisible = false;
            if (!_lifetime.IsCancellationRequested) await RefreshModelsSafelyAsync();
        }
    }
}
