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
        SettingsSearchInput.Text = ""; ModelSearchInput.Text = ""; ModelFilterInput.SelectedIndex = 0;
        _modelTarget = modelId; _onlineTarget = false; SettingsTabs.SelectedItem = ModelsTab;
        ModelSettingsTabs.SelectedItem = ModelLibraryTab;
        if (IsVisible) FocusModelTarget();
    }
    public void OpenOnlineAiSettings()
    {
        SettingsSearchInput.Text = "";
        _modelTarget = null; _onlineTarget = true; SettingsTabs.SelectedItem = ProvidersTab;
        if (IsVisible) FocusModelTarget();
    }
    private void FocusModelTarget() => Dispatcher.UIThread.Post(() =>
    {
        if (_modelsClosed) return;
        if (_onlineTarget)
        {
            var input = ProviderSetupSection.IsVisible && string.IsNullOrWhiteSpace(OnlineKeyInput.Text) ? OnlineKeyInput : OnlineEndpointInput;
            input.BringIntoView(); input.Focus();
        }
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
        public required MenuItem Verify { get; init; }
        public required MenuItem Delete { get; init; }
        public required MenuItem Import { get; init; }
        public CancellationTokenSource? Cancellation { get; set; }
        public string? Outcome { get; set; }
        public string? ErrorMessage { get; set; }
        public bool Installed { get; set; }
        public bool Busy { get; set; }
        public long Downloaded { get; set; }
        public ModelDownloadProgress? Progress { get; set; }
    }

    private static readonly string[] ModelSourceNames = ["自动", "Hugging Face", "ModelScope", "HF-Mirror", "自定义"];
    private void PopulateModelSource(AppSettings source)
    {
        ModelSourceInput.ItemsSource ??= ModelSourceNames;
        var preference = ModelSourcePreference.From(source);
        ModelSourceInput.SelectedIndex = (int)preference.Kind; ModelSourceUrlInput.Text = preference.CustomUrl;
        UpdateModelSourceHint();
    }
    private void UpdateModelSourceHint()
    {
        var kind = (ModelSourceKind)Math.Max(0, ModelSourceInput.SelectedIndex);
        ModelSourceUrlInput.IsVisible = kind == ModelSourceKind.Custom;
        ModelSourceUrlInput.IsEnabled = kind == ModelSourceKind.Custom;
        ModelSourceHint.Text = Localization.Text(kind switch
        {
            ModelSourceKind.Auto => ModelDownloadSources.PrefersChinaSources()
                ? "自动：当前地区依次尝试 ModelScope、HF-Mirror、Hugging Face。" : "自动：依次尝试 Hugging Face、ModelScope、HF-Mirror。",
            ModelSourceKind.Custom => "填写兼容 Hugging Face 路径的镜像基址，例如 https://hf-mirror.com；失败时回退到其它来源。",
            _ => "优先使用所选来源；失败或该模型没有此来源时回退到其它来源。所有文件都校验大小与 SHA-256。"
        });
    }

    private void InitializeModelManagement()
    {
        ModelSourceInput.SelectionChanged += (_, _) => { UpdateModelSourceHint(); MarkDirty(); };
        ModelSourceUrlInput.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) MarkDirty(); };
        _values.Add(() => ModelSourceInput.SelectedIndex); _values.Add(() => ModelSourceUrlInput.Text);
        _appliedValues = _values.Select(value => value()).ToArray();
        ModelFilterInput.SelectedIndex = 0;
        ModelSearchInput.TextChanged += (_, _) => FilterModelRows();
        ModelFilterInput.SelectionChanged += (_, _) => FilterModelRows();
        foreach (var model in ModelCatalog.All)
        {
            var status = Ui.Text("读取状态…", "caption");
            status.TextWrapping = TextWrapping.NoWrap; status.TextTrimming = TextTrimming.CharacterEllipsis;
            var name = Ui.Text(model.Name, "settingsHeading");
            name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(name, model.Name);
            var identity = new Grid { ColumnDefinitions = new("*,120"), ColumnSpacing = 12 };
            identity.Children.Add(name); Grid.SetColumn(status, 1); identity.Children.Add(status);
            var metadata = Ui.FormattedText($"{Localization.Key(model.Purpose)} · {ModelSize(model.DownloadSize)} · {model.License}", "caption");
            metadata.TextWrapping = TextWrapping.NoWrap; metadata.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(metadata, Localization.Format($"{Localization.Key(model.Purpose)} · {ModelSize(model.DownloadSize)} · {model.License}"));

            var download = Ui.Button("下载", () => DownloadOrCancelModel(model));
            var verify = new MenuItem { Header = "校验" }; verify.Click += (_, _) => _ = RunModelActionAsync(model, "verify");
            var delete = new MenuItem { Header = "删除" }; delete.Click += (_, _) => _ = RunModelActionAsync(model, "delete");
            var import = new MenuItem { Header = "导入本地文件…" };
            ToolTip.SetTip(import, "选择已下载的模型文件或所在文件夹，按大小与 SHA-256 校验后安装");
            var importFiles = new MenuItem { Header = "选择文件…" }; importFiles.Click += (_, _) => _ = ImportModelAsync(model, folder: false);
            var importFolder = new MenuItem { Header = "选择文件夹…" }; importFolder.Click += (_, _) => _ = ImportModelAsync(model, folder: true);
            import.Items.Add(importFiles); import.Items.Add(importFolder);
            var more = new Button { Content = "更多…", Classes = { "field-action" }, MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right,
                Flyout = new MenuFlyout { Items = { import, verify, delete } } };
            download.Classes.Add("primary");
            // Keep the action slot and focused control stable when download becomes cancel.
            var buttons = new Grid { ColumnDefinitions = new("*,112,88"), ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(metadata);
            download.Classes.Add("field-action"); Grid.SetColumn(download, 1); buttons.Children.Add(download);
            Grid.SetColumn(more, 2); buttons.Children.Add(more);

            var progressText = Ui.Text("", "caption");
            progressText.TextWrapping = TextWrapping.NoWrap; progressText.TextTrimming = TextTrimming.CharacterEllipsis;
            var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, MinHeight = 4 };
            var progressPanel = new StackPanel { Spacing = 4, IsVisible = false };
            progressPanel.Children.Add(progressText); progressPanel.Children.Add(progressBar);
            var error = Ui.Text("", "settingError"); error.IsVisible = false; error.VerticalAlignment = VerticalAlignment.Top;
            error.TextWrapping = TextWrapping.NoWrap; error.TextTrimming = TextTrimming.CharacterEllipsis;
            // Progress and errors share a reserved area so actions never resize a model row.
            var details = new Grid { Height = 26, ClipToBounds = true };
            details.Children.Add(progressPanel); details.Children.Add(error);
            var panel = new Grid { RowDefinitions = new("Auto,Auto,Auto,26"), RowSpacing = 6 };
            panel.Children.Add(identity); Grid.SetRow(buttons, 1); panel.Children.Add(buttons);
            if (MediaTagRuntime.Supports(model.Id))
            {
                var runtime = new Controls.ModelRuntimeView(_modelStore.Root, model.Id);
                Grid.SetRow(runtime, 2); panel.Children.Add(runtime);
            }
            Grid.SetRow(details, 3); panel.Children.Add(details);
            var container = new Border { Classes = { "settingSection", "modelRow" }, Child = panel };
            ModelList.Children.Add(container);
            _modelRows.Add(model.Id, new()
            {
                Status = status, Error = error, ProgressPanel = progressPanel, ProgressText = progressText, ProgressBar = progressBar,
                Download = download, Verify = verify, Delete = delete, Import = import, Container = container
            });
        }
        ModelStatus.IsVisible = false;
        Opened += async (_, _) => { FocusModelTarget(); await RefreshModelsSafelyAsync(); };
        ModelInstallation.Changed += RepairModelChanged;
        ModelDownloads.Shared.Changed += ModelDownloadChanged;
        Localization.Changed += ModelsLanguageChanged;
        Closed += (_, _) =>
        {
            _modelsClosed = true;
            ModelInstallation.Changed -= RepairModelChanged;
            ModelDownloads.Shared.Changed -= ModelDownloadChanged;
            Localization.Changed -= ModelsLanguageChanged;
        };
    }

    private void FilterModelRows()
    {
        var query = ModelSearchInput.Text?.Trim() ?? "";
        foreach (var model in ModelCatalog.All)
        {
            if (!_modelRows.TryGetValue(model.Id, out var row)) continue;
            var stateMatches = ModelFilterInput.SelectedIndex switch
            {
                1 => row.Installed,
                2 => !row.Installed,
                3 => ModelDownloads.Shared.Find(model.Id)?.Active == true || model.Id == ModelCatalog.LamaId && ModelInstallation.Installing,
                4 => ModelCatalog.RequiresSummaryRuntime(model.Id),
                _ => true
            };
            row.Container.IsVisible = stateMatches && (query.Length == 0
                || new[] { model.Id, model.Name, Localization.Text(model.Purpose) }.Any(text => text.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
        }
    }

    private void ModelsLanguageChanged(object? sender, EventArgs args)
    {
        foreach (var model in ModelCatalog.All) UpdateModelRow(model, _modelRows[model.Id]);
    }

    private void ModelDownloadChanged(string id)
    {
        if (_modelsClosed) return;
        var row = _modelRows[id]; row.Outcome = row.ErrorMessage = null;
        UpdateModelRow(ModelCatalog.Find(id), row);
        if (ModelDownloads.Shared.Find(id)?.Active != true) _ = RefreshModelsSafelyAsync();
    }

    private void RepairModelChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_modelsClosed) return;
        try
        {
            var installing = ModelInstallation.Installing;
            var row = _modelRows[ModelCatalog.LamaId];
            if (installing && !_repairWasInstalling && row.Cancellation is null)
            { row.Outcome = row.ErrorMessage = null; }
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
            if (row.Cancellation is not null || ModelDownloads.Shared.Find(model.Id)?.Active == true)
            { UpdateModelRow(model, row); continue; }
            var installed = await _modelStore.IsInstalledAsync(model.Id, ct: _lifetime.Token);
            if (_modelsClosed) return;
            if (row.Cancellation is not null || ModelDownloads.Shared.Find(model.Id)?.Active == true)
            { UpdateModelRow(model, row); continue; }
            row.Installed = installed; row.Busy = _modelStore.IsBusy(model.Id);
            row.Downloaded = _modelStore.DownloadedBytes(model.Id);
            UpdateModelRow(model, row);
        }
    }

    private void UpdateModelRow(DownloadableModel model, ModelRow row)
    {
        var download = ModelDownloads.Shared.Find(model.Id);
        var background = model.Id == ModelCatalog.LamaId && ModelInstallation.Installing;
        var backgroundFailure = model.Id == ModelCatalog.LamaId && ModelInstallation.Failed && !row.Installed;
        var active = row.Cancellation is not null || download?.Active == true;
        var downloading = background || download?.Active == true;
        var runtime = MediaTagRuntime.Status(_modelStore.Root, model.Id);
        var busy = active || background || row.Busy || runtime.Preparing || runtime.State == ModelLoadState.InUse;
        var progress = download?.Active == true ? download.Progress : row.Progress;
        if (background)
            progress = (ModelInstallation.Progress ?? new(ModelInstallation.Received, ModelInstallation.Total, ModelInstallation.Stage,
                ModelInstallation.RetryAttempt, ModelInstallation.MaxAttempts)) with { Stage = ModelInstallation.Stage };
        var status = !model.Supported ? "当前平台不可用" : background ? ModelInstallation.Stage
            : active ? progress?.Stage ?? "处理中…" : row.Busy ? "使用中" : row.Outcome ?? download?.Outcome ?? (backgroundFailure ? "下载失败" : row.Installed ? "已下载" : "未下载");
        row.Status.Text = Localization.Text(status);
        ToolTip.SetTip(row.Status, row.Status.Text);
        row.Download.IsEnabled = downloading
            ? download?.CancellationRequested != true && (!background || !ModelInstallation.CancellationRequested)
            : model.Supported && !busy;
        row.Download.Content = Localization.Text(downloading ? "取消下载" : row.Downloaded > 0 && !row.Installed ? "继续下载"
            : download?.Error is not null || backgroundFailure ? "重试下载" : row.Installed ? "修复下载" : "下载");
        row.Download.Classes.Set("primary", downloading || !row.Installed);
        row.Verify.IsEnabled = row.Installed && !busy;
        row.Delete.IsEnabled = !busy && _modelStore.HasLocalData(model.Id);
        row.Import.IsEnabled = model.Supported && !busy && model.Files.Count > 0;
        row.Error.Text = Localization.Text(row.ErrorMessage ?? download?.Error ?? (backgroundFailure ? ModelInstallation.Error ?? "图片修复模型安装失败，请检查网络后重试。" : ""));
        row.Error.IsVisible = !string.IsNullOrEmpty(row.Error.Text);
        ToolTip.SetTip(row.Error, row.Error.IsVisible ? row.Error.Text : null);
        FilterModelRows();

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
            row.ProgressText.Text += " · " + Localization.Format($"下载源 {progress.SourceIndex}/{progress.SourceCount} · {progress.SourceName}");
        ToolTip.SetTip(row.ProgressText, row.ProgressText.Text + (progress.Source.Length > 0 ? "\n" + progress.Source : ""));
        row.ProgressBar.IsIndeterminate = active || background
            ? progress.Stage is not ("下载" or "等待重试" or "切换下载源" or "导入文件") : false;
        row.ProgressBar.Value = progress.Percent;
    }

    private async Task ImportModelAsync(DownloadableModel model, bool folder)
    {
        if (_modelsClosed) return;
        var row = _modelRows[model.Id];
        if (row.Cancellation is not null || ModelDownloads.Shared.Find(model.Id)?.Active == true || _modelStore.IsBusy(model.Id)
            || model.Id == ModelCatalog.LamaId && ModelInstallation.Installing) return;
        var paths = folder ? (await Ui.Folder(this, "选择模型文件夹") is { } path ? [path] : [])
            : await Ui.Pick(this, "选择模型文件");
        if (paths.Length == 0 || _modelsClosed) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        ModelDownloads.Shared.ClearResult(model.Id);
        var notificationKey = "model:" + model.Id + ":" + Guid.NewGuid().ToString("N");
        row.Cancellation = cancellation; row.Outcome = row.ErrorMessage = null;
        row.Progress = new(0, model.DownloadSize, "校验文件");
        var progress = new Progress<ModelDownloadProgress>(value =>
        { if (!_modelsClosed && row.Cancellation == cancellation) { row.Progress = value; UpdateModelRow(model, row); } });
        try
        {
            ModelStatus.IsVisible = false; UpdateModelRow(model, row);
            await Task.Run(() => _modelStore.ImportAsync(model.Id, paths, progress, cancellation.Token), cancellation.Token);
            if (model.Id == ModelCatalog.LamaId) ModelInstallation.ClearFailure();
            row.Outcome = "已导入";
            Notifications.NotificationCenter.Shared.Publish(this, new(notificationKey, "模型操作完成",
                (FormattableString)$"{model.Name} · {Localization.Key(row.Outcome)}", Notifications.NotificationKind.Success,
                [new("模型管理", Notifications.ModelNotifications.OpenManagementAsync)]));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { row.Outcome = "已停止"; }
        catch (Exception error)
        {
            row.Outcome = "导入失败"; row.ErrorMessage = error.Message;
            AppDiagnostics.Record("Model import " + model.Id, error);
            Notifications.NotificationCenter.Shared.Publish(this, new(notificationKey, "模型操作失败",
                (FormattableString)$"{model.Name}\n{error.Message}", Notifications.NotificationKind.Error,
                [new("模型管理", Notifications.ModelNotifications.OpenManagementAsync)]));
        }
        finally
        {
            row.Cancellation = null; row.Progress = null;
            if (!_modelsClosed) await RefreshModelsSafelyAsync();
        }
    }

    private void DownloadOrCancelModel(DownloadableModel model)
    {
        var row = _modelRows[model.Id];
        if (ModelDownloads.Shared.Find(model.Id)?.Active == true || model.Id == ModelCatalog.LamaId && ModelInstallation.Installing)
            CancelModelDownload(model);
        else if (row.Cancellation is null)
        {
            try
            {
                var preference = new ModelSourcePreference((ModelSourceKind)Math.Max(0, ModelSourceInput.SelectedIndex),
                    ModelSourceUrlInput.Text?.Trim() ?? "");
                ModelDownloads.Shared.Start(model, this, preference);
                StatusText.IsVisible = false;
            }
            catch (ArgumentException error)
            {
                StatusText.Text = error.Message; StatusText.IsVisible = true;
                ModelSourceUrlInput.Focus();
                if (_settings.PlayErrorSound) _services.PlaySound(UiSound.Error);
            }
        }
    }

    private void CancelModelDownload(DownloadableModel model)
    {
        var row = _modelRows[model.Id];
        if (ModelDownloads.Shared.Find(model.Id)?.Active == true) ModelDownloads.Shared.Cancel(model.Id);
        else if (model.Id == ModelCatalog.LamaId) ModelInstallation.Cancel();
        row.Download.IsEnabled = false;
        row.Status.Text = Localization.Text("正在停止…");
        ToolTip.SetTip(row.Status, row.Status.Text);
    }

    private async Task RunModelActionAsync(DownloadableModel model, string action)
    {
        if (_modelsClosed) return;
        var row = _modelRows[model.Id];
        if (row.Cancellation is not null || ModelDownloads.Shared.Find(model.Id)?.Active == true
            || _modelStore.IsBusy(model.Id) || model.Id == ModelCatalog.LamaId && ModelInstallation.Installing) return;
        if (action == "delete" && model.Id == ModelCatalog.LamaId && _settings.AutoDownloadRepairModel)
        {
            row.ErrorMessage = "请先关闭自动下载图片修复模型并应用，再删除。";
            UpdateModelRow(model, row); return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        ModelDownloads.Shared.ClearResult(model.Id);
        var notificationKey = "model:" + model.Id + ":" + Guid.NewGuid().ToString("N");
        row.Cancellation = cancellation;
        row.Outcome = row.ErrorMessage = null;
        row.Progress = new(0, 0, action == "delete" ? "删除中…" : "校验模型");
        try
        {
            ModelStatus.IsVisible = false;
            UpdateModelRow(model, row);
            if (action == "verify")
            {
                using var lease = await Task.Run(() => _modelStore.AcquireAsync(model.Id, cancellation.Token, forceVerification: true), cancellation.Token);
            }
            else await _modelStore.DeleteAsync(model.Id, cancellation.Token);
            if (model.Id == ModelCatalog.LamaId && action != "delete") ModelInstallation.ClearFailure();
            row.Outcome = action == "verify" ? "校验通过" : "未下载";
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
            row.Outcome = action == "verify" ? "校验失败" : "删除失败";
            row.ErrorMessage = error.Message;
            AppDiagnostics.Record("Model management " + action + " " + model.Id, error);
            Notifications.NotificationCenter.Shared.Publish(this, new(notificationKey, "模型操作失败",
                (FormattableString)$"{model.Name}\n{error.Message}", Notifications.NotificationKind.Error, [
                    new("重试", () => { _ = RunModelActionAsync(model, action); return Task.CompletedTask; }, Primary: true, DismissOnSuccess: true,
                        Enabled: () => !_modelsClosed && row.Cancellation is null && !_modelStore.IsBusy(model.Id)),
                    new("模型管理", Notifications.ModelNotifications.OpenManagementAsync)]));
        }
        finally
        {
            row.Cancellation = null; row.Progress = null;
            if (!_modelsClosed) await RefreshModelsSafelyAsync();
        }
    }
}
