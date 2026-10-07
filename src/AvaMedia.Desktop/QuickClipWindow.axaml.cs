using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed class QuickClipEntry : Observable
{
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public ConversionOptions Options { get; private set; }
    public MediaInfo? Info { get; internal set; }
    public Task Ready { get; internal set; } = Task.CompletedTask;
    internal int LoadRevision { get; set; }
    private Bitmap? _thumbnail;
    public Bitmap? Thumbnail { get => _thumbnail; internal set => Set(ref _thumbnail, value); }
    private string _details = "正在读取媒体信息…";
    public string Details { get => _details; internal set => Set(ref _details, value); }
    private string _previewStatus = "读取中…";
    public string PreviewStatus { get => _previewStatus; internal set => Set(ref _previewStatus, value); }
    public string Error { get; internal set; } = "";
    private bool _up, _down;
    public bool CanMoveUp { get => _up; internal set => Set(ref _up, value); }
    public bool CanMoveDown { get => _down; internal set => Set(ref _down, value); }
    public string Range => Localization.Format($"剪辑区间  {Time(Options.Start)} → {(Options.End > 0 ? Time(Options.End) : Localization.Text("结尾"))}");
    public QuickClipEntry(string path, ConversionOptions options) { Path = path; Options = options.Clone(); }
    public void SetOptions(ConversionOptions options) { Options = options.Clone(); Raise(nameof(Range)); }
    internal static string Time(double seconds) => MediaTime.Format(seconds);
}

public sealed partial class QuickClipWindow : Window
{
    public const string SourceDirectory = "输出至源文件目录";
    private readonly IMediaEngine _engine;
    private readonly ObservableCollection<QuickClipEntry> _entries = [];
    private readonly ObservableCollection<string> _folders;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _loadSlots = new(2);
    private ConversionOptions _defaults = new();
    private bool _closed;
    public IReadOnlyList<QuickClipEntry> Entries => _entries;
    public Task Ready => Task.WhenAll(_entries.Select(e => e.Ready));
    public string Preset => FormatCombo.SelectedItem as string ?? "Fast Copy";

    public QuickClipWindow() : this(new MediaEngine(new()), new AppSettings().OutputFolder, []) { }
    public QuickClipWindow(IMediaEngine engine, string outputFolder, string[] files)
    {
        InitializeComponent(); _engine = engine;
        _folders = new([System.IO.Path.GetFullPath(outputFolder), SourceDirectory]);
        FileList.ItemsSource = _entries; OutputCombo.ItemsSource = _folders; OutputCombo.SelectedIndex = 0;
        FormatCombo.ItemsSource = QuickClipBatch.Presets; FormatCombo.SelectedIndex = 0;
        AddFiles(files); UpdateOrder();
        DragDrop.SetAllowDrop(this, true); AddHandler(DragDrop.DropEvent, DropFiles);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(null); e.Handled = true; } else if (e.Key == Key.Delete && FileList.SelectedItem is QuickClipEntry entry) { RemoveEntry(entry); e.Handled = true; } };
        Closed += (_, _) => { _closed = true; _stop.Cancel(); foreach (var entry in _entries) entry.Thumbnail?.Dispose(); };
    }

    // Returns skipped files for the picker/drop UI. Folder imports use the same media filter.
    public IReadOnlyList<string> AddFiles(IEnumerable<string> paths)
    {
        var skipped = new List<string>();
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !QuickClipBatch.VideoExtensions.Contains(System.IO.Path.GetExtension(path).TrimStart('.'))) { skipped.Add(path); continue; }
            var full = System.IO.Path.GetFullPath(path);
            if (_entries.Any(e => string.Equals(e.Path, full, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) continue;
            var entry = new QuickClipEntry(full, _defaults); _entries.Add(entry); entry.Ready = LoadEntry(entry);
        }
        UpdateOrder(); return skipped;
    }

    private async Task LoadEntry(QuickClipEntry entry)
    {
        var acquired = false;var revision=++entry.LoadRevision;var options=entry.Options.Clone();
        entry.Info=null;entry.Error="";entry.Details="正在读取媒体信息…";entry.PreviewStatus="读取中…";
        try
        {
            await _loadSlots.WaitAsync(_stop.Token); acquired = true;
            var info = await _engine.Probe(entry.Path, _stop.Token,options.VideoStreamIndex,options.AudioStreamIndex);
            if (!info.HasVideo) throw new InvalidDataException("此文件不包含视频画面。");
            var ratio = (double)info.Width / Math.Max(1, info.Height);
            var width = Math.Clamp((int)Math.Round(88 * ratio), 1, 124);
            var height = Math.Clamp((int)Math.Round(124 / Math.Max(.001, ratio)), 1, 88);
            var bytes = await _engine.Thumbnail(entry.Path, Math.Min(options.Start, info.Duration), width, height, _stop.Token,videoStreamIndex:options.VideoStreamIndex,endExclusive:info.Duration>0&&options.Start>=info.Duration);
            using var stream = new MemoryStream(bytes); var bitmap = new Bitmap(stream);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_closed || revision!=entry.LoadRevision || !_entries.Contains(entry)) { bitmap.Dispose(); return; }
                entry.Info = info; entry.Error = ""; entry.Thumbnail?.Dispose(); entry.Thumbnail = bitmap; entry.PreviewStatus = "";
                entry.Details = Localization.Format($"{QuickClipEntry.Time(info.Duration)}  ·  {info.Width} × {info.Height}  ·  {info.VideoCodec} / {(info.HasAudio ? info.AudioCodec : Localization.Text("无音轨"))}  ·  {new FileInfo(entry.Path).Length / 1048576d:0.00} MB");
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_closed || revision!=entry.LoadRevision || !_entries.Contains(entry)) return;
                entry.Info=null;entry.Thumbnail?.Dispose();entry.Thumbnail=null;entry.Error = ex.Message; entry.Details = Localization.Format($"无法读取：{ex.Message}"); entry.PreviewStatus = "预览不可用";
            });
        }
        finally { if (acquired) _loadSlots.Release(); }
    }

    public void MoveEntry(QuickClipEntry entry, int delta)
    {
        var index = _entries.IndexOf(entry); var target = index + delta;
        if (index < 0 || target < 0 || target >= _entries.Count) return;
        _entries.Move(index, target); FileList.SelectedItem = entry; UpdateOrder();
    }
    public void RemoveEntry(QuickClipEntry entry) { if (_entries.Remove(entry)) entry.Thumbnail?.Dispose(); UpdateOrder(); }
    public void SortByName()
    {
        var sorted = _entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        for (int index = 0; index < sorted.Length; index++) _entries.Move(_entries.IndexOf(sorted[index]), index);
        UpdateOrder();
    }
    private void UpdateOrder()
    {
        for (int i = 0; i < _entries.Count; i++) { _entries[i].CanMoveUp = i > 0; _entries[i].CanMoveDown = i < _entries.Count - 1; }
        EmptyText.IsVisible = _entries.Count == 0; Localization.SetText(CountText,$"{_entries.Count} 个文件 / 片段");
    }
    private static QuickClipEntry? Entry(object? sender) => (sender as Control)?.DataContext as QuickClipEntry;
    private void UpClick(object? sender, RoutedEventArgs e) { if (Entry(sender) is { } item) MoveEntry(item, -1); }
    private void DownClick(object? sender, RoutedEventArgs e) { if (Entry(sender) is { } item) MoveEntry(item, 1); }
    private void RemoveClick(object? sender, RoutedEventArgs e) { if (Entry(sender) is { } item) RemoveEntry(item); }
    private void SortClick(object? sender, RoutedEventArgs e) => SortByName();
    private void ClearClick(object? sender, RoutedEventArgs e) { foreach (var item in _entries.ToArray()) RemoveEntry(item); }
    private void CancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void FormatChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PresetName is null || AppendSetting is null || ModeNote is null) return;
        PresetName.Text = Preset == "Fast Copy" ? "FastCopy" : Preset;
        Localization.SetContent(AppendSetting,$"添加设置名称 [{PresetName.Text}]");
        ModeNote.Text = Preset == "Fast Copy" ? "Fast Copy 保留源格式和编码，复制首个视频及音轨；剪辑起点受关键帧限制。" : Localization.Format($"输出 {Preset} 并重新编码，可应用精确剪辑、裁剪、速度和淡入淡出。");
    }

    private async void AddFilesClick(object? sender, RoutedEventArgs e)
    {
        var skipped = AddFiles(await Ui.Pick(this, "添加视频文件"));
        if (skipped.Count > 0) await Ui.Message(this, "未添加的文件", Localization.Format($"仅添加支持的视频文件。\n\n{string.Join("\n", skipped)}"));
    }
    private async void AddFolderClick(object? sender, RoutedEventArgs e)
    {
        if (await Ui.Folder(this, "添加文件夹内的视频（当前目录）") is not { } folder) return;
        try { AddFiles(Directory.EnumerateFiles(folder).Where(p => QuickClipBatch.VideoExtensions.Contains(System.IO.Path.GetExtension(p).TrimStart('.'))).OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase)); }
        catch (Exception ex) { await Ui.Message(this, "添加文件夹失败", ex.Message); }
    }
    private async void DropFiles(object? sender, DragEventArgs e)
    {
        var skipped = AddFiles(e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>() ?? []);
        if (skipped.Count > 0) await Ui.Message(this, "未添加的文件", string.Join("\n", skipped));
    }
    private async void BrowseOutputClick(object? sender, RoutedEventArgs e)
    {
        if (await Ui.Folder(this, "选择输出目录") is not { } folder) return;
        if (!_folders.Contains(folder)) _folders.Insert(0, folder); OutputCombo.SelectedItem = folder;
    }
    private async void FolderClick(object? sender, RoutedEventArgs e)
    {
        if (Entry(sender) is not { } entry) return;
        try
        {
            PlatformServices.RevealFile(entry.Path);
        }
        catch (Exception ex) { await Ui.Message(this, "打开目录失败", ex.Message); }
    }
    private async void InfoClick(object? sender, RoutedEventArgs e)
    {
        if (Entry(sender) is not { } entry) return; await entry.Ready;
        await Ui.Message(this, Localization.Format($"媒体信息 · {entry.Name}"), entry.Path + "\n\n" + entry.Details + "\n" + entry.Range + "\n\n" + (entry.Info?.RawJson ?? entry.Error));
    }
    private async void EditEntryClick(object? sender, RoutedEventArgs e)
    {
        if (Entry(sender) is not { } entry) return;
        await entry.Ready; if (_closed) return;
        if (entry.Info is null) { await Ui.Message(this, "媒体打开失败", entry.Error); return; }
        var draft = QuickClipBatch.ResolveOptions(entry.Path, Preset, entry.Options);
        // Editing filters is allowed in the editor; accepting them selects a re-encoding preset.
        draft.CopyStreams = false;
        var result = await new EditorWindow(_engine, entry.Path, draft, "clip").ShowDialog<ConversionOptions?>(this);
        if (result is null || !_entries.Contains(entry)) return;
        entry.SetOptions(result); SwitchForFilters(result, draft); entry.Ready = LoadEntry(entry);
    }
    private void SwitchForFilters(ConversionOptions options,ConversionOptions? previous=null)
    {
        if (Preset == "Fast Copy" && (MediaEngine.HasFilters(options) || options.SampleRate>0 || options.AudioChannels>0 || previous is not null && (options.Quality!=previous.Quality || options.AudioBitrate!=previous.AudioBitrate || options.VideoCodec!=previous.VideoCodec || options.AudioCodec!=previous.AudioCodec)))
        {
            FormatCombo.SelectedItem = "MP4";
            ModeNote.Text = "已切换为 MP4：裁剪、速度或其他滤镜需要重新编码。每个文件的剪辑区间独立保存。";
        }
    }
    private async void OutputSettingsClick(object? sender, RoutedEventArgs e)
    {
        var draft = _defaults.Clone(); draft.Format = Preset == "Fast Copy" ? "mp4" : Preset.ToLowerInvariant();
        var changed = await new OptionsWindow(draft, Preset == "Fast Copy").ShowDialog<ConversionOptions?>(this);
        if (changed is null) return;
        SwitchForFilters(changed, _defaults); _defaults = changed.Clone();
        foreach (var entry in _entries)
        {
            // Output settings are shared. Preserve the per-file timeline, crop and watermark regions.
            var o = changed.Clone(); var old = entry.Options;
            o.Start = old.Start; o.End = old.End; o.CropX = old.CropX; o.CropY = old.CropY; o.CropWidth = old.CropWidth; o.CropHeight = old.CropHeight;
            o.DelogoX = old.DelogoX; o.DelogoY = old.DelogoY; o.DelogoWidth = old.DelogoWidth; o.DelogoHeight = old.DelogoHeight;
            var tracksChanged=o.VideoStreamIndex!=old.VideoStreamIndex || o.AudioStreamIndex!=old.AudioStreamIndex;entry.SetOptions(o);if(tracksChanged)entry.Ready=LoadEntry(entry);
        }
    }

    public void SplitEntry(QuickClipEntry entry, int parts)
    {
        if (entry.Info is null) throw new InvalidOperationException("请等待媒体信息读取完成。");
        var options = QuickClipBatch.Split(entry.Options, entry.Info.Duration, parts);
        ReplaceWithSegments(entry, options);
    }
    private void ReplaceWithSegments(QuickClipEntry entry, IReadOnlyList<ConversionOptions> options)
    {
        var index = _entries.IndexOf(entry); if (index < 0) return;
        _entries.RemoveAt(index); entry.Thumbnail?.Dispose();
        foreach (var draft in options)
        {
            var segment = new QuickClipEntry(entry.Path, draft); _entries.Insert(index++, segment); segment.Ready = LoadEntry(segment);
        }
        UpdateOrder();
    }
    private async void SplitClick(object? sender, RoutedEventArgs e)
    {
        if (Entry(sender) is not { } entry) return; await entry.Ready;
        if (_closed || !_entries.Contains(entry)) return;
        if (entry.Info is null) { await Ui.Message(this, "分割失败", entry.Error); return; }
        var options = await new ClipSplitWindow(entry.Options, entry.Info.Duration).ShowDialog<IReadOnlyList<ConversionOptions>?>(this);
        if (!_closed && options is not null) ReplaceWithSegments(entry, options);
    }

    public ConversionRequest CreateRequest()
    {
        if (_entries.Count == 0) throw new ArgumentException("请添加文件。");
        var items = new List<QuickClipInput>();
        foreach (var entry in _entries)
        {
            if (!entry.Ready.IsCompleted) throw new InvalidOperationException("请等待媒体信息读取完成。");
            if (entry.Info is null) throw new InvalidDataException(entry.Name + "：" + entry.Error);
            var options = QuickClipBatch.ResolveOptions(entry.Path, Preset, entry.Options);
            try{MediaEngine.ValidateEdits(new() { FeatureId = "clip", Inputs = [entry.Path], Options = options, Output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AvaMedia-validation-" + Guid.NewGuid() + "." + options.Format) },[entry.Info]);}
            catch(ArgumentException ex){throw new ArgumentException(entry.Name+"："+ex.Message,ex);}
            items.Add(new(entry.Path, options));
        }
        var output = OutputCombo.SelectedItem as string ?? _folders[0];
        return new(Catalog.Find("clip"), items.Select(i => i.Path).ToArray(), output == SourceDirectory ? _folders.First(f => f != SourceDirectory) : System.IO.Path.GetFullPath(output),
            items[0].Options.Clone(), items, output == SourceDirectory, AppendSetting.IsChecked == true ? PresetName.Text ?? "" : "");
    }
    private async void ConfirmClick(object? sender, RoutedEventArgs e)
    {
        OkButton.IsEnabled = false;
        try { await Ready; if (!_closed) Close(CreateRequest()); }
        catch (Exception ex) { if (!_closed) await Ui.Message(this, "参数错误", ex.Message); }
        finally { OkButton.IsEnabled = true; }
    }
}
