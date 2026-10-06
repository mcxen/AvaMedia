using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed record VideoCompressionRequest(string[] Files, VideoCompressionOptions Options, string OutputFolder,
    bool OutputToSource, bool AddSettingName);

public sealed class VideoCompressionEntry(string path) : Observable
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public long Bytes { get; set; }
    public MediaInfo? Info { get; set; }
    public bool Loading { get; set; } = true;
    public string InspectionError { get; set; } = "";
    public VideoCompressionPlan? Plan { get; set; }
    public string Error { get; set; } = "";
    public bool HasError => Error.Length > 0;
    public string SourceSummary => Info is { } source
        ? Localization.Format($"源视频：{MediaTime.Format(source.Duration)} · {source.Width} × {source.Height} · {Bytes / 1000000d:0.##} MB")
        : Loading ? Localization.Text("正在读取媒体信息…") : "";
    public string PlanSummary => Plan is { } plan
        ? Localization.Format($"预计 {plan.EstimatedBytes / 1000000d:0.##} MB · {plan.Width} × {plan.Height} · 视频 {plan.VideoBitrate} kbps") : "";
    public void Refresh() => Raise(string.Empty);
}

public partial class VideoCompressionWindow : Window
{
    private readonly IMediaEngine _engine;
    private readonly ObservableCollection<VideoCompressionEntry> _entries = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _inspectionSlots = new(2);
    private bool _closed;

    public VideoCompressionWindow() : this(new MediaEngine(new()), "", []) { }
    public VideoCompressionWindow(IMediaEngine engine, string outputFolder, string[] files, VideoCompressionOptions? initial = null)
    {
        InitializeComponent(); _engine = engine; WindowArtwork.SetKind(this, "gear");
        SourceList.ItemsSource = _entries;
        var options = initial ?? new();
        ModeInput.ItemsSource = new[] { "按原体积百分比", "按目标 MB" };
        FormatInput.ItemsSource = new[] { "MP4", "MKV" };
        CodecInput.ItemsSource = new[] { "H.264（兼容性优先）", "HEVC（H.265）" };
        ResolutionInput.ItemsSource = new[] { "保持原分辨率", "最长边 1920", "最长边 1280", "最长边 854" };
        FrameRateInput.ItemsSource = new[] { "保持原帧率", "不超过 30 fps", "不超过 24 fps" };
        AudioInput.ItemsSource = new[] { "64 kbps", "96 kbps", "128 kbps", "192 kbps" };
        ModeInput.SelectedIndex = (int)options.Mode;
        FormatInput.SelectedIndex = options.Format == "mkv" ? 1 : 0;
        CodecInput.SelectedIndex = options.Codec == "hevc" ? 1 : 0;
        ResolutionInput.SelectedIndex = Array.IndexOf(new[] { 0, 1920, 1280, 854 }, options.MaxDimension);
        FrameRateInput.SelectedIndex = Array.IndexOf(new[] { 0, 30, 24 }, options.MaxFrameRate);
        AudioInput.SelectedIndex = Array.IndexOf(new[] { 64, 96, 128, 192 }, options.AudioBitrate);
        PercentageInput.Value = (decimal)options.Percentage; SizeInput.Value = (decimal)options.TargetMegabytes;
        KeepAudioInput.IsChecked = options.KeepAudio; GpuInput.IsChecked = options.PreferGpu;
        OutputInput.Text = outputFolder; Localization.SetIsUserText(OutputInput, true);
        SourceFolderInput.IsChecked = engine.Settings.OutputToSource; SettingNameInput.IsChecked = engine.Settings.AddSettingName;
        foreach (var combo in new[] { ModeInput, FormatInput, CodecInput, ResolutionInput, FrameRateInput, AudioInput })
            combo.SelectionChanged += (_, _) => Recalculate();
        foreach (var number in new[] { PercentageInput, SizeInput })
            number.PropertyChanged += (_, change) =>
            { if (change.Property == NumericUpDown.ValueProperty || change.Property == DataValidationErrors.HasErrorsProperty) Recalculate(); };
        KeepAudioInput.IsCheckedChanged += (_, _) => Recalculate();
        GpuInput.IsCheckedChanged += (_, _) => Recalculate();
        SourceFolderInput.IsCheckedChanged += (_, _) => UpdateOutput();
        Opened += async (_, _) => await AddFilesAsync(files);
        Localization.Changed += LanguageChanged;
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); Localization.Changed -= LanguageChanged; };
        UpdateOutput(); Recalculate();
    }

    private void LanguageChanged(object? sender, EventArgs args) => Recalculate();
    private void UpdateOutput() => OutputInput.IsEnabled = BrowseButton.IsEnabled = SourceFolderInput.IsChecked != true;
    private VideoCompressionOptions ReadOptions()
    {
        if (ModeInput.SelectedIndex < 0 || FormatInput.SelectedIndex < 0 || CodecInput.SelectedIndex < 0 ||
            ResolutionInput.SelectedIndex < 0 || FrameRateInput.SelectedIndex < 0 || AudioInput.SelectedIndex < 0)
            throw new ArgumentException("请完成压缩选项。");
        if (ModeInput.SelectedIndex == 0 && (PercentageInput.Value is null || DataValidationErrors.GetHasErrors(PercentageInput)) ||
            ModeInput.SelectedIndex == 1 && (SizeInput.Value is null || DataValidationErrors.GetHasErrors(SizeInput)))
            throw new ArgumentException("请填写压缩目标。");
        return new()
        {
            Mode = (VideoCompressionMode)ModeInput.SelectedIndex,
            Percentage = (double)(PercentageInput.Value ?? 60), TargetMegabytes = (double)(SizeInput.Value ?? 50),
            Format = FormatInput.SelectedIndex == 1 ? "mkv" : "mp4", Codec = CodecInput.SelectedIndex == 1 ? "hevc" : "h264",
            MaxDimension = new[] { 0, 1920, 1280, 854 }[ResolutionInput.SelectedIndex],
            MaxFrameRate = new[] { 0, 30, 24 }[FrameRateInput.SelectedIndex],
            AudioBitrate = new[] { 64, 96, 128, 192 }[AudioInput.SelectedIndex],
            KeepAudio = KeepAudioInput.IsChecked == true, PreferGpu = GpuInput.IsChecked == true
        };
    }

    private async Task AddFilesAsync(IEnumerable<string> files)
    {
        try
        {
            var existing = _entries.Select(entry => entry.Path).ToHashSet(VideoFolderScanner.PathComparer);
            var added = files.Select(Path.GetFullPath).Where(existing.Add).Select(path => new VideoCompressionEntry(path)).ToArray();
            foreach (var entry in added) _entries.Add(entry);
            Recalculate();
            await Task.WhenAll(added.Select(InspectAsync));
        }
        catch (Exception exception) { if (!_closed) await Ui.Message(this, "添加视频失败", exception.Message); }
    }

    private async Task InspectAsync(VideoCompressionEntry entry)
    {
        var acquired = false;
        try
        {
            await _inspectionSlots.WaitAsync(_lifetime.Token); acquired = true;
            if (!VideoFolderScanner.IsVideoFile(entry.Path)) throw new ArgumentException("请选择支持的视频文件。");
            entry.Bytes = new FileInfo(entry.Path).Length;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            entry.Info = await _engine.Probe(entry.Path, timeout.Token);
        }
        catch (OperationCanceledException) { entry.InspectionError = "媒体读取超时或已取消。"; }
        catch (Exception exception) { entry.InspectionError = exception.Message; }
        finally
        {
            if (acquired) _inspectionSlots.Release();
            entry.Loading = false;
            if (!_closed) Recalculate();
        }
    }

    private void Recalculate()
    {
        if (_closed) return;
        PercentageSection.IsVisible = ModeInput.SelectedIndex == 0; SizeSection.IsVisible = ModeInput.SelectedIndex == 1;
        AudioInput.IsEnabled = KeepAudioInput.IsChecked == true;
        try
        {
            var options = ReadOptions(); options.Validate();
            foreach (var entry in _entries)
            {
                entry.Plan = null; entry.Error = entry.InspectionError;
                if (entry.Info is { } source && entry.Error.Length == 0)
                    try { entry.Plan = VideoCompression.Plan(entry.Bytes, source, options); }
                    catch (ArgumentException exception) { entry.Error = exception.Message; }
                entry.Refresh();
            }
            var pending = _entries.Count(entry => entry.Loading);
            var invalid = _entries.Count(entry => entry.HasError);
            var ready = _entries.Count(entry => entry.Plan is not null);
            Localization.SetText(TotalSummary, $"{_entries.Count} 个视频 · 原体积 {_entries.Sum(entry => entry.Bytes) / 1000000d:0.##} MB · 预计 {_entries.Sum(entry => entry.Plan?.EstimatedBytes ?? 0) / 1000000d:0.##} MB");
            ValidationText.Text = _entries.Count == 0 ? "添加一个或多个视频开始压缩。" : pending > 0 ? "正在读取，请稍候…" :
                invalid > 0 ? "请调整目标或移除有错误的视频。" : "每个视频独立导出，源文件不会被覆盖。";
            ConfirmButton.IsEnabled = ready > 0 && ready == _entries.Count && pending == 0 && invalid == 0;
        }
        catch (Exception exception)
        {
            ValidationText.Text = exception.Message; ConfirmButton.IsEnabled = false;
        }
    }

    private async void AddClick(object? sender, RoutedEventArgs args) => await AddFilesAsync(await Ui.Pick(this, "添加压缩视频"));
    private async void FolderClick(object? sender, RoutedEventArgs args)
    {
        if (await Ui.Folder(this, "添加视频文件夹") is not { } folder) return;
        try { await AddFilesAsync(Directory.EnumerateFiles(folder).Where(VideoFolderScanner.IsVideoFile)); }
        catch (Exception exception) { await Ui.Message(this, "添加视频失败", exception.Message); }
    }
    private void RemoveClick(object? sender, RoutedEventArgs args)
    {
        foreach (var entry in SourceList.SelectedItems?.OfType<VideoCompressionEntry>().ToArray() ?? []) _entries.Remove(entry);
        Recalculate();
    }
    private void LightClick(object? sender, RoutedEventArgs args) { ModeInput.SelectedIndex = 0; PercentageInput.Value = 80; }
    private void BalancedClick(object? sender, RoutedEventArgs args) { ModeInput.SelectedIndex = 0; PercentageInput.Value = 60; }
    private void SmallClick(object? sender, RoutedEventArgs args) { ModeInput.SelectedIndex = 0; PercentageInput.Value = 40; }
    private async void BrowseClick(object? sender, RoutedEventArgs args)
    { if (await Ui.Folder(this, "选择输出目录") is { } folder) OutputInput.Text = folder; }
    private void CancelClick(object? sender, RoutedEventArgs args) => Close(null);
    private async void ConfirmClick(object? sender, RoutedEventArgs args)
    {
        Recalculate(); if (!ConfirmButton.IsEnabled) return;
        try
        {
            var options = ReadOptions();
            foreach (var entry in _entries)
            {
                if (!File.Exists(entry.Path)) throw new FileNotFoundException("源视频已移走，请重新添加。", entry.Path);
                _ = VideoCompression.Plan(new FileInfo(entry.Path).Length, entry.Info!, options);
            }
            if (string.IsNullOrWhiteSpace(OutputInput.Text)) throw new ArgumentException("请选择输出目录。");
            Close(new VideoCompressionRequest(_entries.Select(entry => entry.Path).ToArray(), options,
                Path.GetFullPath(OutputInput.Text), SourceFolderInput.IsChecked == true, SettingNameInput.IsChecked == true));
        }
        catch (Exception exception) { await Ui.Message(this, "压缩参数错误", exception.Message); }
    }
}
