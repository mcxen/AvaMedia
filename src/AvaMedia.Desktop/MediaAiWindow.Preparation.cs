using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class MediaAiWindow
{
    private readonly TextBlock _warmStatus = Ui.Text("", "caption");
    private readonly Button _warmStart = new() { Content = "预热模型" };
    private readonly Button _warmStop = new() { Content = "停止预热", IsVisible = false };
    private readonly Button _warmRelease = new() { Content = "释放模型", IsVisible = false };
    private readonly ProgressBar _warmProgress = new() { Height = 3, MinHeight = 3, IsIndeterminate = true, IsVisible = false };
    private readonly Dictionary<string, Control> _runtimeModels = [];
    private readonly ModelStore _runtimeStore = new();
    private CancellationTokenSource? _warmRequest;
    private bool _warmFailed, _releasingWarmModels;

    private Control BuildModelPreparation()
    {
        var badges = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, label) in new[] { (ModelCatalog.JoyTagId, "标签"), (ModelCatalog.NsfwId, "成人内容"), (ModelCatalog.EmbeddingId, "场景") })
        {
            var view = new Controls.ModelRuntimeView(_runtimeStore.Root, id, label) { Margin = new(0, 0, 18, 0) };
            _runtimeModels.Add(id, view); badges.Children.Add(view);
        }
        StableLayout.Reserve(_warmStart, "预热模型", "重试预热", "停止预热");
        StableLayout.Reserve(_warmStop, "预热模型", "重试预热", "停止预热");
        StableLayout.Reserve(_warmRelease, "释放模型");
        StableLayout.SetStatusLines(_warmStatus, 2);
        var primary = new Grid(); primary.Children.Add(_warmStart); primary.Children.Add(_warmStop);
        primary.Bind(WidthProperty, new Avalonia.Data.Binding(nameof(Button.Width)) { Source = _warmStart });
        var release = new Border { Child = _warmRelease };
        release.Bind(WidthProperty, new Avalonia.Data.Binding(nameof(Button.Width)) { Source = _warmRelease });
        var actions = new Grid { ColumnDefinitions = new("Auto,Auto,Auto,*"), ColumnSpacing = 8 };
        actions.Children.Add(primary); Grid.SetColumn(release, 1); actions.Children.Add(release);
        var management = Ui.Button("模型管理…", async () => await ManageModelsAsync());
        Grid.SetColumn(management, 2); actions.Children.Add(management);
        Grid.SetColumn(_warmStatus, 3); actions.Children.Add(_warmStatus);
        var progressSlot = new Grid { Height = 3 }; progressSlot.Children.Add(_warmProgress);
        var panel = new StackPanel { Spacing = 5 }; panel.Children.Add(badges); panel.Children.Add(progressSlot); panel.Children.Add(actions);
        _warmStart.Click += async (_, _) => await PrepareModelsAsync(reset: _warmFailed);
        _warmStop.Click += (_, _) =>
        {
            var request = _warmRequest; _warmRequest = null;
            _warmStatus.Text = Localization.Text("预热已停止，开始分析时将按需加载");
            UpdateModelPreparationActions();
            request?.Cancel();
        };
        _warmRelease.Click += async (_, _) =>
        {
            _releasingWarmModels = true; UpdateModelPreparationActions();
            try
            {
                var released = await Task.Run(() => MediaTagRuntime.ReleaseAsync(_runtimeStore.Root, _lifetime.Token), _lifetime.Token);
                if (!_closed) _status.Text = Localization.Text(released ? "空闲模型已释放" : "模型正在使用，任务结束后可释放");
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!_closed) _status.Text = error.Message; }
            finally { _releasingWarmModels = false; if (!_closed) UpdateModelPreparationActions(); }
        };
        MediaTagRuntime.Changed += RuntimeModelsChanged;
        Closed += (_, _) => MediaTagRuntime.Changed -= RuntimeModelsChanged;
        return panel;
    }
    private void RuntimeModelsChanged(string root)
    {
        if (BatchRename.PathComparer.Equals(root, _runtimeStore.Root)) Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            UpdateModelPreparationActions();
            if (!_busy && _warmRequest is null && _status.Text == Localization.Text("模型已就绪")
                && !MediaTagRuntime.Status(_runtimeStore.Root, ModelCatalog.JoyTagId).Loaded)
                _status.Text = Localization.Text("就绪");
        });
    }
    private void UpdateModelPreparationActions()
    {
        if (_runtimeModels.Count == 0) return;
        _runtimeModels[ModelCatalog.NsfwId].IsVisible = _settings.EnableNsfwContent && _realPeople.IsChecked == true;
        _runtimeModels[ModelCatalog.EmbeddingId].IsVisible = NeedsSemanticModel;
        var states = _runtimeModels.Select(item => (Status: MediaTagRuntime.Status(_runtimeStore.Root, item.Key), Visible: item.Value.IsVisible)).ToArray();
        var loading = _warmRequest is not null;
        var occupied = states.Any(item => item.Status.Preparing || item.Status.State == ModelLoadState.InUse);
        _warmStart.Content = Localization.Text(_warmFailed ? "重试预热" : "预热模型");
        _warmStart.IsVisible = !loading && (_warmFailed || states.Any(item => item.Visible && !item.Status.Loaded));
        _warmStart.IsEnabled = !_busy && !_releasingWarmModels && _modelReady && !occupied;
        _warmStop.IsVisible = loading; _warmStop.IsEnabled = !_busy && _warmRequest?.IsCancellationRequested != true;
        _warmRelease.IsVisible = states.Any(item => item.Status.Loaded);
        _warmRelease.IsEnabled = !_busy && !loading && !occupied && !_releasingWarmModels;
        _warmProgress.IsVisible = loading && !_busy;
    }

    private Task PrepareModelsAsync(bool reset = false)
    {
        if (_closed || _busy) return Task.CompletedTask;
        _warmRequest?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _warmRequest = request;
        _warmFailed = false;
        UpdateModelPreparationActions();
        var options = new MediaTagOptions(PreferGpu: _gpu.IsChecked == true, BatchSize: 1, RecognizeScenes: _sceneTags.IsChecked == true)
        {
            SemanticCandidates = SemanticLibraryCandidates,
            RecognizeNsfw = _settings.EnableNsfwContent && _realPeople.IsChecked == true
        };
        return PrepareModelsAsync(options, request, reset);
    }

    private async Task PrepareModelsAsync(MediaTagOptions options, CancellationTokenSource request, bool reset)
    {
        using (request)
        {
            _warmStatus.Text = Localization.Text("准备本地模型");
            ToolTip.SetTip(_warmStatus, null);
            var progress = new Progress<AiActivity>(value =>
            {
                if (_closed || _busy || _warmRequest != request || request.IsCancellationRequested) return;
                _warmStatus.Text = Localization.Text(value.Stage)
                    + (value.Current is { } current && value.Total is > 0 and var total ? $" · {current:0}/{total:0}" : "");
            });
            try
            {
                if (reset) await _tagService.ResetPreparedModelsAsync(request.Token);
                var ready = await _tagService.WarmAsync(options, progress, request.Token);
                if (_closed || _busy || _warmRequest != request || request.IsCancellationRequested) return;
                _warmStatus.Text = "";
                if (ready && !_modelStatus.IsVisible && !_busy && _status.Text == Localization.Text("就绪"))
                    _status.Text = Localization.Text("模型已就绪");
            }
            catch (OperationCanceledException)
            {
                if (!_closed && _warmRequest == request)
                {
                    _warmStatus.Text = "";
                    if (!_busy) _status.Text = Localization.Text("预热已停止，开始分析时将按需加载");
                }
            }
            catch (Exception error)
            {
                AppDiagnostics.Record("AI model preparation", error);
                if (_closed || _busy || _warmRequest != request) return;
                _warmStatus.Text = Localization.Text("模型准备失败");
                ToolTip.SetTip(_warmStatus, error.Message); _warmFailed = true;
            }
            finally
            {
                if (_warmRequest == request) { _warmRequest = null; if (!_closed) UpdateModelPreparationActions(); }
            }
        }
    }
}
