using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed record VideoSlimmingRequest(string[] Files, VideoSlimmingOptions Options, string OutputFolder,
    bool OutputToSource, IReadOnlyDictionary<string, VideoSlimmingAnalysis> Analyses);

public sealed class VideoSlimmingEntry(string path) : Observable
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public MediaInfo? Info { get; set; }
    public long Bytes { get; set; }
    public bool Loading { get; set; } = true;
    public bool Analyzing { get; set; }
    public string InspectionError { get; set; } = "";
    public string ValidationError { get; set; } = "";
    public string AnalysisError { get; set; } = "";
    public string Error => InspectionError.Length > 0 ? InspectionError : ValidationError.Length > 0 ? ValidationError : AnalysisError;
    public bool HasError => Error.Length > 0;
    public VideoSlimmingAnalysis? Analysis { get; set; }
    public string SourceSummary => Info is { } source ? Localization.Format(
        $"{MediaTime.Format(source.Duration)} · {source.Width} × {source.Height} · {source.VideoCodec.ToUpperInvariant()}") :
        Loading ? Localization.Text("正在读取媒体信息…") : "";
    public string SourceSize => Bytes > 0 ? Localization.Format($"{Bytes / 1000000d:0.##} MB") : "—";
    public string EstimatedSize => Analysis is { } analysis ? Localization.Format($"{analysis.EstimatedBytes / 1000000d:0.##} MB") : "—";
    public string Saving => Analysis is { } analysis ? Localization.Format($"{analysis.EstimatedSaving:0.#}%") : "—";
    public bool Unavailable => HasError || Analysis?.Worthwhile == false;
    public string Status => Localization.Text(Loading ? "读取中" : Analyzing ? "分析中" : HasError ? "需要处理" :
        Analysis is null ? "待分析" : Analysis.Worthwhile ? "可瘦身" : "节省不足 5%");
    public string QualitySummary => Analysis is not { } analysis ? "" : Localization.Format(
        $"SSIM {analysis.MeanSsim:0.####} · {analysis.Samples} 段采样 · {analysis.AudioTracks} 音轨 · {analysis.SubtitleTracks} 字幕轨");
    public void Refresh() => Raise(string.Empty);
}

public partial class VideoSlimmingWindow : Window
{
    private readonly IMediaEngine _engine;
    private readonly MediaPreviewPanel _preview;
    private readonly ObservableCollection<VideoSlimmingEntry> _entries = [];
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _analysisCancellation;
    private bool _busy;
    private bool _adding;
    private bool _closed;
    private string _completionStatus = "";

    public VideoSlimmingWindow() : this(new MediaEngine(new()), "", []) { }
    public VideoSlimmingWindow(IMediaEngine engine, string outputFolder, string[] files,
        VideoSlimmingOptions? initial = null, bool editing = false)
    {
        InitializeComponent(); ToolExecution.Configure(this,ConfirmButton,"开始瘦身",editing); _engine = engine; WindowArtwork.SetKind(this, "gear");
        var options = initial ?? new Storage().LoadToolOptions<VideoSlimmingOptions>("video-slim") ?? new();
        PresetInput.ItemsSource = new[] { "保真", "均衡", "更小" };
        CodecInput.ItemsSource = new[] { "HEVC（H.265）", "H.264（兼容性优先）" };
        FormatInput.ItemsSource = new[] { "MKV", "MP4" };
        PresetInput.SelectedIndex = (int)options.Preset;
        CodecInput.SelectedIndex = options.Codec == "h264" ? 1 : 0;
        FormatInput.SelectedIndex = options.Format == "mp4" ? 1 : 0;
        OutputInput.Text = outputFolder;
        SourceFolderInput.IsChecked = !editing && engine.Settings.OutputToSource;
        if (editing) { Title = "编辑视频瘦身任务"; ConfirmButton.Content = "保存修改"; }
        _preview = new MediaPreviewPanel(engine); PreviewHost.Content = _preview;
        SourceList.ItemsSource = _entries;
        foreach (var combo in new[] { PresetInput, CodecInput, FormatInput })
            combo.SelectionChanged += (_, _) =>
            {
                foreach (var entry in _entries) { entry.Analysis = null; entry.AnalysisError = ""; }
                _completionStatus = "";
                Refresh();
            };
        SourceFolderInput.IsCheckedChanged += (_, _) => UpdateOutput();
        OutputInput.TextChanged += (_, _) => Refresh();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        { e.DragEffects = !_busy && !_adding && e.DataTransfer.TryGetFiles() is not null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }, RoutingStrategies.Bubble, true);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            e.Handled = true;
            if (!_busy && !_adding) await AddFilesAsync(e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? []);
        }, RoutingStrategies.Bubble, true);
        Opened += async (_, _) =>
        {
            await AddFilesAsync(files);
            if (initial?.Analysis is { } analysis && _entries.Count == 1 && analysis.Matches(_entries[0].Path, options,
                engine.Settings.MultiThread ? Math.Clamp(engine.Settings.CpuThreads, 1, 16) : 1))
            { _entries[0].Analysis = analysis; Refresh(); }
        };
        Closed += (_, _) => { _closed = true; _preview.Dispose(); _lifetime.Cancel(); _analysisCancellation?.Cancel(); Localization.Changed -= LanguageChanged; };
        Localization.Changed += LanguageChanged;
        UpdateOutput(); Refresh();
    }

    private void LanguageChanged(object? sender, EventArgs args) => Refresh();
    private VideoSlimmingOptions ReadOptions() => new()
    {
        Preset = (VideoSlimmingPreset)PresetInput.SelectedIndex,
        Codec = CodecInput.SelectedIndex == 1 ? "h264" : "hevc",
        Format = FormatInput.SelectedIndex == 1 ? "mp4" : "mkv"
    };
    private void UpdateOutput()
    {
        OutputInput.IsEnabled = BrowseButton.IsEnabled = !_busy && SourceFolderInput.IsChecked != true;
        Refresh();
    }

    private void Refresh()
    {
        if (_closed) return;
        var options = ReadOptions();
        foreach (var entry in _entries)
        {
            entry.ValidationError = "";
            if (entry.Info is { } info)
            {
                try { VideoSlimming.ValidateSource(info, options); }
                catch (ArgumentException exception) { entry.ValidationError = exception.Message; }
            }
            entry.Refresh();
        }
        OptionsPanel.IsEnabled = !_busy; SourceList.IsEnabled = !_busy;
        ImportPanel.IsEnabled = !_busy && !_adding;
        RemoveButton.IsEnabled = !_busy && !_adding && SourceList.SelectedItems?.Count > 0;
        RemoveUnavailableButton.IsEnabled = !_busy && !_adding && _entries.Any(entry => entry.Unavailable);
        SourceFolderInput.IsEnabled = !_busy;
        OutputInput.IsEnabled = BrowseButton.IsEnabled = !_busy && SourceFolderInput.IsChecked != true;
        AnalysisProgress.IsVisible = _busy;
        var available = _entries.Where(entry => !entry.Loading && !entry.HasError && entry.Analysis?.Worthwhile != false).ToArray();
        var valid = available.Length > 0 && !_entries.Any(entry => entry.Loading);
        var stopping = _analysisCancellation?.IsCancellationRequested == true;
        AnalyzeButton.Content = Localization.Text(_busy ? stopping ? "正在停止…" : "停止分析" : "预估体积");
        AnalyzeButton.IsEnabled = _busy ? !stopping : !_adding && _entries.Any(entry => !entry.Loading && entry.InspectionError.Length == 0 && entry.ValidationError.Length == 0);
        var hasFolder = SourceFolderInput.IsChecked == true || !string.IsNullOrWhiteSpace(OutputInput.Text);
        ConfirmButton.IsEnabled = !_busy && !_adding && valid && hasFolder;
        var analyzed = _entries.Count(entry => entry.Analysis is not null);
        FileCountText.Text = _entries.Count > 0 ? Localization.Format($"{_entries.Count} 个视频") : "";
        EmptyText.IsVisible = _entries.Count == 0;
        SummaryText.Text = _entries.Count == 0 ? "" : analyzed == _entries.Count
            ? Localization.Format($"原体积 {_entries.Sum(entry => entry.Bytes) / 1000000d:0.##} MB · 预计 {_entries.Sum(entry => entry.Analysis!.EstimatedBytes) / 1000000d:0.##} MB（采样估算）")
            : Localization.Format($"原体积 {_entries.Sum(entry => entry.Bytes) / 1000000d:0.##} MB · 已分析 {analyzed}/{_entries.Count}");
        if (!_busy)
        {
            var unavailable = _entries.Count(entry => entry.Unavailable);
            StatusText.Text = _adding ? Localization.Text("正在读取媒体信息…") : unavailable > 0
                ? Localization.Format($"处理 {available.Length} 个视频，跳过 {unavailable} 个不可处理文件") : !hasFolder && _entries.Count > 0
                ? Localization.Text("请选择输出目录。") : _completionStatus.Length > 0 ? Localization.Text(_completionStatus)
                : _entries.Count > 0 && analyzed < _entries.Count ? Localization.Text("未预估的视频将在执行时自动分析。") : "";
        }
        RefreshDetails();
    }

    private void SourceSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (RemoveButton is not null) RemoveButton.IsEnabled = !_busy && !_adding && SourceList.SelectedItems?.Count > 0;
        RefreshDetails();
    }
    private void RefreshDetails()
    {
        if (AnalysisDetails is null || _preview is null) return;
        if (SourceList.SelectedItem is null && _entries.Count > 0) SourceList.SelectedIndex = 0;
        var entry = SourceList.SelectedItem as VideoSlimmingEntry;
        _preview.SetSource(entry?.Path); SizeChart.SetSizes(entry?.Bytes ?? 0,entry?.Analysis?.EstimatedBytes);
        AnalysisDetails.IsVisible = entry is { Analysis: not null } || entry?.HasError == true;
        SelectedFileText.Text = entry?.Name ?? "";
        QualityText.Text = entry?.QualitySummary ?? "";
        QualityText.IsVisible = QualityText.Text.Length > 0;
        ErrorText.Text = entry?.Error ?? "";
        ErrorText.IsVisible = ErrorText.Text.Length > 0;
        if (entry?.HasError == true) AnalysisDetails.IsExpanded = true;
    }

    private async Task AddFilesAsync(IEnumerable<string> files)
    {
        if (_busy || _adding || _closed) return;
        _adding = true; _completionStatus = "";
        try
        {
            var used = _entries.Select(entry => entry.Path).ToHashSet(VideoFolderScanner.PathComparer);
            var added = files.Select(Path.GetFullPath).Where(used.Add).Select(path => new VideoSlimmingEntry(path)).ToArray();
            foreach (var entry in added) _entries.Add(entry);
            if (SourceList.SelectedItem is null && _entries.Count > 0) SourceList.SelectedIndex = 0;
            Refresh();
            foreach (var entry in added)
            {
                try
                {
                    if (!VideoFolderScanner.IsVideoFile(entry.Path)) throw new ArgumentException("请选择支持的视频文件。");
                    entry.Bytes = new FileInfo(entry.Path).Length;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    entry.Info = await _engine.Probe(entry.Path, timeout.Token);
                }
                catch (OperationCanceledException) { entry.InspectionError = "媒体读取超时或已取消。"; }
                catch (Exception exception) { entry.InspectionError = exception.Message; }
                entry.Loading = false; Refresh();
                if (_closed) return;
            }
        }
        catch (Exception exception) { if (!_closed) await Ui.Message(this, "添加视频失败", exception.Message); }
        finally { _adding = false; Refresh(); }
    }

    private async void AnalyzeClick(object? sender, RoutedEventArgs args)
    {
        if (_busy) { _analysisCancellation?.Cancel(); AnalyzeButton.IsEnabled = false; AnalyzeButton.Content = Localization.Text("正在停止…"); return; }
        if (_engine is not MediaEngine engine) { await Ui.Message(this, "分析失败", "媒体引擎不支持视频瘦身。"); return; }
        _analysisCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var entries = _entries.Where(entry => !entry.Loading && entry.InspectionError.Length == 0 && entry.ValidationError.Length == 0).ToArray();
        if (entries.Length == 0) { _analysisCancellation.Dispose(); _analysisCancellation = null; return; }
        var token = _analysisCancellation.Token;
        AnalysisProgress.Value = 0; _completionStatus = "";
        _busy = true; Refresh();
        try
        {
            var options = ReadOptions();
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index]; var completed = index;
                token.ThrowIfCancellationRequested();
                entry.AnalysisError = ""; entry.Analysis = null; entry.Analyzing = true; entry.Refresh();
                StatusText.Text = Localization.Format($"分析 {index + 1}/{entries.Length} · {entry.Name}");
                try
                {
                    entry.Analysis = await new VideoSlimming(engine).AnalyzeAsync(entry.Path, options, (value, detail) =>
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (_closed || !_busy || !entry.Analyzing || token.IsCancellationRequested) return;
                            AnalysisProgress.Value = (completed + Math.Clamp(value, 0, 100) / 100) / entries.Length * 100;
                            StatusText.Text = Localization.Join(" · ", [Localization.Format($"分析 {completed + 1}/{entries.Length}"), entry.Name, detail]);
                        }), token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) { entry.AnalysisError = exception.Message; }
                finally { entry.Analyzing = false; }
                AnalysisProgress.Value = (index + 1d) / entries.Length * 100;
                Refresh();
            }
            _completionStatus = "分析完成";
        }
        catch (OperationCanceledException) { _completionStatus = "分析已停止"; }
        finally { _analysisCancellation.Dispose(); _analysisCancellation = null; _busy = false; Refresh(); }
    }

    private async void AddClick(object? sender, RoutedEventArgs args) => await AddFilesAsync(await Ui.Pick(this, "添加视频"));
    private async void FolderClick(object? sender, RoutedEventArgs args)
    {
        if (await Ui.Folder(this, "添加视频文件夹") is not { } folder) return;
        try { await AddFilesAsync(Directory.EnumerateFiles(folder).Where(VideoFolderScanner.IsVideoFile)); }
        catch (Exception exception) { await Ui.Message(this, "添加视频失败", exception.Message); }
    }
    private void RemoveClick(object? sender, RoutedEventArgs args)
    {
        foreach (var entry in SourceList.SelectedItems?.OfType<VideoSlimmingEntry>().ToArray() ?? []) _entries.Remove(entry);
        _completionStatus = "";
        if (SourceList.SelectedItem is null && _entries.Count > 0) SourceList.SelectedIndex = 0;
        Refresh();
    }
    private void RemoveUnavailableClick(object? sender, RoutedEventArgs args)
    {
        foreach (var entry in _entries.Where(entry => entry.Unavailable).ToArray()) _entries.Remove(entry);
        _completionStatus = "";
        if (SourceList.SelectedItem is null && _entries.Count > 0) SourceList.SelectedIndex = 0;
        Refresh();
    }
    private async void BrowseClick(object? sender, RoutedEventArgs args)
    { if (await Ui.Folder(this, "选择输出目录") is { } folder) OutputInput.Text = folder; }
    private void CancelClick(object? sender, RoutedEventArgs args) => Close(null);
    private async void ConfirmClick(object? sender, RoutedEventArgs args)
    {
        Refresh(); if (!ConfirmButton.IsEnabled) return;
        try
        {
            var options = ReadOptions(); options.Validate();
            var available=_entries.Where(entry=>!entry.Loading&&!entry.HasError&&entry.Analysis?.Worthwhile!=false).ToArray();
            new Storage().SaveToolOptions("video-slim",options with { Analysis=null });
            var folder = SourceFolderInput.IsChecked == true ? Path.GetDirectoryName(available[0].Path)! : OutputInput.Text;
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("请选择输出目录。");
            ToolExecution.Complete(this, new VideoSlimmingRequest(available.Select(entry => entry.Path).ToArray(), options, Path.GetFullPath(folder),
                SourceFolderInput.IsChecked == true, available.Where(entry => entry.Analysis is not null)
                    .ToDictionary(entry => entry.Path, entry => entry.Analysis!)));
        }
        catch (Exception exception) { await Ui.Message(this, "瘦身参数错误", exception.Message); }
    }
}
