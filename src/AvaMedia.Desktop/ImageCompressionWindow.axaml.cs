using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed class ImageCompressionEntry(string path) : Observable
{
    private bool _include = true, _encoding, _canInclude = true;
    private string? _error;
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public bool Include { get => _include; set => Set(ref _include, value); }
    public bool CanInclude { get => _canInclude; internal set => Set(ref _canInclude, value); }
    public string? Error { get => _error; internal set { if (Set(ref _error, value)) Raise(nameof(Status)); } }
    public bool Encoding { get => _encoding; internal set { if (Set(ref _encoding, value)) Raise(nameof(Status)); } }
    public ImageCompressionSource? Source { get; private set; }
    public ImageCompressionResult? Result { get; private set; }
    internal ImageCompressionOptions? ResultOptions { get; set; }
    internal Task Ready { get; set; } = Task.CompletedTask;
    public string SourceDescription => Source is null ? "" : $"{Source.Width} × {Source.Height} · {ImageCompression.Bytes(Source.Bytes)}";
    public string Status => Error ?? (Encoding ? Localization.Text("正在实际编码…") : Source is null ? Localization.Text("正在读取…") :
        Result is null ? Localization.Text("等待压缩预览") : Result.IsSmaller ? Localization.Format($"{ImageCompression.Bytes(Result.OutputBytes)} · 节省 {Result.SavedPercent:0.##}%") : Localization.Text("当前设置未压小"));
    internal void SetSource(ImageCompressionSource source) { Source = source; Raise(nameof(SourceDescription)); Raise(nameof(Status)); }
    internal void SetResult(ImageCompressionResult? result) { Result = result; Raise(nameof(Status)); }
}

public sealed partial class ImageCompressionWindow : Window
{
    private readonly IImageCompressor _compressor;
    private readonly IMediaPreview _preview;
    private readonly ObservableCollection<ImageCompressionEntry> _entries = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _inspectionSlots = new(2);
    private readonly List<Task> _inspections = [];
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "AvaMedia-image-preview-" + Guid.NewGuid().ToString("N"));
    private CancellationTokenSource? _previewCancellation, _batchCancellation;
    private Task _previewTask = Task.CompletedTask, _batchTask = Task.CompletedTask;
    private bool _initializing = true, _busy, _closed;
    private Bitmap? _sourceBitmap, _resultBitmap;
    private ImageCompressionEntry? _displayedEntry;
    private ImageCompressionEntry? Active => FileList.SelectedItem as ImageCompressionEntry;
    public Task Ready => Task.WhenAll(_entries.Select(entry => entry.Ready).Append(_previewTask));

    public ImageCompressionWindow() : this(new MediaEngine(new()), MediaFolders.DefaultOutput) { }
    public ImageCompressionWindow(IMediaEngine engine, string outputFolder, IEnumerable<string>? files = null,
        ImageCompressionOptions? initialOptions = null, bool canStart = true, IImageCompressor? compressor = null, bool editing = false)
    {
        _compressor = compressor ?? new FfmpegImageCompressor(engine);
        _preview = engine;
        InitializeComponent();
        FileList.ItemsSource = _entries;
        OutputInput.Text = outputFolder;
        SourceFolderInput.IsChecked = !editing && engine.Settings.OutputToSource;
        StartInput.IsChecked = canStart && !editing; StartInput.IsEnabled = canStart;
        if (editing) { Title = "编辑图片压缩任务"; ConfirmButton.Content = "保存修改"; StartInput.IsVisible = false; }
        else if (!canStart) ToolTip.SetTip(StartInput, "已有任务正在运行；本次图片先加入等待队列。");
        if (initialOptions is { } options) SetOptions(options);
        foreach (var check in new[] { LosslessInput, ResizeInput }) check.IsCheckedChanged += (_, _) => ParametersChanged(null, null!);
        SourceFolderInput.IsCheckedChanged += (_, _) => RefreshControls();
        SideBySideInput.IsCheckedChanged += (_, _) =>
        { Comparison.SideBySide = SideBySideInput.IsChecked == true; CompareSlider.IsVisible = !Comparison.SideBySide; Comparison.ResetView(); };
        foreach (var number in new[] { QualityInput, EdgeInput }) number.PropertyChanged += (_, change) =>
        { if (change.Property == NumericUpDown.ValueProperty || change.Property == NumericUpDown.TextProperty) ParametersChanged(null, null!); };
        OutputInput.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) RefreshControls(); };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, args) => args.DragEffects = _busy ? DragDropEffects.None : DragDropEffects.Copy);
        AddHandler(DragDrop.DropEvent, (_, args) =>
        { if (!_busy) AddFiles(args.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? []); });
        Closed += async (_, _) => await CleanupAsync();
        _initializing = false;
        if (files is not null) AddFiles(files);
        RefreshControls();
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var known = new HashSet<string>(_entries.Select(entry => entry.Path), comparer);
        var skipped = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !ImageCompression.Supports(path)) { skipped++; continue; }
            var full = Path.GetFullPath(path);
            if (!known.Add(full)) continue;
            var entry = new ImageCompressionEntry(full);
            entry.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(entry.Include)) RefreshControls(); };
            _entries.Add(entry);
            entry.Ready = InspectAsync(entry);
            _inspections.Add(entry.Ready);
        }
        if (FileList.SelectedItem is null && _entries.Count > 0) FileList.SelectedIndex = 0;
        if (skipped > 0) StatusText.Text = Localization.Format($"跳过 {skipped} 项；支持静态 HEIC / HEIF、JPEG、PNG、WebP 和 BMP。");
        RefreshControls();
    }
    private async Task InspectAsync(ImageCompressionEntry entry)
    {
        try
        {
            await _inspectionSlots.WaitAsync(_lifetime.Token);
            try { var source = await _compressor.InspectAsync(entry.Path, _lifetime.Token); if (!_closed) entry.SetSource(source); }
            finally { _inspectionSlots.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) { entry.Error = ex.Message; entry.Include = false; } }
        if (!_closed) RefreshControls();
    }
    private ImageCompressionOptions ReadOptions()
    {
        var format = ((ComboBoxItem)FormatCombo.SelectedItem!).Tag!.ToString()!;
        var lossless = format == "webp" && LosslessInput.IsChecked == true;
        var options = new ImageCompressionOptions { Format = format, Lossless = lossless,
            Quality = format == "png" || lossless ? 100 : Integer(QualityInput, "图片质量"),
            MaxDimension = ResizeInput.IsChecked == true ? Integer(EdgeInput, "最长边") : 0 };
        options.Validate(); return options;
    }
    private static int Integer(NumericUpDown input, string label)
    {
        if (!decimal.TryParse(input.Text, NumberStyles.Integer, input.NumberFormat, out var value) || value < input.Minimum || value > input.Maximum)
            throw new ArgumentException(Localization.Format($"{Localization.Key(label)}：请输入 {input.Minimum} 到 {input.Maximum} 之间的整数。"));
        return decimal.ToInt32(value);
    }
    private void SetOptions(ImageCompressionOptions options)
    {
        FormatCombo.SelectedIndex = options.Format switch { "jpg" => 1, "png" => 2, _ => 0 };
        QualityInput.Value = options.Quality; LosslessInput.IsChecked = options.Lossless;
        ResizeInput.IsChecked = options.MaxDimension > 0; EdgeInput.Value = options.MaxDimension > 0 ? options.MaxDimension : 1920;
        PresetCombo.SelectedIndex = options == new ImageCompressionOptions() ? 0 :
            options == new ImageCompressionOptions { Quality = 65, MaxDimension = 1920 } ? 1 :
            options == new ImageCompressionOptions { Quality = 92 } ? 2 :
            options == new ImageCompressionOptions { Quality = 100, Lossless = true } ? 3 : 4;
    }
    private void PresetChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_initializing || PresetCombo.SelectedIndex == 4) return;
        _initializing = true;
        SetOptions(PresetCombo.SelectedIndex switch
        {
            1 => new() { Quality = 65, MaxDimension = 1920 },
            2 => new() { Quality = 92 },
            3 => new() { Quality = 100, Lossless = true },
            _ => new()
        });
        _initializing = false; ParametersChanged(PresetCombo, null!);
    }
    private void ParametersChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_initializing || _closed) return;
        if (!ReferenceEquals(sender, PresetCombo))
        { _initializing = true; PresetCombo.SelectedIndex = 4; _initializing = false; }
        _previewCancellation?.Cancel();
        foreach (var entry in _entries) { if (entry.Result is { } result) DeletePreview(result.Path); entry.SetResult(null); entry.ResultOptions = null; }
        Comparison.Result = null; _resultBitmap?.Dispose(); _resultBitmap = null;
        AfterText.Text = Localization.Text("压缩后 · 尚未预览");
        StatusText.Text = Localization.Text("正在准备预览…");
        StatusText.Classes.Set("compressionError", false);
        BatchSummary.Text = "";
        RefreshControls();
        SchedulePreview();
    }
    private void RefreshControls()
    {
        if (_initializing || _closed) return;
        var format = ((ComboBoxItem)FormatCombo.SelectedItem!).Tag!.ToString()!;
        LosslessInput.IsEnabled = format == "webp";
        var qualityEnabled = format != "png" && !(format == "webp" && LosslessInput.IsChecked == true);
        QualityInput.IsEnabled = QualitySlider.IsEnabled = qualityEnabled;
        EdgeInput.IsEnabled = ResizeInput.IsChecked == true;
        foreach (var entry in _entries) entry.CanInclude = !_busy && entry.Source is not null;
        var included = _entries.Count(entry => entry.Include);
        ListSummary.Text = Localization.Format($"{_entries.Count} 张图片 · 已勾选 {included} 张");
        SettingsPanel.IsEnabled = AddButton.IsEnabled = FolderButton.IsEnabled = SelectAllButton.IsEnabled = !_busy;
        RemoveButton.IsEnabled = !_busy && Active is not null;
        OutputInput.IsEnabled = BrowseButton.IsEnabled = !_busy && SourceFolderInput.IsChecked != true;
        SourceFolderInput.IsEnabled = !_busy;
        PreviewAllButton.IsEnabled = !_busy && included > 0;
        CancelPreviewButton.IsVisible = _busy;
        var valid = true;
        try { _ = ReadOptions(); }
        catch (ArgumentException ex) { valid = false; StatusText.Text = ex.Message; }
        ConfirmButton.IsEnabled = !_busy && included > 0 && valid && (SourceFolderInput.IsChecked == true || !string.IsNullOrWhiteSpace(OutputInput.Text));
    }
    private void FileSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (_initializing || _closed) return;
        DisposeImages(); Comparison.ResetView();
        BeforeText.Text = Localization.Text("压缩前"); AfterText.Text = Localization.Text("压缩后 · 尚未预览");
        StatusText.Text = Active is null ? Localization.Text("尚未添加图片") : Localization.Text("正在准备预览…");
        RefreshControls();
        if (_busy) _ = DisplayAsync(Active, _lifetime.Token); else SchedulePreview();
    }
    private void SchedulePreview()
    {
        _previewCancellation?.Cancel();
        var prior = _previewTask;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _previewCancellation = cancellation;
        _previewTask = PreviewAfterAsync(prior, Active, cancellation);
    }
    private async Task PreviewAfterAsync(Task prior, ImageCompressionEntry? entry, CancellationTokenSource cancellation)
    { await prior; try { await LivePreviewAsync(entry, cancellation.Token); } finally { if (!ReferenceEquals(cancellation, _previewCancellation)) cancellation.Dispose(); } }
    private async Task LivePreviewAsync(ImageCompressionEntry? entry, CancellationToken token)
    {
        if (entry is null) { DisposeImages(); return; }
        try
        {
            await Task.Delay(250, token);
            await entry.Ready;
            token.ThrowIfCancellationRequested();
            await DisplayAsync(entry, token);
            await EncodePreviewAsync(entry, ReadOptions(), token);
            await DisplayAsync(entry, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested && !_closed) { entry.Error = ex.Message; StatusText.Text = ex.Message; await DisplayAsync(entry, token); } }
    }
    private async Task EncodePreviewAsync(ImageCompressionEntry entry, ImageCompressionOptions options, CancellationToken token)
    {
        await entry.Ready; token.ThrowIfCancellationRequested();
        if (entry.Source is null) throw new InvalidDataException(entry.Error ?? "无法读取图片。");
        if (entry.Result is not null && entry.ResultOptions == options && File.Exists(entry.Result.Path)) return;
        entry.Encoding = true; entry.Error = null;
        try
        {
            if (entry.Result is { } old) DeletePreview(old.Path);
            entry.SetResult(null);
            Directory.CreateDirectory(_temporary);
            var output = Path.Combine(_temporary, Guid.NewGuid().ToString("N") + "." + options.Format);
            var result = await _compressor.CompressAsync(entry.Path, output, options, token, keepLargerPreview: true);
            token.ThrowIfCancellationRequested();
            entry.SetResult(result); entry.ResultOptions = options;
        }
        finally { entry.Encoding = false; }
    }
    private async Task DisplayAsync(ImageCompressionEntry? entry, CancellationToken token)
    {
        if (entry?.Source is null) { if (ReferenceEquals(entry, Active)) DisposeImages(); return; }
        try
        {
            var result = entry.Result;
            var existing = _sourceBitmap;
            var reuse = existing is not null && ReferenceEquals(entry, _displayedEntry);
            var decodedSource = !reuse && (entry.Source.HasOrientation || Path.GetExtension(entry.Path).ToLowerInvariant() is ".heic" or ".heif") ?
                await _preview.Thumbnail(entry.Path, 0, entry.Source.Width, entry.Source.Height, token, pad: false) : null;
            var images = await Task.Run(() =>
            {
                using var decoded = decodedSource is null ? null : new MemoryStream(decodedSource);
                var source = reuse ? null : decoded is null ? new Bitmap(entry.Path) : new Bitmap(decoded);
                try { return (Source: source, Result: result is null ? null : new Bitmap(result.Path)); }
                catch { source?.Dispose(); throw; }
            }, token);
            if (_closed || token.IsCancellationRequested || !ReferenceEquals(entry, Active) || !ReferenceEquals(result, entry.Result) ||
                reuse && !ReferenceEquals(existing, _sourceBitmap))
            { images.Source?.Dispose(); images.Result?.Dispose(); return; }
            if (images.Source is not null) { DisposeImages(); _sourceBitmap = images.Source; _displayedEntry = entry; }
            else { Comparison.Result = null; _resultBitmap?.Dispose(); }
            _resultBitmap = images.Result;
            Comparison.Source = _sourceBitmap; Comparison.Result = _resultBitmap;
            BeforeText.Text = Localization.Format($"压缩前 · {ImageCompression.Bytes(entry.Source.Bytes)}\n{entry.Source.Width} × {entry.Source.Height}");
            AfterText.Text = result is null ? Localization.Text("压缩后 · 尚未预览") : Localization.Format($"压缩后 · {ImageCompression.Bytes(result.OutputBytes)}\n{result.Width} × {result.Height}");
            StatusText.Text = result is null ? entry.Error ?? Localization.Text("正在准备预览…") : result.IsSmaller ?
                Localization.Format($"节省 {result.SavedPercent:0.##}%") : Localization.Text("当前设置未减小体积");
            StatusText.Classes.Set("compressionError", result is not null && !result.IsSmaller);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && !token.IsCancellationRequested) { StatusText.Text = ex.Message; StatusText.Classes.Set("compressionError", true); } }
    }
    private async Task<bool> PreviewAllAsync()
    {
        if (_busy) return false;
        _busy = true; RefreshControls();
        _batchCancellation?.Dispose();
        _batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _batchCancellation.Token;
        try
        {
            _previewCancellation?.Cancel(); await _previewTask;
            var options = ReadOptions();
            var batch = _entries.Where(entry => entry.Include).ToArray();
            for (var index = 0; index < batch.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                BatchSummary.Text = Localization.Format($"正在实际编码 {index + 1} / {batch.Length}");
                try { await EncodePreviewAsync(batch[index], options, token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { batch[index].Error = ex.Message; }
                if (ReferenceEquals(batch[index], Active)) await DisplayAsync(batch[index], token);
            }
            var smaller = batch.Where(entry => entry.Result?.IsSmaller == true && entry.Error is null).ToArray();
            var before = smaller.Sum(entry => entry.Result!.SourceBytes); var after = smaller.Sum(entry => entry.Result!.OutputBytes);
            BatchSummary.Text = Localization.Format($"{smaller.Length} 张可压小，{batch.Length - smaller.Length} 张未压小或失败。\n{ImageCompression.Bytes(before)} → {ImageCompression.Bytes(after)}");
            return true;
        }
        catch (OperationCanceledException) { if (!_closed) BatchSummary.Text = Localization.Text("已停止压缩预览"); return false; }
        catch (Exception ex) { if (!_closed) StatusText.Text = ex.Message; return false; }
        finally { _busy = false; RefreshControls(); }
    }
    private async void PreviewAllClick(object? sender, RoutedEventArgs args) { _batchTask = PreviewAllAsync(); await _batchTask; }
    private void CancelPreviewClick(object? sender, RoutedEventArgs args) => _batchCancellation?.Cancel();
    private async void ConfirmClick(object? sender, RoutedEventArgs args)
    {
        var task = PreviewAllAsync(); _batchTask = task;
        if (!await task || _closed) return;
        var inputs = _entries.Where(entry => entry.Include && entry.Result?.IsSmaller == true && entry.Error is null).Select(entry => entry.Path).ToArray();
        if (inputs.Length == 0) { StatusText.Text = Localization.Text("没有体积减小的图片"); return; }
        try { Close(new ImageCompressionRequest(inputs, ReadOptions(), OutputInput.Text?.Trim() ?? "", SourceFolderInput.IsChecked == true, StartInput.IsChecked == true)); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async void AddClick(object? sender, RoutedEventArgs args)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = Localization.Text("选择要压缩的图片"), AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(Localization.Text("静态图片")) { Patterns = ["*.heic", "*.heif", "*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp"] }] });
        if (!_closed) AddFiles(files.Select(file => file.TryGetLocalPath()).OfType<string>());
    }
    private async void AddFolderClick(object? sender, RoutedEventArgs args)
    {
        if (await Ui.Folder(this, "选择图片文件夹") is not { } folder) return;
        try { var files = await Task.Run(() => Directory.EnumerateFiles(folder).Where(ImageCompression.Supports).Order(StringComparer.CurrentCultureIgnoreCase).ToArray(), _lifetime.Token); if (!_closed) AddFiles(files); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void RemoveClick(object? sender, RoutedEventArgs args)
    { if (Active is not { } active) return; _previewCancellation?.Cancel(); _entries.Remove(active); if (_entries.Count > 0) FileList.SelectedIndex = 0; else DisposeImages(); RefreshControls(); }
    private void SelectAllClick(object? sender, RoutedEventArgs args) { foreach (var entry in _entries.Where(entry => entry.Source is not null)) entry.Include = true; RefreshControls(); }
    private async void BrowseClick(object? sender, RoutedEventArgs args) { if (await Ui.Folder(this, "选择压缩图片保存文件夹") is { } folder) OutputInput.Text = folder; }
    private void ZoomInClick(object? sender, RoutedEventArgs args) => Comparison.Zoom(1.25);
    private void ZoomOutClick(object? sender, RoutedEventArgs args) => Comparison.Zoom(.8);
    private void ResetViewClick(object? sender, RoutedEventArgs args) => Comparison.ResetView();
    private void ActualSizeClick(object? sender, RoutedEventArgs args) => Comparison.ActualSize();
    private void CancelClick(object? sender, RoutedEventArgs args) => Close(null);
    private static void DeletePreview(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* A decoder can still be reading; window cleanup removes the cache. */ }
    }
    private void DisposeImages() { Comparison.Source = Comparison.Result = null; _sourceBitmap?.Dispose(); _resultBitmap?.Dispose(); _sourceBitmap = _resultBitmap = null; _displayedEntry = null; }
    private async Task CleanupAsync()
    {
        _closed = true; _lifetime.Cancel(); _previewCancellation?.Cancel(); _batchCancellation?.Cancel(); DisposeImages();
        await Task.WhenAll(_previewTask, _batchTask, Task.WhenAll(_inspections));
        try { if (Directory.Exists(_temporary)) Directory.Delete(_temporary, true); }
        catch (IOException) { /* A late preview reader can briefly retain a temporary file. */ }
        _previewCancellation?.Dispose(); _batchCancellation?.Dispose(); _lifetime.Dispose(); _inspectionSlots.Dispose();
    }
}
