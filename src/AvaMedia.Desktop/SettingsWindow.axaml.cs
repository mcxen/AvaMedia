using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Globalization;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private bool _initializing;
    private bool _applied;
    private readonly List<Func<object?>> _values=[];
    private object?[] _appliedValues=[];
    public event EventHandler? Applied;
    public SettingsWindow() : this(new AppSettings()) { }
    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent(); _settings = settings;
        Populate(settings);
        foreach (var input in new[] { OutputInput, FfmpegInput, FfprobeInput, YtdlpInput })
        { _values.Add(()=>input.Text); input.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) MarkDirty(); }; }
        foreach (var input in new[] { AutoGpuInput, MultithreadInput, NotifyInput, ReducedMotionInput })
        { _values.Add(()=>input.IsChecked); input.PropertyChanged += (_, args) => { if (args.Property == CheckBox.IsCheckedProperty) MarkDirty(); }; }
        foreach (var input in new[] { ThreadsInput, JpegQualityInput, WebpQualityInput, ParallelInput })
        { _values.Add(()=>input.Text); input.PropertyChanged += (_, args) => { if (args.Property == NumericUpDown.ValueProperty || args.Property==NumericUpDown.TextProperty) MarkDirty(); }; }
        MultithreadInput.PropertyChanged += (_, args) => { if (args.Property == CheckBox.IsCheckedProperty) ThreadsInput.IsEnabled = MultithreadInput.IsChecked == true; };
        _appliedValues=_values.Select(value=>value()).ToArray();
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
        catch (Exception ex) { StatusText.Text = ex.Message; StatusText.IsVisible = true; return false; }
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
}
