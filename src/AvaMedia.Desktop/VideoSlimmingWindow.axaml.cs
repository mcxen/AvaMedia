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
    public string InspectionError { get; set; } = "";
    public string Error { get; set; } = "";
    public bool HasError => Error.Length > 0;
    public VideoSlimmingAnalysis? Analysis { get; set; }
    public string SourceSummary => Info is { } source ? Localization.Format(
        $"{MediaTime.Format(source.Duration)} · {source.Width} × {source.Height} · {Bytes / 1000000d:0.##} MB · {source.VideoCodec.ToUpperInvariant()}") :
        Loading ? Localization.Text("正在读取媒体信息…") : "";
    public string ResultSummary => Analysis is not { } analysis ? Localization.Text("执行前自动分析") :
        analysis.Worthwhile ? Localization.Format($"预计 {analysis.EstimatedBytes / 1000000d:0.##} MB · 节省 {analysis.EstimatedSaving:0.#}%（采样估算）") :
        Localization.Text("不建议瘦身：预计节省不足 5%");
    public string QualitySummary => Analysis is not { } analysis ? "" : Localization.Format(
        $"SSIM {analysis.MeanSsim:0.####} · {analysis.Samples} 段采样 · {analysis.AudioTracks} 音轨 · {analysis.SubtitleTracks} 字幕轨");
    public void Refresh() => Raise(string.Empty);
}

public partial class VideoSlimmingWindow : Window
{
    private readonly IMediaEngine _engine;
    private readonly ObservableCollection<VideoSlimmingEntry> _entries = [];
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _analysisCancellation;
    private bool _busy;
    private bool _closed;

    public VideoSlimmingWindow() : this(new MediaEngine(new()), "", []) { }
    public VideoSlimmingWindow(IMediaEngine engine, string outputFolder, string[] files,
        VideoSlimmingOptions? initial = null, bool editing = false)
    {
        InitializeComponent(); _engine = engine; WindowArtwork.SetKind(this, "gear");
        var options = initial ?? new();
        PresetInput.ItemsSource = new[] { "保真", "均衡", "更小" };
        CodecInput.ItemsSource = new[] { "HEVC（H.265）", "H.264（兼容性优先）" };
        FormatInput.ItemsSource = new[] { "MKV", "MP4" };
        PresetInput.SelectedIndex = (int)options.Preset;
        CodecInput.SelectedIndex = options.Codec == "h264" ? 1 : 0;
        FormatInput.SelectedIndex = options.Format == "mp4" ? 1 : 0;
        OutputInput.Text = outputFolder;
        SourceFolderInput.IsChecked = !editing && engine.Settings.OutputToSource;
        if (editing) { Title = "编辑视频瘦身任务"; ConfirmButton.Content = "保存修改"; }
        SourceList.ItemsSource = _entries;
        foreach (var combo in new[] { PresetInput, CodecInput, FormatInput })
            combo.SelectionChanged += (_, _) =>
            {
                foreach (var entry in _entries) { entry.Analysis = null; entry.Error = entry.InspectionError; }
                Refresh();
            };
        SourceFolderInput.IsCheckedChanged += (_, _) => UpdateOutput();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        { e.DragEffects = !_busy && e.DataTransfer.TryGetFiles() is not null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }, RoutingStrategies.Bubble, true);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            e.Handled = true;
            if (!_busy) await AddFilesAsync(e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? []);
        }, RoutingStrategies.Bubble, true);
        Opened += async (_, _) =>
        {
            await AddFilesAsync(files);
            if (initial?.Analysis is { } analysis && _entries.Count == 1 && analysis.Matches(_entries[0].Path, options,
                engine.Settings.MultiThread ? Math.Clamp(engine.Settings.CpuThreads, 1, 16) : 1))
            { _entries[0].Analysis = analysis; Refresh(); }
        };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _analysisCancellation?.Cancel(); Localization.Changed -= LanguageChanged; };
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
    private void UpdateOutput() => OutputInput.IsEnabled = BrowseButton.IsEnabled = !_busy && SourceFolderInput.IsChecked != true;

    private void Refresh()
    {
        if (_closed) return;
        var options = ReadOptions();
        foreach (var entry in _entries)
        {
            if (entry.InspectionError.Length > 0) entry.Error = entry.InspectionError;
            else if (entry.Info is { } info)
            {
                try { VideoSlimming.ValidateSource(info, options); }
                catch (ArgumentException exception) { entry.Error = exception.Message; }
            }
            entry.Refresh();
        }
        OptionsPanel.IsEnabled = !_busy; SourceList.IsEnabled = !_busy;
        SourceFolderInput.IsEnabled = !_busy; UpdateOutput();
        AnalysisProgress.IsVisible = _busy;
        var valid = _entries.Count > 0 && _entries.All(entry => !entry.Loading && !entry.HasError);
        AnalyzeButton.Content = Localization.Text(_busy ? "停止分析" : "分析体积");
        AnalyzeButton.IsEnabled = _busy || _entries.Count > 0 && _entries.All(entry => !entry.Loading && entry.InspectionError.Length == 0);
        ConfirmButton.IsEnabled = !_busy && valid && _entries.All(entry => entry.Analysis?.Worthwhile != false);
        var analyzed = _entries.Count(entry => entry.Analysis is not null);
        SummaryText.Text = Localization.Format($"{_entries.Count} 个视频 · 原体积 {_entries.Sum(entry => entry.Bytes) / 1000000d:0.##} MB · 已分析 {analyzed}");
    }

    private async Task AddFilesAsync(IEnumerable<string> files)
    {
        try
        {
            var used = _entries.Select(entry => entry.Path).ToHashSet(VideoFolderScanner.PathComparer);
            var added = files.Select(Path.GetFullPath).Where(used.Add).Select(path => new VideoSlimmingEntry(path)).ToArray();
            foreach (var entry in added) _entries.Add(entry);
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
                entry.Loading = false; entry.Error = entry.InspectionError; Refresh();
                if (_closed) return;
            }
        }
        catch (Exception exception) { if (!_closed) await Ui.Message(this, "添加视频失败", exception.Message); }
    }

    private async void AnalyzeClick(object? sender, RoutedEventArgs args)
    {
        if (_busy) { _analysisCancellation?.Cancel(); return; }
        if (_engine is not MediaEngine engine) { await Ui.Message(this, "分析失败", "媒体引擎不支持视频瘦身。"); return; }
        _analysisCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _busy = true; Refresh();
        try
        {
            var options = ReadOptions();
            foreach (var entry in _entries)
            {
                entry.Error = ""; entry.Analysis = null;
                try
                {
                    entry.Analysis = await new VideoSlimming(engine).AnalyzeAsync(entry.Path, options, (value, detail) =>
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (_closed || !_busy) return;
                            AnalysisProgress.Value = value; StatusText.Text = Localization.Join(" · ", [entry.Name, detail]);
                        }), _analysisCancellation.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) { entry.Error = exception.Message; }
                Refresh();
            }
            StatusText.Text = Localization.Text("分析完成");
        }
        catch (OperationCanceledException) { if (!_closed) StatusText.Text = Localization.Text("分析已停止"); }
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
            if (string.IsNullOrWhiteSpace(OutputInput.Text)) throw new ArgumentException("请选择输出目录。");
            Close(new VideoSlimmingRequest(_entries.Select(entry => entry.Path).ToArray(), options, Path.GetFullPath(OutputInput.Text),
                SourceFolderInput.IsChecked == true, _entries.Where(entry => entry.Analysis is not null)
                    .ToDictionary(entry => entry.Path, entry => entry.Analysis!)));
        }
        catch (Exception exception) { await Ui.Message(this, "瘦身参数错误", exception.Message); }
    }
}
