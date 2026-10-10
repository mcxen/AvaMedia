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
    private bool _releasingWarmModels;
    private readonly Controls.ModelRuntimeView _captionRuntime = new(new ModelStore().Root, ModelCatalog.SummaryQwen35Id, "画面描述");

    private void UpdateCaptionWarmup()
    {
        var local = _generateCaptions.IsChecked == true && _captionLocalModelId is not null;
        _captionRuntime.IsVisible = local;
        _captionWarmup?.Update(local ? [_captionLocalModelId!] : [], _gpu.IsChecked == true,
            _entries.FirstOrDefault(entry => entry.Include)?.Path);
    }

    private Control BuildModelPreparation()
    {
        var badges = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, label) in new[] { (ModelCatalog.JoyTagId, "标签"), (ModelCatalog.NsfwId, "成人内容"), (ModelCatalog.EmbeddingId, "场景") })
        {
            var view = new Controls.ModelRuntimeView(_runtimeStore.Root, id, label) { Margin = new(0, 0, 18, 0) };
            _runtimeModels.Add(id, view); badges.Children.Add(view);
        }
        _captionRuntime.Width = 310; _captionRuntime.Margin = new(0, 0, 18, 0); badges.Children.Add(_captionRuntime);
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
        _warmStart.Click += (_, _) => _captionWarmup.Retry();
        _warmStop.Click += (_, _) => _captionWarmup.Stop();
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
            if (!_busy && _captionWarmup?.Running != true && _status.Text == Localization.Text("模型已就绪")
                && !MediaTagRuntime.Status(_runtimeStore.Root, ModelCatalog.JoyTagId).Loaded)
                _status.Text = Localization.Text("就绪");
        });
    }
    private void UpdateModelPreparationActions()
    {
        if (_runtimeModels.Count == 0) return;
        _runtimeModels[ModelCatalog.NsfwId].IsVisible = _settings.EnableNsfwContent && _realPeople.IsChecked == true;
        _runtimeModels[ModelCatalog.EmbeddingId].IsVisible = NeedsSemanticModel;
        var states = _runtimeModels.Select(item => MediaTagRuntime.Status(_runtimeStore.Root, item.Key))
            .Append(MediaTagRuntime.Status(_runtimeStore.Root, ModelCatalog.SummaryQwen35Id)).ToArray();
        var loading = _captionWarmup?.Running == true;
        var local = _generateCaptions.IsChecked == true && _captionLocalModelId is not null;
        var caption = MediaTagRuntime.Status(_runtimeStore.Root, ModelCatalog.SummaryQwen35Id);
        _warmStart.Content = Localization.Text(_captionWarmup?.Error is not null ? "重试预热" : "预热模型");
        _warmStart.IsVisible = local && !loading && !caption.Loaded;
        _warmStart.IsEnabled = !_releasingWarmModels && caption.State != ModelLoadState.InUse;
        _warmStop.IsVisible = local && loading; _warmStop.IsEnabled = true;
        _warmRelease.IsVisible = states.Any(state => state.Loaded);
        _warmRelease.IsEnabled = !loading && !_releasingWarmModels && !states.Any(state => state.Preparing || state.State == ModelLoadState.InUse);
        _warmProgress.IsVisible = loading;
        _warmStatus.Text = Localization.Text(_captionWarmup?.Stopped == true ? "预热已停止，开始分析时将按需加载"
            : _captionWarmup?.Error is not null ? "模型准备失败" : "");
        ToolTip.SetTip(_warmStatus, _captionWarmup?.Error?.Message);
    }
}
