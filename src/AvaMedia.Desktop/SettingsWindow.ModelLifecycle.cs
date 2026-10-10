using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class SettingsWindow
{
    private static readonly int[] ModelIdleChoices = [1, 5, 15, 30, -1];
    private readonly ComboBox _modelWarmChoice = Ui.Combo(["打开本地大模型工具时", "开始任务时"], "打开本地大模型工具时");
    private readonly NumericUpDown _warmInitialCpu = WarmPercent(), _warmInitialGpu = WarmPercent(),
        _warmCpu = WarmPercent(), _warmGpu = WarmPercent();
    private readonly NumericUpDown _warmRamp = new() { Minimum = 1, Maximum = 30, Increment = 1, Width = 100 };
    private static NumericUpDown WarmPercent() => new() { Minimum = 1, Maximum = 100, Increment = 5, Width = 100 };
    private readonly ComboBox _modelIdleChoice = Ui.Combo(["空闲 1 分钟", "空闲 5 分钟（推荐）", "空闲 15 分钟", "空闲 30 分钟", "保持到退出应用"], "空闲 5 分钟（推荐）");
    private readonly Button _releaseModels = new() { Content = "释放空闲模型", Classes = { "field-action" } };
    private bool _releasingModels;
    private void PopulateModelLifecycle(AppSettings source)
    {
        _modelWarmChoice.SelectedIndex = source.PrewarmLocalModels ? 0 : 1;
        _warmInitialCpu.Value = source.ModelWarmupInitialCpuPercent; _warmInitialGpu.Value = source.ModelWarmupInitialGpuPercent;
        _warmCpu.Value = source.ModelWarmupCpuPercent; _warmGpu.Value = source.ModelWarmupGpuPercent;
        _warmRamp.Value = source.ModelWarmupRampSeconds;
        var index = Array.IndexOf(ModelIdleChoices, source.TagModelIdleMinutes);
        _modelIdleChoice.SelectedIndex = index < 0 ? 1 : index;
    }
    private void InitializeModelLifecycle()
    {
        var title = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        title.Children.Add(Ui.Text("本地大模型预热", "settingsHeading"));
        Grid.SetColumn(_releaseModels, 1); title.Children.Add(_releaseModels);
        var choices = new Grid { ColumnDefinitions = new("Auto,*,Auto,*"), ColumnSpacing = 12, RowSpacing = 5 };
        choices.Children.Add(Ui.Text("加载时机")); Grid.SetColumn(_modelWarmChoice, 1); choices.Children.Add(_modelWarmChoice);
        var idleLabel = Ui.Text("释放内存"); Grid.SetColumn(idleLabel, 2); choices.Children.Add(idleLabel);
        Grid.SetColumn(_modelIdleChoice, 3); choices.Children.Add(_modelIdleChoice);
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(title); panel.Children.Add(choices);
        var intensity = new Grid { ColumnDefinitions = new("*,100,100"), RowDefinitions = new("Auto,Auto,Auto"), ColumnSpacing = 12, RowSpacing = 8 };
        var cpu = Ui.Text("CPU (%)"); Grid.SetColumn(cpu, 1); intensity.Children.Add(cpu);
        var gpu = Ui.Text("GPU (%)"); Grid.SetColumn(gpu, 2); intensity.Children.Add(gpu);
        foreach (var (row, label, cpuInput, gpuInput) in new[] { (1, "打开工具时", _warmInitialCpu, _warmInitialGpu), (2, "选择素材后", _warmCpu, _warmGpu) })
        {
            var text = Ui.Text(label); Grid.SetRow(text, row); intensity.Children.Add(text);
            Grid.SetRow(cpuInput, row); Grid.SetColumn(cpuInput, 1); intensity.Children.Add(cpuInput);
            Grid.SetRow(gpuInput, row); Grid.SetColumn(gpuInput, 2); intensity.Children.Add(gpuInput);
        }
        panel.Children.Add(intensity);
        var ramp = new Grid { ColumnDefinitions = new("*,100"), ColumnSpacing = 12 };
        ramp.Children.Add(Ui.Text("逐步增加用时（秒）")); Grid.SetColumn(_warmRamp, 1); ramp.Children.Add(_warmRamp); panel.Children.Add(ramp);
        panel.Children.Add(Ui.Text("比例控制后台预热节奏，实际占用由系统调度。仅预热已安装的画面理解、文本生成模型。", "caption"));
        ModelLifecycleSection.Child = panel;
        _modelWarmChoice.SelectionChanged += (_, _) => MarkDirty();
        _modelIdleChoice.SelectionChanged += (_, _) => MarkDirty();
        _values.Add(() => _modelWarmChoice.SelectedIndex); _values.Add(() => _modelIdleChoice.SelectedIndex);
        foreach (var input in new[] { _warmInitialCpu, _warmInitialGpu, _warmCpu, _warmGpu, _warmRamp })
        { _values.Add(() => input.Value); input.PropertyChanged += (_, args) => { if (args.Property == NumericUpDown.ValueProperty || args.Property == NumericUpDown.TextProperty) MarkDirty(); }; }
        _appliedValues = _values.Select(value => value()).ToArray();
        _releaseModels.Click += async (_, _) =>
        {
            _releasingModels = true; UpdateReleaseModelsAction();
            try
            {
                var released = await Task.Run(() => MediaTagRuntime.ReleaseAsync(_modelStore.Root, _lifetime.Token), _lifetime.Token);
                if (!_modelsClosed)
                {
                    ModelStatus.Text = Localization.Text(released ? "空闲模型已释放" : "模型正在使用，任务结束后可释放");
                    ModelStatus.IsVisible = true;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!_modelsClosed) { ModelStatus.Text = error.Message; ModelStatus.IsVisible = true; } }
            finally { _releasingModels = false; if (!_modelsClosed) UpdateReleaseModelsAction(); }
        };
        MediaTagRuntime.Changed += ModelRuntimeChanged;
        Closed += (_, _) => MediaTagRuntime.Changed -= ModelRuntimeChanged;
        UpdateReleaseModelsAction();
    }
    private void ModelRuntimeChanged(string root) => Dispatcher.UIThread.Post(() =>
    {
        if (_modelsClosed || !BatchRename.PathComparer.Equals(root, _modelStore.Root)) return;
        UpdateReleaseModelsAction();
        foreach (var model in ModelCatalog.All.Where(model => MediaTagRuntime.Supports(model.Id)))
            if (_modelRows.TryGetValue(model.Id, out var row)) { row.Busy = _modelStore.IsBusy(model.Id); UpdateModelRow(model, row); }
    });
    private void UpdateReleaseModelsAction()
    {
        var states = ModelCatalog.All.Where(model => MediaTagRuntime.Supports(model.Id))
            .Select(model => MediaTagRuntime.Status(_modelStore.Root, model.Id)).ToArray();
        _releaseModels.IsEnabled = !_releasingModels && states.Any(state => state.Loaded)
            && !states.Any(state => state.Preparing || state.State == ModelLoadState.InUse);
    }
}
