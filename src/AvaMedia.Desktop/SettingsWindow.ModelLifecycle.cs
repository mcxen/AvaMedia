using Avalonia;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Layout;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class SettingsWindow
{
    private static readonly int[] ModelIdleChoices = [1, 5, 15, 30, -1];
    private readonly ToggleSwitch _modelWarmChoice = new() { OnContent = null, OffContent = null };
    private readonly NumericUpDown _warmInitialCpu = WarmPercent(), _warmInitialGpu = WarmPercent(),
        _warmCpu = WarmPercent(), _warmGpu = WarmPercent();
    private readonly NumericUpDown _warmRamp = new() { Minimum = 1, Maximum = 30, Increment = 1 };
    private static NumericUpDown WarmPercent() => new() { Minimum = 1, Maximum = 100, Increment = 5 };
    private readonly ComboBox _modelIdleChoice = Ui.Combo(["空闲 1 分钟", "空闲 5 分钟（推荐）", "空闲 15 分钟", "空闲 30 分钟", "保持到退出应用"], "空闲 5 分钟（推荐）");
    private readonly Button _releaseModels = new() { Content = "立即释放空闲模型", Classes = { "ac", "small" } };
    private bool _releasingModels;
    private void PopulateModelLifecycle(AppSettings source)
    {
        _modelWarmChoice.IsChecked = source.PrewarmLocalModels;
        _warmInitialCpu.Value = source.ModelWarmupInitialCpuPercent; _warmInitialGpu.Value = source.ModelWarmupInitialGpuPercent;
        _warmCpu.Value = source.ModelWarmupCpuPercent; _warmGpu.Value = source.ModelWarmupGpuPercent;
        _warmRamp.Value = source.ModelWarmupRampSeconds;
        var index = Array.IndexOf(ModelIdleChoices, source.TagModelIdleMinutes);
        _modelIdleChoice.SelectedIndex = index < 0 ? 1 : index;
        _modelIdleChoice.Classes.Add("ac"); _modelIdleChoice.Bind(WidthProperty, new DynamicResourceExtension("StIdleChoiceWidth"));
    }
    private void InitializeModelLifecycle()
    {
        var panel = new StackPanel();
        panel.Children.Add(GroupTitle("加载与释放"));
        panel.Children.Add(Group(Row("打开工具时预加载模型", "关闭后在开始任务时才加载", _modelWarmChoice),
            Row("空闲后释放", null, _modelIdleChoice), Row(null, null, _releaseModels)));
        var intensity = new StackPanel();
        foreach (var (label, cpuInput, gpuInput) in new[] { ("打开工具时", _warmInitialCpu, _warmInitialGpu), ("选择素材后", _warmCpu, _warmGpu) })
        {
            AutomationProperties.SetName(cpuInput, label + " CPU (%)"); AutomationProperties.SetName(gpuInput, label + " GPU (%)");
            cpuInput.Classes.Add("ac"); gpuInput.Classes.Add("ac"); cpuInput.Bind(WidthProperty, new DynamicResourceExtension("StWarmupWidth")); gpuInput.Bind(WidthProperty, new DynamicResourceExtension("StWarmupWidth"));
            var row = Row(label, label == "选择素材后" ? "不能低于打开工具时的比例" : null,
                Secondary(new TextBlock { Text = "CPU", [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("StDescriptionFont") }), cpuInput,
                Secondary(new TextBlock { Text = "%", [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("StDescriptionFont") }),
                Secondary(new TextBlock { Text = "GPU", [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("StDescriptionFont") }), gpuInput,
                Secondary(new TextBlock { Text = "%", [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("StDescriptionFont") }));
            row.Bind(MarginProperty, new DynamicResourceExtension("StChildRowMargin")); intensity.Children.Add(row);
        }
        _warmRamp.Classes.Add("ac"); _warmRamp.Bind(WidthProperty, new DynamicResourceExtension("StWarmupWidth"));
        var ramp = Row("逐步增加用时", "1–30 秒", _warmRamp, Secondary(new TextBlock { Text = "秒", [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("StDescriptionFont") }));
        ramp.Bind(MarginProperty, new DynamicResourceExtension("StChildRowMargin")); intensity.Children.Add(ramp);
        panel.Children.Add(GroupTitle("预热工作比例")); panel.Children.Add(Group(intensity));
        intensity.IsEnabled = _modelWarmChoice.IsChecked == true;
        AutomationProperties.SetName(_modelWarmChoice, "打开工具时预加载模型");
        AutomationProperties.SetName(_warmRamp, "逐步增加用时（秒）");
        _modelWarmChoice.IsCheckedChanged += (_, _) => intensity.IsEnabled = _modelWarmChoice.IsChecked == true;
        _warmInitialCpu.ValueChanged += (_, _) => ConstrainWarmup(_warmInitialCpu, _warmCpu);
        _warmInitialGpu.ValueChanged += (_, _) => ConstrainWarmup(_warmInitialGpu, _warmGpu);
        ConstrainWarmup(_warmInitialCpu, _warmCpu); ConstrainWarmup(_warmInitialGpu, _warmGpu);
        ModelLifecycleSection.Child = panel;
        _modelWarmChoice.IsCheckedChanged += (_, _) => MarkDirty();
        _modelIdleChoice.SelectionChanged += (_, _) => MarkDirty();
        _values.Add(() => _modelWarmChoice.IsChecked); _values.Add(() => _modelIdleChoice.SelectedIndex);
        foreach (var input in new[] { _warmInitialCpu, _warmInitialGpu, _warmCpu, _warmGpu, _warmRamp })
        { _values.Add(() => NumericDraftValue(input)); input.PropertyChanged += (_, args) => { if (args.Property == NumericUpDown.ValueProperty || args.Property == NumericUpDown.TextProperty) MarkDirty(); }; }
        _appliedValues = _values.Select(value => value()).ToArray();
        _releaseModels.Click += async (_, _) =>
        {
            _releasingModels = true; UpdateReleaseModelsAction();
            try
            {
                var released = await Task.Run(() => MediaTagRuntime.ReleaseAsync(_modelStore.Root, _lifetime.Token), _lifetime.Token);
                if (!_modelsClosed)
                {
                    StatusText.Text = Localization.Text(released ? "空闲模型已释放" : "模型正在使用，任务结束后可释放");
                    StatusText.IsVisible = true;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!_modelsClosed) { StatusText.Text = error.Message; StatusText.IsVisible = true; } }
            finally { _releasingModels = false; if (!_modelsClosed) UpdateReleaseModelsAction(); }
        };
        MediaTagRuntime.Changed += ModelRuntimeChanged;
        Closed += (_, _) => MediaTagRuntime.Changed -= ModelRuntimeChanged;
        UpdateReleaseModelsAction();
    }
    private static void ConstrainWarmup(NumericUpDown initial, NumericUpDown selected)
    {
        selected.Minimum = initial.Value ?? 1;
        if (selected.Value < selected.Minimum) selected.Value = selected.Minimum;
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
