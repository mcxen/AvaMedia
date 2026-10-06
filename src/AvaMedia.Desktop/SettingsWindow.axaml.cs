using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Globalization;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly IAppOptionsServices _services;
    private readonly bool _ownsServices;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _initializing;
    private bool _applied;
    private readonly List<Func<object?>> _values=[];
    private object?[] _appliedValues=[];
    public event EventHandler? Applied;
    public SettingsWindow() : this(new AppSettings()) { }
    public SettingsWindow(AppSettings settings, IAppOptionsServices? services = null)
    {
        InitializeComponent(); _settings = settings; _services = services ?? new AppOptionsServices(); _ownsServices = services is null;
        Populate(settings);
        foreach (var input in new[] { OutputInput, FfmpegInput, FfprobeInput, YtdlpInput })
        { _values.Add(()=>input.Text); input.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) MarkDirty(); }; }
        foreach (var input in new[] { AutoGpuInput, MultithreadInput, NotifyInput, ReducedMotionInput, OutputToSourceInput, AddSettingNameInput,
            ShutdownInput, OpenOutputInput, OperationSoundInput, CompleteSoundInput, ErrorSoundInput, ContextMenuInput, TrayInput, CheckUpdatesInput })
        { _values.Add(()=>input.IsChecked); input.PropertyChanged += (_, args) => { if (args.Property == CheckBox.IsCheckedProperty) MarkDirty(); }; }
        foreach (var input in new[] { ThreadsInput, JpegQualityInput, WebpQualityInput, ParallelInput })
        { _values.Add(()=>input.Text); input.PropertyChanged += (_, args) => { if (args.Property == NumericUpDown.ValueProperty || args.Property==NumericUpDown.TextProperty) MarkDirty(); }; }
        MultithreadInput.PropertyChanged += (_, args) => { if (args.Property == CheckBox.IsCheckedProperty) ThreadsInput.IsEnabled = MultithreadInput.IsChecked == true; };
        _appliedValues=_values.Select(value=>value()).ToArray();
        ContextMenuInput.IsEnabled = _services.CanUseContextMenu; TrayInput.IsEnabled = _services.CanUseTray;
        ContextMenuInput.Content = OperatingSystem.IsMacOS() ? "添加到 Finder 快速操作" : "添加到系统上下文菜单";
        ToolTip.SetTip(ContextMenuInput, OperatingSystem.IsMacOS() ? "安装到当前用户的 Finder 服务；可在系统设置的扩展中管理。" : "添加当前用户的资源管理器菜单；Windows 11 可能位于“显示更多选项”。");
        if (!_services.CanUseTray) ToolTip.SetTip(TrayInput, "当前环境不提供系统托盘，使用正常窗口最小化。");
        Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); if (_ownsServices) _services.Dispose(); };
        AddHandler(Button.ClickEvent,(_,_)=>{if(_settings.PlayOperationSound)_services.PlaySound(UiSound.Operation);},RoutingStrategies.Bubble);
    }

    public AppSettings ReadSettings()
    {
        var draft = _settings.Clone();
        draft.OutputFolder = OutputInput.Text?.Trim() ?? "";
        draft.FFmpegPath = FfmpegInput.Text?.Trim() ?? ""; draft.FFprobePath = FfprobeInput.Text?.Trim() ?? "";
        draft.YtDlpPath = YtdlpInput.Text?.Trim() ?? ""; draft.ParallelJobs = Number(ParallelInput);
        draft.MultiThread = MultithreadInput.IsChecked == true; draft.CpuThreads = Number(ThreadsInput);
        draft.AutoDetectGpu = AutoGpuInput.IsChecked == true; draft.JpegQuality = Number(JpegQualityInput); draft.WebpQuality = Number(WebpQualityInput);
        draft.NotifyComplete = NotifyInput.IsChecked == true; draft.ReduceMotion = ReducedMotionInput.IsChecked == true;
        draft.OutputToSource = OutputToSourceInput.IsChecked == true; draft.AddSettingName = AddSettingNameInput.IsChecked == true;
        draft.ShutdownOnComplete = ShutdownInput.IsChecked == true; draft.OpenOutputFolderOnComplete = OpenOutputInput.IsChecked == true;
        draft.PlayOperationSound = OperationSoundInput.IsChecked == true; draft.PlayCompleteSound = CompleteSoundInput.IsChecked == true;
        draft.PlayErrorSound = ErrorSoundInput.IsChecked == true; draft.SystemContextMenu = ContextMenuInput.IsChecked == true;
        draft.MinimizeToTray = TrayInput.IsChecked == true; draft.CheckForUpdates = CheckUpdatesInput.IsChecked == true;
        SettingsPolicy.Validate(draft); draft.OutputFolder = Path.GetFullPath(draft.OutputFolder); return draft;
    }
    private static int Number(NumericUpDown input)
    {
        if (!decimal.TryParse(input.Text,NumberStyles.Integer,input.NumberFormat,out var value) || value<input.Minimum || value>input.Maximum)
            throw new ArgumentException($"请输入 {input.Minimum} 到 {input.Maximum} 之间的整数设置值。");
        return decimal.ToInt32(value);
    }
    private void Populate(AppSettings source)
    {
        _initializing = true;
        OutputInput.Text = source.OutputFolder; FfmpegInput.Text = source.FFmpegPath; FfprobeInput.Text = source.FFprobePath; YtdlpInput.Text = source.YtDlpPath;
        ParallelInput.Value = Math.Clamp(source.ParallelJobs, 1, 8); AutoGpuInput.IsChecked = source.AutoDetectGpu;
        MultithreadInput.IsChecked = source.MultiThread; ThreadsInput.Value = Math.Clamp(source.CpuThreads, 1, 16); ThreadsInput.IsEnabled = source.MultiThread;
        JpegQualityInput.Value = Math.Clamp(source.JpegQuality, 1, 100); WebpQualityInput.Value = Math.Clamp(source.WebpQuality, 1, 100);
        NotifyInput.IsChecked = source.NotifyComplete; ReducedMotionInput.IsChecked = source.ReduceMotion;
        OutputToSourceInput.IsChecked = source.OutputToSource; AddSettingNameInput.IsChecked = source.AddSettingName;
        ShutdownInput.IsChecked = source.ShutdownOnComplete; OpenOutputInput.IsChecked = source.OpenOutputFolderOnComplete;
        OperationSoundInput.IsChecked = source.PlayOperationSound; CompleteSoundInput.IsChecked = source.PlayCompleteSound;
        ErrorSoundInput.IsChecked = source.PlayErrorSound; ContextMenuInput.IsChecked = source.SystemContextMenu;
        TrayInput.IsChecked = source.MinimizeToTray; CheckUpdatesInput.IsChecked = source.CheckForUpdates;
        RuntimeInfo.Text = $"AvaMedia · .NET {Environment.Version}\n{System.Runtime.InteropServices.RuntimeInformation.OSDescription}\nCPU logical processors: {Environment.ProcessorCount}\nSettings: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia")}\n\n设备资源共享：未集成服务 SDK，不会启动共享进程。";
        StatusText.IsVisible = false; _initializing = false;
    }
    private void MarkDirty() { if (!_initializing) { ApplyButton.IsEnabled = !_values.Select(value=>value()).SequenceEqual(_appliedValues); StatusText.IsVisible = false; } }
    private bool ApplyDraft()
    {
        try
        {
            var draft = ReadSettings(); var prior = _settings.Clone(); _settings.CopyFrom(draft);
            try { Applied?.Invoke(this, EventArgs.Empty); }
            catch { _settings.CopyFrom(prior); throw; }
            _applied = true; _appliedValues=_values.Select(value=>value()).ToArray(); ApplyButton.IsEnabled = false; StatusText.IsVisible = false; return true;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; StatusText.IsVisible = true;if(_settings.PlayErrorSound)_services.PlaySound(UiSound.Error); return false; }
    }
    private void ApplyClick(object? sender, RoutedEventArgs args) => ApplyDraft();
    private void OkClick(object? sender, RoutedEventArgs args) { if (ApplyDraft()) Close(true); }
    private void CancelClick(object? sender, RoutedEventArgs args) => Close(_applied);
    private void ResetClick(object? sender, RoutedEventArgs args) { Populate(new AppSettings { Theme = _settings.Theme }); MarkDirty(); }
    private async void HardwareTestClick(object? sender, RoutedEventArgs args)
    {
        HardwareTestButton.IsEnabled = false;
        try { await new HardwareTestWindow(FfmpegInput.Text?.Trim() ?? "").ShowDialog(this); }
        finally { HardwareTestButton.IsEnabled = true; }
    }
    private async void OutputBrowseClick(object? sender, RoutedEventArgs args) { if (await Ui.Folder(this, "选择输出文件夹") is { } path) OutputInput.Text = path; }
    private async Task Browse(TextBox input) { if ((await Ui.Pick(this, "选择可执行文件", false)).FirstOrDefault() is { } path) input.Text = path; }
    private async void FfmpegBrowseClick(object? sender, RoutedEventArgs args) => await Browse(FfmpegInput);
    private async void FfprobeBrowseClick(object? sender, RoutedEventArgs args) => await Browse(FfprobeInput);
    private async void YtdlpBrowseClick(object? sender, RoutedEventArgs args) => await Browse(YtdlpInput);
    private async void CheckUpdatesClick(object? sender, RoutedEventArgs args)
    {
        CheckUpdatesButton.IsEnabled = false;
        try { var result = await _services.CheckUpdatesAsync(_lifetime.Token); if (IsVisible) await new UpdateWindow(result).ShowDialog(this); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (IsVisible) { StatusText.Text = "版本检查失败：" + ex.Message; StatusText.IsVisible = true; } }
        finally { if (IsVisible) CheckUpdatesButton.IsEnabled = true; }
    }
}
