using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed class BatchCropEntry(string path) : Observable
{
    private bool _include = true;
    private string _status = "正在读取…";
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public bool Include { get => _include; set => Set(ref _include, value); }
    public string Status { get => _status; internal set => Set(ref _status, value); }
    public string Dimensions => Info is { } info ? $"{info.Width} × {info.Height} · {TimeSpan.FromSeconds(Math.Max(0, info.Duration)):hh\\:mm\\:ss}" : "";
    public MediaInfo? Info { get; internal set; }
    public string? Error { get; internal set; }
    internal Task Ready { get; set; } = Task.CompletedTask;
    internal void Loaded(MediaInfo info) { Info = info; Raise(nameof(Dimensions)); }
}

public sealed partial class BatchCropWindow : Window
{
    public const string PixelMode = "同一像素区域";
    public const string RelativeMode = "按画面比例";
    private readonly MediaEngine _engine;
    private readonly ObservableCollection<BatchCropEntry> _entries = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _loadSlots = new(2);
    private CancellationTokenSource? _previewCancellation;
    private Task _previewReady = Task.CompletedTask;
    private BatchCropEntry? _active;
    private BatchCropEntry? _reference;
    private ConversionOptions _options = new();
    private bool _updating;
    private bool _closed;
    public IReadOnlyList<BatchCropEntry> Entries => _entries;
    public BatchCropMode Mode => ModeCombo.SelectedItem as string == RelativeMode ? BatchCropMode.Relative : BatchCropMode.Pixels;
    public CropArea Area => new(Integer(CropXInput), Integer(CropYInput), Integer(CropWidthInput), Integer(CropHeightInput));
    public Task Ready => WaitForReady();

    public BatchCropWindow() : this(new MediaEngine(new()),new AppSettings().OutputFolder) { }
    public BatchCropWindow(MediaEngine engine, string outputFolder, IEnumerable<string>? files = null)
    {
        InitializeComponent(); _engine = engine;
        FileList.ItemsSource = _entries;
        ModeCombo.ItemsSource = new[] { PixelMode, RelativeMode }; ModeCombo.SelectedIndex = 0;
        FormatCombo.ItemsSource = new[] { "mp4", "mkv", "webm", "mov", "avi" }; FormatCombo.SelectedIndex = 0;
        OutputInput.Text = outputFolder;
        foreach (var input in new[] { CropXInput, CropYInput, CropWidthInput, CropHeightInput })
            input.PropertyChanged += (_, args) => { if (args.Property == NumericUpDown.ValueProperty && !_updating) RefreshValidation(); };
        PreviewSeek.PropertyChanged += (_, args) =>
        {
            if (args.Property == Slider.ValueProperty && !_updating && _active?.Info is not null)
                _previewReady = LoadPreview(_active, true);
        };
        OutputInput.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) RefreshValidation(); };
        OutputInput.TextChanged += (_, _) => RefreshValidation();
        CropLayer.Changed += rect =>
        {
            if (_active?.Info is null) return;
            _reference = _active;
            var x = BatchCrop.EvenFloor(rect.X); var y = BatchCrop.EvenFloor(rect.Y);
            SetArea(new(x, y, BatchCrop.EvenFloor(rect.Right) - x, BatchCrop.EvenFloor(rect.Bottom) - y));
        };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, args) => args.DragEffects = DragDropEffects.Copy);
        AddHandler(DragDrop.DropEvent, (_, args) =>
        {
            var skipped = AddFiles(args.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>() ?? []);
            if (skipped.Count > 0) ValidationText.Text = $"已跳过 {skipped.Count} 个非视频或不存在的文件。";
        });
        Closed += (_, _) =>
        {
            _closed = true; _lifetime.Cancel(); _previewCancellation?.Cancel();
            (PreviewImage.Source as Bitmap)?.Dispose(); PreviewImage.Source = null;
        };
        if (files is not null) AddFiles(files);
        RefreshValidation();
    }

    private async Task WaitForReady()
    {
        await Task.WhenAll(_entries.Select(e => e.Ready));
        await _previewReady;
    }

    public IReadOnlyList<string> AddFiles(IEnumerable<string> paths)
    {
        var skipped = new List<string>();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !QuickClipBatch.VideoExtensions.Contains(System.IO.Path.GetExtension(path).TrimStart('.')))
            { skipped.Add(path); continue; }
            var full = System.IO.Path.GetFullPath(path);
            if (_entries.Any(e => comparer.Equals(e.Path, full))) continue;
            var entry = new BatchCropEntry(full);
            entry.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(BatchCropEntry.Include)) RefreshValidation(); };
            _entries.Add(entry); entry.Ready = LoadEntry(entry);
        }
        if (_active is null && _entries.Count > 0) FileList.SelectedIndex = 0;
        RefreshValidation(); return skipped;
    }

    private async Task LoadEntry(BatchCropEntry entry)
    {
        var token = _lifetime.Token; var acquired = false;
        try
        {
            await _loadSlots.WaitAsync(token); acquired = true;
            var info = await _engine.Probe(entry.Path, token);
            if (!info.HasVideo || info.Width < 2 || info.Height < 2 || info.Duration <= 0)
                throw new InvalidDataException("文件不包含可裁剪的视频画面。");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_closed || !_entries.Contains(entry)) return;
                entry.Loaded(info);
                if (_active == entry)
                {
                    if (_reference is null) SetReferenceToFull(entry);
                    PrepareSeek(entry); _previewReady = LoadPreview(entry);
                }
                RefreshValidation();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_closed || !_entries.Contains(entry)) return;
                entry.Error = ex.Message;
                if (_active == entry) { PreviewStatus.Text = "无法读取视频：" + ex.Message; PreviewStatus.IsVisible = true; }
                RefreshValidation();
            });
        }
        finally { if (acquired) _loadSlots.Release(); }
    }

    private void PrepareSeek(BatchCropEntry entry)
    {
        _updating = true;
        PreviewSeek.Maximum = Math.Max(0, (entry.Info?.Duration ?? 0) - 0.04);
        PreviewSeek.Value = 0; PreviewSeek.IsEnabled = entry.Info is not null;
        _updating = false;
    }

    private async Task LoadPreview(BatchCropEntry entry, bool debounce = false)
    {
        _previewCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _previewCancellation = cancellation; var token = cancellation.Token;
        var seconds = PreviewSeek.Value;
        CropLayer.Enabled = false; PreviewStatus.Text = "正在读取预览…"; PreviewStatus.IsVisible = true;
        try
        {
            if (debounce) await Task.Delay(120, token);
            if (entry.Info is null) return;
            var bytes = await _engine.Thumbnail(entry.Path, seconds, 960, 540, token, pad: false);
            token.ThrowIfCancellationRequested();
            var bitmap = new Bitmap(new MemoryStream(bytes));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_closed || token.IsCancellationRequested || _active != entry) { bitmap.Dispose(); return; }
                (PreviewImage.Source as Bitmap)?.Dispose(); PreviewImage.Source = bitmap;
                PreviewName.Text = $"{entry.Name} · {entry.Info.Width} × {entry.Info.Height} · {TimeSpan.FromSeconds(seconds):hh\\:mm\\:ss}";
                PreviewStatus.IsVisible = false; RefreshValidation();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed && !token.IsCancellationRequested && _active == entry)
            { PreviewStatus.Text = "预览失败：" + ex.Message; PreviewStatus.IsVisible = true; CropLayer.Enabled = false; }
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
            cancellation.Dispose();
        }
    }

    public void SetArea(CropArea area)
    {
        _updating = true;
        CropXInput.Value = area.X; CropYInput.Value = area.Y;
        CropWidthInput.Value = area.Width; CropHeightInput.Value = area.Height;
        _updating = false; RefreshValidation();
    }

    private void SetReferenceToFull(BatchCropEntry entry)
    {
        if (entry.Info is null) return;
        _reference = entry;
        SetArea(new(0, 0, BatchCrop.EvenFloor(entry.Info.Width), BatchCrop.EvenFloor(entry.Info.Height)));
    }

    private void RefreshValidation()
    {
        if (ListSummary is null || _closed) return;
        var included = _entries.Where(e => e.Include).ToArray(); var invalid = 0; var pending = 0;
        ListSummary.Text = $"{_entries.Count} 个视频 · 勾选 {included.Length} 个";
        ReferenceText.Text = _reference?.Info is { } reference
            ? $"选区参考：{_reference.Name}（{reference.Width} × {reference.Height} 像素）" : "选区参考：请选择一个已读取的视频";
        foreach (var entry in _entries)
        {
            if (!entry.Include) { entry.Status = "不处理"; continue; }
            if (entry.Error is not null) { entry.Status = "读取失败：" + entry.Error; invalid++; continue; }
            if (entry.Info is null) { entry.Status = "正在读取…"; pending++; continue; }
            try
            {
                if (_reference?.Info is null) throw new ArgumentException("请选择已读取的视频作为选区参考。");
                var options = BatchCrop.ResolveOptions(Area, _reference.Info, entry.Info, Mode, _options);
                entry.Status = $"选区 {options.CropX},{options.CropY} · {options.CropWidth} × {options.CropHeight}";
            }
            catch (ArgumentException ex) { entry.Status = "不可裁剪：" + ex.Message; invalid++; }
        }
        var outputValid = !string.IsNullOrWhiteSpace(OutputInput.Text);
        OkButton.IsEnabled = included.Length > 0 && invalid == 0 && pending == 0 && outputValid;
        ValidationText.Foreground = invalid > 0 ? this.FindResource("UiDanger") as IBrush : this.FindResource("UiText") as IBrush;
        ValidationText.Text = included.Length == 0 ? "请添加并勾选视频" : pending > 0 ? $"正在读取 {pending} 个视频…"
            : invalid > 0 ? $"{invalid} 个视频无法使用此选区，请调整选区、切换比例模式或取消勾选。"
            : !outputValid ? "请选择输出目录" : $"共同选区将应用于 {included.Length} 个视频。源文件保持原样。";
        CropLayer.Enabled = false;
        if (_active?.Info is { } active && _reference?.Info is not null && PreviewImage.Source is not null && !PreviewStatus.IsVisible)
        {
            try
            {
                var resolved = BatchCrop.Resolve(Area, _reference.Info, active, Mode);
                CropLayer.SourceWidth = active.Width; CropLayer.SourceHeight = active.Height;
                CropLayer.Selection = new(resolved.X, resolved.Y, resolved.Width, resolved.Height);
                CropLayer.Enabled = true;
            }
            catch (ArgumentException)
            {
                // Keep the drawing surface usable so an invalid region can be redrawn.
                CropLayer.SourceWidth = active.Width; CropLayer.SourceHeight = active.Height;
                CropLayer.Selection = default; CropLayer.Enabled = true;
            }
        }
        CropLayer.InvalidateVisual();
    }

    public BatchCropRequest CreateRequest()
    {
        var included = _entries.Where(e => e.Include).ToArray();
        if (included.Length == 0) throw new ArgumentException("请先勾选视频。");
        if (_reference?.Info is null) throw new ArgumentException("请先选择一个视频并设置裁剪区域。");
        foreach (var entry in included)
        {
            if (entry.Error is not null) throw new ArgumentException(entry.Name + "：" + entry.Error);
            if (entry.Info is null) throw new ArgumentException("视频信息正在读取，请稍候。");
            var options = BatchCrop.ResolveOptions(Area, _reference.Info, entry.Info, Mode, _options);
            MediaEngine.Validate(new() { FeatureId = "crop", Inputs = [entry.Path], Options = options,
                Output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AvaMedia-crop-" + Guid.NewGuid() + "." + options.Format) });
        }
        if (string.IsNullOrWhiteSpace(OutputInput.Text)) throw new ArgumentException("请选择输出目录。");
        return new(included.Select(e => new BatchCropInput(e.Path, e.Info!)).ToArray(), Area, _reference.Info,
            Mode, _options.Clone(), System.IO.Path.GetFullPath(OutputInput.Text));
    }

    private static int Integer(NumericUpDown control)
    {
        if (control.Value is not { } value || value != decimal.Truncate(value)) throw new ArgumentException("裁剪区域请输入整数像素。");
        return (int)value;
    }

    private async void AddClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "选择多个视频", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("视频") { Patterns = QuickClipBatch.VideoExtensions.Select(e => "*." + e).ToArray() }, FilePickerFileTypes.All] });
        var skipped = AddFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>());
        if (skipped.Count > 0) ValidationText.Text = $"已跳过 {skipped.Count} 个非视频或不存在的文件。";
    }
    private void RemoveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        foreach (var entry in FileList.SelectedItems?.Cast<BatchCropEntry>().ToArray() ?? []) _entries.Remove(entry);
        if (_active is null || !_entries.Contains(_active))
        { FileList.SelectedIndex = _entries.Count > 0 ? 0 : -1; if (_entries.Count == 0) ClearPreview(); }
        RefreshValidation();
    }
    private void SelectAllClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    { foreach (var entry in _entries) entry.Include = true; }
    private void ClearPreview()
    {
        _previewCancellation?.Cancel(); _active = null; CropLayer.Enabled = false;
        (PreviewImage.Source as Bitmap)?.Dispose(); PreviewImage.Source = null;
        PreviewStatus.Text = "添加视频后可预览并拖动选区"; PreviewStatus.IsVisible = true;
        PreviewName.Text = "选择一个视频预览"; PreviewSeek.IsEnabled = false; CropLayer.InvalidateVisual();
    }
    private void FileSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        _active = args.AddedItems.OfType<BatchCropEntry>().LastOrDefault() ?? FileList.SelectedItem as BatchCropEntry;
        if (_active is null) { ClearPreview(); return; }
        _previewCancellation?.Cancel(); (PreviewImage.Source as Bitmap)?.Dispose(); PreviewImage.Source = null;
        CropLayer.Enabled = false; PreviewStatus.Text = _active.Error is { } error ? "无法读取视频：" + error : "正在读取预览…"; PreviewStatus.IsVisible = true;
        PreviewName.Text = _active.Name; PrepareSeek(_active);
        if (_active.Info is not null)
        {
            if (_reference is null) SetReferenceToFull(_active);
            _previewReady = LoadPreview(_active);
        }
        RefreshValidation();
    }
    private void ResetClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    { if (_active is not null) SetReferenceToFull(_active); }
    private void ModeChanged(object? sender, SelectionChangedEventArgs args) => RefreshValidation();
    private void FormatChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (FormatCombo?.SelectedItem is not string format) return;
        _options.Format = format; _options.VideoCodec = _options.AudioCodec = "自动"; RefreshValidation();
    }
    private async void BrowseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    { if (await Ui.Folder(this, "选择裁剪输出目录") is { } folder) OutputInput.Text = folder; }
    private async void OptionsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        var result = await new OptionsWindow(_options, copyStreamsMode: false).ShowDialog<ConversionOptions?>(this);
        if (result is not null) { _options = result; RefreshValidation(); }
    }
    private void CancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => Close(null);
    private void ConfirmClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        try { Close(CreateRequest()); }
        catch (Exception ex) { ValidationText.Text = ex.Message; ValidationText.Foreground = this.FindResource("UiDanger") as IBrush; }
    }
}
