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
    public string Warning => Plan is { EstimatedBytes: { } bytes } && bytes >= Bytes
        ? Localization.Text("预计输出不小于源视频；可降低码率或分辨率。") : "";
    public bool HasWarning => Warning.Length > 0;
    public string SourceSummary => Info is { } source
        ? Localization.Format($"源视频：{MediaTime.Format(source.Duration)} · {source.Width} × {source.Height} · {Bytes / 1000000d:0.##} MB")
        : Loading ? Localization.Text("正在读取媒体信息…") : "";
    public string PlanSummary => Plan is not { } plan ? "" : plan.QualityDriven
        ? Localization.Format($"画质优先 · 质量 {plan.Quality} · {plan.Width} × {plan.Height} · 体积由内容决定")
        : Localization.Format($"预计 {plan.EstimatedBytes!.Value / 1000000d:0.##} MB · {plan.Width} × {plan.Height} · 视频 {plan.VideoBitrate} kbps");
    public void Refresh() => Raise(string.Empty);
}

public partial class VideoCompressionWindow : Window
{
    private readonly IMediaEngine _engine;
    private readonly ObservableCollection<VideoCompressionEntry> _entries = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _inspectionSlots = new(2);
    private bool _closed;
    private bool _updatingControls;
    private static readonly int[] CommonBitrates = [0, 500, 1000, 2000, 4000, 8000, 12000, 20000];

    public VideoCompressionWindow() : this(new MediaEngine(new()), "", []) { }
    public VideoCompressionWindow(IMediaEngine engine, string outputFolder, string[] files, VideoCompressionOptions? initial = null)
    {
        InitializeComponent(); _engine = engine; WindowArtwork.SetKind(this, "gear");
        SourceList.ItemsSource = _entries;
        var options = VideoCompression.Effective(initial ?? new());
        ModeInput.ItemsSource = new[] { "自动档（推荐）", "手动质量（画质优先）", "手动码率（体积可估）", "按原体积百分比", "按目标 MB" };
        PresetInput.ItemsSource = new[] { "高 · 画质优先", "中 · 均衡（推荐）", "低 · 体积优先" };
        SpeedInput.ItemsSource = new[] { "快 · 更快完成", "中 · 均衡速度", "慢 · 压缩效率优先" };
        BitratePresetInput.ItemsSource = new[] { "自定义码率", "500 kbps", "1000 kbps（1 Mbps）", "2000 kbps（2 Mbps）",
            "4000 kbps（4 Mbps）", "8000 kbps（8 Mbps）", "12000 kbps（12 Mbps）", "20000 kbps（20 Mbps）" };
        FormatInput.ItemsSource = new[] { "MP4", "MKV" };
        CodecInput.ItemsSource = new[] { "H.264（兼容性优先）", "HEVC（H.265）" };
        ResolutionInput.ItemsSource = new[] { "保持原分辨率", "最长边 1920", "最长边 1280", "最长边 854" };
        FrameRateInput.ItemsSource = new[] { "保持原帧率", "不超过 30 fps", "不超过 24 fps" };
        AudioInput.ItemsSource = new[] { "64 kbps", "96 kbps", "128 kbps", "192 kbps" };
        ModeInput.SelectedIndex = (int)options.Mode;
        PresetInput.SelectedIndex = (int)options.Preset; SpeedInput.SelectedIndex = (int)options.Speed;
        QualityInput.Value = options.Quality; BitrateInput.Value = options.VideoBitrate;
        BitratePresetInput.SelectedIndex = Math.Max(0, Array.IndexOf(CommonBitrates, options.VideoBitrate));
        FormatInput.SelectedIndex = options.Format == "mkv" ? 1 : 0;
        CodecInput.SelectedIndex = options.Codec == "hevc" ? 1 : 0;
        ResolutionInput.SelectedIndex = Array.IndexOf(new[] { 0, 1920, 1280, 854 }, options.MaxDimension);
        FrameRateInput.SelectedIndex = Array.IndexOf(new[] { 0, 30, 24 }, options.MaxFrameRate);
        AudioInput.SelectedIndex = Array.IndexOf(new[] { 64, 96, 128, 192 }, options.AudioBitrate);
        PercentageInput.Value = (decimal)options.Percentage; SizeInput.Value = (decimal)options.TargetMegabytes;
        KeepAudioInput.IsChecked = options.KeepAudio; GpuInput.IsChecked = options.PreferGpu;
        OutputInput.Text = outputFolder; Localization.SetIsUserText(OutputInput, true);
        SourceFolderInput.IsChecked = engine.Settings.OutputToSource; SettingNameInput.IsChecked = engine.Settings.AddSettingName;
        foreach (var combo in new[] { FormatInput, CodecInput, ResolutionInput, FrameRateInput, AudioInput, SpeedInput })
            combo.SelectionChanged += (_, _) => { if (!_updatingControls) Recalculate(); };
        ModeInput.SelectionChanged += (_, _) =>
        { if (_updatingControls) return; if (ModeInput.SelectedIndex == (int)VideoCompressionMode.Automatic) SetAutoPreset(); Recalculate(); };
        PresetInput.SelectionChanged += (_, _) =>
        { if (_updatingControls) return; if (ModeInput.SelectedIndex == (int)VideoCompressionMode.Automatic) SetAutoPreset(); Recalculate(); };
        BitratePresetInput.SelectionChanged += (_, _) =>
        {
            if (_updatingControls || BitratePresetInput.SelectedIndex <= 0) return;
            BitrateInput.Value = CommonBitrates[BitratePresetInput.SelectedIndex];
        };
        foreach (var number in new[] { PercentageInput, SizeInput, QualityInput, BitrateInput })
            number.PropertyChanged += (_, change) =>
            {
                if (_updatingControls) return;
                if (change.Property == NumericUpDown.ValueProperty || change.Property == DataValidationErrors.HasErrorsProperty)
                {
                    if (number == BitrateInput)
                    {
                        _updatingControls = true;
                        try { BitratePresetInput.SelectedIndex = Math.Max(0, Array.FindIndex(CommonBitrates, rate => BitrateInput.Value == rate)); }
                        finally { _updatingControls = false; }
                    }
                    Recalculate();
                }
            };
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
    private void SetAutoPreset()
    {
        if (PresetInput.SelectedIndex < 0) return;
        var options = VideoCompression.ApplyPreset(new(), (VideoCompressionPreset)PresetInput.SelectedIndex);
        _updatingControls = true;
        try
        {
            QualityInput.Value = options.Quality;
            ResolutionInput.SelectedIndex = Array.IndexOf(new[] { 0, 1920, 1280, 854 }, options.MaxDimension);
            FrameRateInput.SelectedIndex = Array.IndexOf(new[] { 0, 30, 24 }, options.MaxFrameRate);
            AudioInput.SelectedIndex = Array.IndexOf(new[] { 64, 96, 128, 192 }, options.AudioBitrate);
            SpeedInput.SelectedIndex = (int)options.Speed;
        }
        finally { _updatingControls = false; }
    }

    private static decimal Number(NumericUpDown input, decimal fallback, bool required, bool integer = false)
    {
        if (!required) return fallback;
        if (input.Value is not { } value || DataValidationErrors.GetHasErrors(input) || integer && value != decimal.Truncate(value))
            throw new ArgumentException("请填写有效的质量、码率或体积目标。");
        return value;
    }

    private VideoCompressionOptions ReadOptions()
    {
        if (ModeInput.SelectedIndex < 0 || FormatInput.SelectedIndex < 0 || CodecInput.SelectedIndex < 0 ||
            ResolutionInput.SelectedIndex < 0 || FrameRateInput.SelectedIndex < 0 || AudioInput.SelectedIndex < 0 ||
            PresetInput.SelectedIndex < 0 || SpeedInput.SelectedIndex < 0)
            throw new ArgumentException("请完成压缩选项。");
        var mode = (VideoCompressionMode)ModeInput.SelectedIndex;
        return VideoCompression.Effective(new()
        {
            Mode = mode, Preset = (VideoCompressionPreset)PresetInput.SelectedIndex, Speed = (VideoEncodingSpeed)SpeedInput.SelectedIndex,
            Quality = (int)Number(QualityInput, 23, mode == VideoCompressionMode.Quality, true),
            VideoBitrate = (int)Number(BitrateInput, 4000, mode == VideoCompressionMode.Bitrate, true),
            Percentage = (double)Number(PercentageInput, 60, mode == VideoCompressionMode.Percentage),
            TargetMegabytes = (double)Number(SizeInput, 50, mode == VideoCompressionMode.TargetSize),
            Format = FormatInput.SelectedIndex == 1 ? "mkv" : "mp4", Codec = CodecInput.SelectedIndex == 1 ? "hevc" : "h264",
            MaxDimension = new[] { 0, 1920, 1280, 854 }[ResolutionInput.SelectedIndex],
            MaxFrameRate = new[] { 0, 30, 24 }[FrameRateInput.SelectedIndex],
            AudioBitrate = new[] { 64, 96, 128, 192 }[AudioInput.SelectedIndex],
            KeepAudio = KeepAudioInput.IsChecked == true, PreferGpu = GpuInput.IsChecked == true
        });
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
        var mode = (VideoCompressionMode)ModeInput.SelectedIndex;
        AutoSection.IsVisible = mode == VideoCompressionMode.Automatic;
        QualitySection.IsVisible = mode == VideoCompressionMode.Quality;
        BitrateSection.IsVisible = mode == VideoCompressionMode.Bitrate;
        PercentageSection.IsVisible = mode == VideoCompressionMode.Percentage; SizeSection.IsVisible = mode == VideoCompressionMode.TargetSize;
        ResolutionInput.IsEnabled = FrameRateInput.IsEnabled = SpeedInput.IsEnabled = mode != VideoCompressionMode.Automatic;
        AudioInput.IsEnabled = KeepAudioInput.IsChecked == true && mode != VideoCompressionMode.Automatic;
        var preset = VideoCompression.ApplyPreset(new(), PresetInput.SelectedIndex < 0 ? VideoCompressionPreset.Balanced : (VideoCompressionPreset)PresetInput.SelectedIndex);
        PresetDescription.Text = Localization.Join(" · ", [PresetInput.SelectedIndex switch
        {
            0 => "保留原尺寸与帧率 · 质量 20 · 慢速",
            2 => "最长边 1280 · 不超过 30 fps · 质量 28 · 快速",
            _ => "最长边 1920 · 不超过 30 fps · 质量 23 · 中速"
        }, KeepAudioInput.IsChecked == true ? Localization.Format($"音频 {preset.AudioBitrate} kbps（源视频有声音时）") : "移除声音"]);
        OutputSummary.Text = Localization.Format($"输出：{(FormatInput.SelectedIndex == 1 ? "MKV" : "MP4")} · {(CodecInput.SelectedIndex == 1 ? "HEVC" : "H.264")}");
        ModeDescription.Text = Localization.Text(VideoCompression.UsesQuality(mode)
            ? "按画质控制编码；体积由画面内容决定，可能大于原文件。分辨率与帧率只降低，不放大。"
            : "按码率或体积预算编码；预计大小不作精确保证，画质随码率变化。分辨率与帧率只降低，不放大。");
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
            if (VideoCompression.UsesQuality(mode))
                Localization.SetText(TotalSummary, $"{_entries.Count} 个视频 · 原体积 {_entries.Sum(entry => entry.Bytes) / 1000000d:0.##} MB · 输出体积由内容决定");
            else Localization.SetText(TotalSummary, $"{_entries.Count} 个视频 · 原体积 {_entries.Sum(entry => entry.Bytes) / 1000000d:0.##} MB · 预计 {_entries.Sum(entry => entry.Plan?.EstimatedBytes ?? 0) / 1000000d:0.##} MB");
            ValidationText.Text = _entries.Count == 0 ? "添加一个或多个视频开始压缩。" : pending > 0 ? "正在读取，请稍候…" :
                invalid > 0 ? "请调整目标或移除有错误的视频。" : "每个视频独立导出，源文件不会被覆盖。";
            ConfirmButton.IsEnabled = ready > 0 && ready == _entries.Count && pending == 0 && invalid == 0;
        }
        catch (Exception exception)
        {
            foreach (var entry in _entries) { entry.Plan = null; entry.Refresh(); }
            TotalSummary.Text = Localization.Text("参数无效，暂不显示输出预估。");
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
    private void LightClick(object? sender, RoutedEventArgs args) { PercentageInput.Value = 80; }
    private void BalancedClick(object? sender, RoutedEventArgs args) { PercentageInput.Value = 60; }
    private void SmallClick(object? sender, RoutedEventArgs args) { PercentageInput.Value = 40; }
    private void HighQualityClick(object? sender, RoutedEventArgs args) { QualityInput.Value = 20; }
    private void MediumQualityClick(object? sender, RoutedEventArgs args) { QualityInput.Value = 23; }
    private void LowQualityClick(object? sender, RoutedEventArgs args) { QualityInput.Value = 28; }
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
