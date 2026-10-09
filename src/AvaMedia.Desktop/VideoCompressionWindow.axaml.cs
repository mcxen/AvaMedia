using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
    public VideoCompressionColor? Color { get; set; }
    public bool Loading { get; set; } = true;
    public string InspectionError { get; set; } = "";
    public VideoCompressionPlan? Plan { get; set; }
    public string Error { get; set; } = "";
    public bool HasError => Error.Length > 0;
    public string Warning => Plan is { EstimatedBytes: { } bytes } && bytes >= Bytes
        ? Localization.Text("预计输出不小于源视频；可降低码率或分辨率。") : "";
    public bool HasWarning => Warning.Length > 0;
    public string SourceSummary => Info is { } source
        ? Localization.Format($"{MediaTime.Format(source.Duration)} · {source.Width} × {source.Height} · {Bytes / 1000000d:0.##} MB · {source.VideoCodec.ToUpperInvariant()}")
        : Loading ? Localization.Text("正在读取媒体信息…") : "";
    public bool HasColorChange => Color?.ToneMap == true;
    public string ColorSummary => HasColorChange ? Localization.Format($"{Color!.SourceLabel} → SDR（BT.709）；不保留 HDR 动态元数据。") : "";
    public string PlanSummary => Plan is not { } plan ? "" : plan.QualityDriven
        ? Localization.Format($"输出 {plan.Width} × {plan.Height} · 质量 {plan.Quality}")
        : Localization.Format($"预计 {plan.EstimatedBytes!.Value / 1000000d:0.##} MB · 输出 {plan.Width} × {plan.Height}");
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
    private static readonly string[] Formats = ["mp4", "mov", "m4v", "mkv", "ts"];

    public VideoCompressionWindow() : this(new MediaEngine(new()), "", []) { }
    public VideoCompressionWindow(IMediaEngine engine, string outputFolder, string[] files, VideoCompressionOptions? initial = null, bool editing = false)
    {
        InitializeComponent(); ToolExecution.Configure(this,ConfirmButton,"开始压缩",editing); _engine = engine; WindowArtwork.SetKind(this, "gear");
        if (editing) { Title = "编辑视频压缩任务"; ConfirmButton.Content = "保存修改"; }
        SourceList.ItemsSource = _entries;
        SourceList.SelectionChanged += (_, _) => RemoveButton.IsEnabled = SourceList.SelectedItems?.Count > 0;
        var options = VideoCompression.Effective(initial ?? new Storage().LoadToolOptions<VideoCompressionOptions>("video-compress") ?? new());
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = !_closed && e.DataTransfer.TryGetFiles() is not null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            e.Handled = true;
            if (!_closed) await AddFilesAsync(e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>().Where(File.Exists) ?? []);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        ModeInput.ItemsSource = new[] { "自动", "按画质", "按码率", "按原体积比例", "指定体积" };
        PresetInput.ItemsSource = new[] { "高画质", "均衡", "小体积" };
        SpeedInput.ItemsSource = new[] { "快速编码", "均衡", "慢速编码" };
        BitratePresetInput.ItemsSource = new[] { "自定义码率", "500 kbps", "1000 kbps（1 Mbps）", "2000 kbps（2 Mbps）",
            "4000 kbps（4 Mbps）", "8000 kbps（8 Mbps）", "12000 kbps（12 Mbps）", "20000 kbps（20 Mbps）" };
        FormatInput.ItemsSource = new[] { "MP4", "MOV（QuickTime）", "M4V（Apple 视频）", "MKV", "TS（MPEG-TS）" };
        CodecInput.ItemsSource = new[] { "H.264（兼容性优先）", "HEVC（H.265）" };
        ResolutionInput.ItemsSource = new[] { "保持原分辨率", "最长边 1920", "最长边 1280", "最长边 854" };
        FrameRateInput.ItemsSource = new[] { "保持原帧率", "不超过 30 fps", "不超过 24 fps" };
        AudioInput.ItemsSource = new[] { "64 kbps", "96 kbps", "128 kbps", "192 kbps" };
        ModeInput.SelectedIndex = (int)options.Mode;
        PresetInput.SelectedIndex = (int)options.Preset; SpeedInput.SelectedIndex = (int)options.Speed;
        QualityInput.Value = options.Quality; BitrateInput.Value = options.VideoBitrate;
        BitratePresetInput.SelectedIndex = Math.Max(0, Array.IndexOf(CommonBitrates, options.VideoBitrate));
        FormatInput.SelectedIndex = Array.IndexOf(Formats, options.Format);
        CodecInput.SelectedIndex = options.Codec == "hevc" ? 1 : 0;
        ResolutionInput.SelectedIndex = Array.IndexOf(new[] { 0, 1920, 1280, 854 }, options.MaxDimension);
        FrameRateInput.SelectedIndex = Array.IndexOf(new[] { 0, 30, 24 }, options.MaxFrameRate);
        AudioInput.SelectedIndex = Array.IndexOf(new[] { 64, 96, 128, 192 }, options.AudioBitrate);
        PercentageInput.Value = (decimal)options.Percentage; SizeInput.Value = (decimal)options.TargetMegabytes;
        KeepAudioInput.IsChecked = options.KeepAudio; GpuInput.IsChecked = options.PreferGpu;
        OutputInput.Text = outputFolder; Localization.SetIsUserText(OutputInput, true);
        SourceFolderInput.IsChecked = !editing && engine.Settings.OutputToSource; SettingNameInput.IsChecked = !editing && engine.Settings.AddSettingName;
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
        SourceFolderInput.IsCheckedChanged += (_, _) => { UpdateOutput(); Recalculate(); };
        OutputInput.TextChanged += (_, _) => Recalculate();
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
            Format = Formats[FormatInput.SelectedIndex], Codec = CodecInput.SelectedIndex == 1 ? "hevc" : "h264",
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
            entry.Color = VideoCompressionColor.Inspect(entry.Info);
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
            0 => "目标原体积 85% · 慢速",
            2 => "目标原体积 50% · 快速",
            _ => "目标原体积 70% · 中速"
        }, KeepAudioInput.IsChecked == true ? Localization.Format($"音频最高 {preset.AudioBitrate} kbps") : "移除声音"]);
        OutputOptions.Header = Localization.Format($"输出参数 · {(FormatInput.SelectedIndex >= 0 ? Formats[FormatInput.SelectedIndex].ToUpperInvariant() : "")} · {(CodecInput.SelectedIndex == 1 ? "HEVC" : "H.264")}");
        ModeDescription.Text = Localization.Text(mode == VideoCompressionMode.Automatic
            ? "低码率时自动降低分辨率与音频码率"
            : VideoCompression.UsesQuality(mode)
                ? "输出体积可能大于原文件"
                : "输出体积为估算值");
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
            EmptyText.IsVisible = _entries.Count == 0;
            RemoveInvalidButton.IsEnabled = invalid > 0;
            if (_entries.Count == 0) TotalSummary.Text = "";
            else if (VideoCompression.UsesQuality(mode))
                Localization.SetText(TotalSummary, $"{_entries.Count} 个视频 · 原体积 {_entries.Sum(entry => entry.Bytes) / 1000000d:0.##} MB · 输出体积由内容决定");
            else Localization.SetText(TotalSummary, $"{_entries.Count} 个视频 · 原体积 {_entries.Sum(entry => entry.Bytes) / 1000000d:0.##} MB · 预计 {_entries.Sum(entry => entry.Plan?.EstimatedBytes ?? 0) / 1000000d:0.##} MB");
            var hasFolder = SourceFolderInput.IsChecked == true || !string.IsNullOrWhiteSpace(OutputInput.Text);
            ValidationText.Text = Localization.Text(_entries.Count == 0 ? "" : pending > 0 ? "正在读取…" :
                invalid > 0 ? Localization.Format($"处理 {ready} 个视频，跳过 {invalid} 个错误文件") : !hasFolder ? "请选择输出目录。" : "");
            ConfirmButton.IsEnabled = ready > 0 && pending == 0 && hasFolder;
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
    private void RemoveInvalidClick(object? sender, RoutedEventArgs args)
    {
        foreach (var entry in _entries.Where(entry => entry.HasError).ToArray()) _entries.Remove(entry);
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
            var available = _entries.Where(entry => entry.Plan is not null && !entry.HasError && !entry.Loading).ToArray();
            foreach (var entry in available)
            {
                if (!File.Exists(entry.Path)) throw new FileNotFoundException("源视频已移走，请重新添加。", entry.Path);
                _ = VideoCompression.Plan(new FileInfo(entry.Path).Length, entry.Info!, options);
            }
            var folder = SourceFolderInput.IsChecked == true ? Path.GetDirectoryName(available[0].Path)! : OutputInput.Text;
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("请选择输出目录。");
            new Storage().SaveToolOptions("video-compress",options);
            Close(new VideoCompressionRequest(available.Select(entry => entry.Path).ToArray(), options,
                Path.GetFullPath(folder), SourceFolderInput.IsChecked == true, SettingNameInput.IsChecked == true));
        }
        catch (Exception exception) { await Ui.Message(this, "压缩参数错误", exception.Message); }
    }
}
