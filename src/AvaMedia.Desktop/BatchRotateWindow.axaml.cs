using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed class BatchRotateEntry(string path) : Observable
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
    public int? Rotation { get; internal set; } = 90;
    public VideoOrientationResult? Detection { get; internal set; }
    internal bool IsDetecting { get; set; }
    internal string? DetectionMessage { get; set; }
    internal OrientationDetectionProgress? DetectionProgress { get; set; }
    internal Task Ready { get; set; } = Task.CompletedTask;
    internal void Loaded(MediaInfo info) { Info = info; Raise(nameof(Dimensions)); }
}

public sealed partial class BatchRotateWindow : Window
{
    private readonly IMediaEngine _engine;
    private readonly ObservableCollection<BatchRotateEntry> _entries = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _loadSlots = new(2);
    private readonly RotateTransform _rotation = new();
    private readonly IVideoOrientationDetector _detector;
    private CancellationTokenSource? _detectionCancellation;
    private Task _detectionReady = Task.CompletedTask;
    private int _sharedRotation = 90;
    private bool _detecting;
    private bool _syncingDirection;
    private CancellationTokenSource? _previewCancellation;
    private Task _previewReady = Task.CompletedTask;
    private BatchRotateEntry? _active;
    private bool _updating;
    private bool _closed;
    public IReadOnlyList<BatchRotateEntry> Entries => _entries;
    public int Rotation => PerFile ? _active?.Rotation ?? 0 : _sharedRotation;
    public bool PerFile => ModeCombo.SelectedIndex == 1;
    public Task DetectionReady => _detectionReady;
    public string Format => FormatCombo.SelectedItem as string ?? "mp4";
    public Task Ready => WaitForReady();

    public BatchRotateWindow() : this(new MediaEngine(new()), new AppSettings().OutputFolder) { }
    public BatchRotateWindow(IMediaEngine engine, string outputFolder, IEnumerable<string>? files = null, IVideoOrientationDetector? detector = null)
    {
        InitializeComponent(); _engine = engine; _detector = detector ?? new VideoOrientationDetector(engine);
        FileList.ItemsSource = _entries; RotationTransform.LayoutTransform = _rotation;
        ModeCombo.ItemsSource = new[] { "统一旋转", "逐个调整" }; ModeCombo.SelectedIndex = 0;
        DirectionCombo.ItemsSource = new[] { BatchRotate.Direction(90), BatchRotate.Direction(270), BatchRotate.Direction(180), BatchRotate.Direction(0) }; DirectionCombo.SelectedIndex = 0;
        FormatCombo.ItemsSource = new[] { SourceVideoExport.Original, SourceVideoExport.FastRotation, "mp4", "mkv", "webm", "mov", "avi" }; FormatCombo.SelectedIndex = 0;
        OutputInput.Text = outputFolder;
        SourceOutputInput.IsChecked=engine.Settings.OutputToSource;SettingNameInput.IsChecked=engine.Settings.AddSettingName;
        SourceOutputInput.IsCheckedChanged+=(_,_)=>{OutputInput.IsEnabled=BrowseOutputButton.IsEnabled=SourceOutputInput.IsChecked!=true;RefreshValidation();};
        OutputInput.IsEnabled=BrowseOutputButton.IsEnabled=SourceOutputInput.IsChecked!=true;
        OutputInput.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) RefreshValidation(); };
        PreviewSeek.PropertyChanged += (_, args) =>
        {
            if (args.Property == Slider.ValueProperty && !_updating && _active?.Info is not null) _previewReady = LoadPreview(_active, true);
        };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, args) => args.DragEffects = _detecting ? DragDropEffects.None : DragDropEffects.Copy);
        AddHandler(DragDrop.DropEvent, (_, args) =>
        { if (!_detecting) ReportSkipped(AddFiles(args.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>() ?? [])); });
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _detectionCancellation?.Cancel(); _previewCancellation?.Cancel(); DisposePreview(); };
        if (files is not null) AddFiles(files);
        RefreshValidation();
    }

    private async Task WaitForReady()
    { await Task.WhenAll(_entries.Select(e => e.Ready)); await _previewReady; }

    public IReadOnlyList<string> AddFiles(IEnumerable<string> paths)
    {
        if (_detecting) throw new InvalidOperationException("请等待或停止方向检测后再添加视频。");
        var skipped = new List<string>();
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !QuickClipBatch.VideoExtensions.Contains(System.IO.Path.GetExtension(path).TrimStart('.')))
            { skipped.Add(path); continue; }
            var full = System.IO.Path.GetFullPath(path);
            if (_entries.Any(e => BatchVideoTools.PathComparer.Equals(e.Path, full))) continue;
            var entry = new BatchRotateEntry(full) { Rotation = PerFile ? null : _sharedRotation,
                DetectionMessage = PerFile ? "尚未检测，请自动检测或手动选择方向。" : null };
            entry.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(BatchRotateEntry.Include)) RefreshValidation(); };
            _entries.Add(entry); entry.Ready = LoadEntry(entry);
        }
        if (_active is null && _entries.Count > 0) FileList.SelectedIndex = 0;
        RefreshValidation(); return skipped;
    }

    private async Task LoadEntry(BatchRotateEntry entry)
    {
        var token = _lifetime.Token; var acquired = false;
        try
        {
            await _loadSlots.WaitAsync(token); acquired = true;
            var info = await _engine.Probe(entry.Path, token); _ = BatchRotate.OutputSize(info, 0);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_closed || !_entries.Contains(entry)) return;
                entry.Loaded(info);
                if (_active == entry) { PrepareSeek(entry); _previewReady = LoadPreview(entry); }
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
                if (_active == entry) { PreviewStatus.Text = Localization.Format($"无法读取视频：{ex.Message}"); PreviewStatus.IsVisible = true; }
                RefreshValidation();
            });
        }
        finally { if (acquired) _loadSlots.Release(); }
    }

    private void PrepareSeek(BatchRotateEntry entry)
    {
        _updating = true; PreviewSeek.Maximum = Math.Max(0, (entry.Info?.Duration ?? 0) - 0.04);
        PreviewSeek.Value = 0; PreviewSeek.IsEnabled = entry.Info is not null; _updating = false;
    }

    private async Task LoadPreview(BatchRotateEntry entry, bool debounce = false)
    {
        _previewCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _previewCancellation = cancellation; var token = cancellation.Token; var seconds = PreviewSeek.Value;
        PreviewStatus.Text = "正在读取预览…"; PreviewStatus.IsVisible = true;
        try
        {
            if (debounce) await Task.Delay(120, token);
            if (entry.Info is null) return;
            var bytes = await _engine.Thumbnail(entry.Path, seconds, 960, 540, token, pad: false, videoStreamIndex: entry.Info.VideoStreamIndex);
            token.ThrowIfCancellationRequested(); using var stream = new MemoryStream(bytes); var bitmap = new Bitmap(stream);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_closed || token.IsCancellationRequested || _active != entry) { bitmap.Dispose(); return; }
                DisposePreview(); SourceImage.Source = ResultImage.Source = bitmap;
                ResultImage.Width = bitmap.Size.Width; ResultImage.Height = bitmap.Size.Height;
                PreviewName.Text = $"{entry.Name} · {TimeSpan.FromSeconds(seconds):hh\\:mm\\:ss}";
                PreviewStatus.IsVisible = false; RefreshValidation();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed && !token.IsCancellationRequested && _active == entry)
            { PreviewStatus.Text = Localization.Format($"预览失败：{ex.Message}"); PreviewStatus.IsVisible = true; }
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
            cancellation.Dispose();
        }
    }

    private void DisposePreview()
    {
        var bitmap = SourceImage.Source as Bitmap;
        SourceImage.Source = ResultImage.Source = null; bitmap?.Dispose();
    }

    private void RefreshValidation()
    {
        if (ListSummary is null || _closed) return;
        var activeRotation = _active is null ? (int?)_sharedRotation : EffectiveRotation(_active);
        _syncingDirection = true;
        DirectionCombo.SelectedItem = activeRotation is { } chosen ? BatchRotate.Direction(chosen) : null;
        _syncingDirection = false;
        DirectionLabel.Text = PerFile ? "当前视频" : "旋转方向";
        _rotation.Angle = activeRotation ?? 0;
        ResultDescription.Text = activeRotation is { } angle ? Localization.Format($"旋转后 · {Localization.Key(BatchRotate.Direction(angle))}") : "旋转后 · 请手动指定方向";
        if (_active?.Info is { } active)
        { var size = BatchRotate.OutputSize(active, activeRotation ?? 0); ResultDescription.Text += $" · {size.Width} × {size.Height}"; }
        var included = _entries.Where(e => e.Include).ToArray(); var invalid = 0; var pending = 0; var unresolved = 0; var changed = 0; var skipped = 0;
        Localization.SetText(ListSummary,$"{_entries.Count} 个视频 · 勾选 {included.Length} 个");
        foreach (var entry in _entries)
        {
            if (!entry.Include) { entry.Status = "不处理"; continue; }
            if (entry.Error is not null) { entry.Status = Localization.Format($"读取失败：{entry.Error}"); invalid++; continue; }
            if (entry.Info is null) { entry.Status = "正在读取…"; pending++; continue; }
            if (entry.IsDetecting) { entry.Status = entry.DetectionProgress is { } progress
                ? Localization.Format($"正在检测 {progress.CompletedFrames}/{progress.TotalFrames} 帧…") : entry.DetectionMessage ?? "正在检测方向…"; continue; }
            var correction = EffectiveRotation(entry);
            if (correction is null) { entry.Status = Localization.Format($"无法确定 · {(entry.DetectionMessage ?? entry.Detection?.Reason ?? "请手动指定方向。")}"); unresolved++; continue; }
            try
            {
                var export = BatchRotate.ResolveOptions(entry.Info, correction.Value, Format, entry.Path); var size = BatchRotate.OutputSize(entry.Info, correction.Value);
                if (correction == 0) { entry.Status = "无需旋转 · 跳过"; skipped++; }
                else { entry.Status = Localization.Format($"{Localization.Key(BatchRotate.Direction(correction.Value))} → {size.Width} × {size.Height} · {export.Format.ToUpperInvariant()}"); changed++; }
                if (PerFile && entry.Detection is { IsCertain: true } result)
                    entry.Status = Localization.Join(" · ", new[] { entry.Status, Localization.Format($"自动判断（{Localization.Key(result.Reliability == OrientationReliability.High ? "高" : "中")}可靠度）"), Localization.OrientationReason(result) });
            }
            catch (ArgumentException ex) { entry.Status = Localization.Format($"不可旋转：{ex.Message}"); invalid++; }
        }
        var outputValid = !string.IsNullOrWhiteSpace(OutputInput.Text);
        ExportHint.Text = Format == SourceVideoExport.FastRotation ? SourceVideoExport.FastHint : Format == SourceVideoExport.Original ? SourceVideoExport.OriginalHint : "按所选格式重新编码视频和音频。";
        OkButton.IsEnabled = changed > 0 && invalid == 0 && unresolved == 0 && pending == 0 && outputValid && !_detecting;
        DetectButton.IsEnabled = included.Length > 0 && pending == 0 && invalid == 0 && !_detecting;
        CancelDetectionButton.IsVisible = _detecting;
        AddButton.IsEnabled = RemoveButton.IsEnabled = SelectAllButton.IsEnabled = ModeCombo.IsEnabled = !_detecting;
        DirectionCombo.IsEnabled = !_detecting && (!PerFile || _active?.Info is not null);
        ValidationText.Classes.Set("error", invalid > 0 || unresolved > 0);
        ValidationText.Text = included.Length == 0 ? "请添加并勾选视频" : pending > 0 ? Localization.Format($"正在读取 {pending} 个视频…")
            : _detecting ? "正在检测方向…"
            : invalid > 0 ? Localization.Format($"{invalid} 个视频无法处理，请移除或取消勾选。")
            : unresolved > 0 ? Localization.Format($"{unresolved} 个视频方向无法确定，请逐个指定方向或取消勾选。")
            : !outputValid ? "请选择输出目录" : changed == 0 ? "所选视频均无需旋转"
            : Localization.Format($"{changed} 个视频将按各自方向旋转，{skipped} 个跳过");
    }

    private int? EffectiveRotation(BatchRotateEntry entry) => PerFile ? entry.Rotation : _sharedRotation;

    public Task DetectDirectionsAsync()
    {
        if (_detecting) return _detectionReady;
        _detectionReady = DetectDirectionsCore();
        return _detectionReady;
    }

    private async Task DetectDirectionsCore()
    {
        var targets = _entries.Where(e => e.Include).ToArray();
        if (targets.Length == 0) return;
        _detecting = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _detectionCancellation = cancellation;
        RefreshValidation();
        try
        {
            await WaitForReady(); cancellation.Token.ThrowIfCancellationRequested();
            if (targets.Any(e => e.Error is not null || e.Info is null))
            { DetectionStatus.Text = "请先移除或取消勾选无法读取的视频。"; return; }
            foreach (var entry in targets) { entry.Rotation = null; entry.Detection = null; entry.DetectionMessage = "等待检测，请稍候。"; }
            ModeCombo.SelectedIndex = 1;
            for (var index = 0; index < targets.Length; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var entry = targets[index]; entry.IsDetecting = true;
                entry.DetectionMessage = "正在检测方向…";
                Localization.SetText(DetectionStatus,$"正在检测 {index + 1}/{targets.Length}：{entry.Name}");
                RefreshValidation();
                var progress = new Progress<OrientationDetectionProgress>(p =>
                {
                    if (_closed || !ReferenceEquals(_detectionCancellation, cancellation) || cancellation.IsCancellationRequested || !entry.IsDetecting) return;
                    entry.DetectionProgress = p; entry.DetectionMessage = $"正在检测 {p.CompletedFrames}/{p.TotalFrames} 帧…";
                    RefreshValidation();
                });
                try
                {
                    var result = await _detector.DetectAsync(entry.Path, entry.Info!, progress, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    entry.Detection = result; entry.Rotation = result.Rotation; entry.DetectionMessage = null;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception ex) { entry.DetectionMessage = $"检测失败：{ex.Message} 请手动选择方向。"; }
                finally { entry.IsDetecting = false; }
                RefreshValidation();
            }
            var certain = targets.Count(e => e.Rotation is not null);
            var unchanged = targets.Count(e => e.Rotation == 0);
            Localization.SetText(DetectionStatus,$"检测完成：{certain} 个已确定（{unchanged} 个无需旋转），{targets.Length - certain} 个需手动检查。请对比预览。");
        }
        catch (OperationCanceledException) { if (!_closed) DetectionStatus.Text = "检测已停止"; }
        finally
        {
            foreach (var entry in targets)
            {
                entry.IsDetecting = false;
                if (entry.Rotation is null && entry.Detection is null && (entry.DetectionMessage is null || entry.DetectionMessage.StartsWith("等待") || entry.DetectionMessage.StartsWith("正在")))
                    entry.DetectionMessage = "检测未完成，请手动选择方向。";
            }
            if (ReferenceEquals(_detectionCancellation, cancellation)) _detectionCancellation = null;
            _detecting = false; RefreshValidation();
        }
    }

    public void CancelDetection() => _detectionCancellation?.Cancel();

    public BatchRotateRequest CreateRequest()
    {
        if (_detecting) throw new ArgumentException("请等待检测完成或停止检测后再加入队列。");
        var included = _entries.Where(e => e.Include).ToArray();
        if (included.Length == 0) throw new ArgumentException("请先勾选视频。");
        if (string.IsNullOrWhiteSpace(OutputInput.Text)) throw new ArgumentException("请选择输出目录。");
        foreach (var entry in included)
        {
            if (entry.Error is not null) throw new ArgumentException(entry.Name + "：" + entry.Error);
            if (entry.Info is null) throw new ArgumentException("视频信息正在读取，请稍候。");
            var angle = EffectiveRotation(entry) ?? throw new ArgumentException(Localization.Format($"{entry.Name}：方向无法确定，请手动选择。"));
            var options = BatchRotate.ResolveOptions(entry.Info, angle, Format, entry.Path);
            MediaEngine.Validate(new() { FeatureId = "rotate", Inputs = [entry.Path], Options = options,
                Output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AvaMedia-rotate-" + Guid.NewGuid() + "." + options.Format) });
        }
        var inputs = included.Where(e => EffectiveRotation(e) != 0)
            .Select(e => new BatchRotateInput(e.Path, e.Info!, PerFile ? e.Rotation : null)).ToArray();
        if (inputs.Length == 0) throw new ArgumentException("勾选的视频均无需旋转。");
        return new(inputs, PerFile ? 0 : _sharedRotation, Format, System.IO.Path.GetFullPath(OutputInput.Text!),SourceOutputInput.IsChecked==true,SettingNameInput.IsChecked==true?Format.ToUpperInvariant():"");
    }

    private void ClearPreview()
    {
        _previewCancellation?.Cancel(); DisposePreview();
        PreviewStatus.Text = "添加视频后可对比旋转前后的方向"; PreviewStatus.IsVisible = true;
        PreviewName.Text = "选择一个视频预览"; PreviewSeek.IsEnabled = false;
    }
    private void FileSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        _active = args.AddedItems.OfType<BatchRotateEntry>().LastOrDefault() ?? FileList.SelectedItem as BatchRotateEntry; ClearPreview();
        if (_active is not null)
        {
            PreviewName.Text = _active.Name; PrepareSeek(_active);
            PreviewStatus.Text = _active.Error is { } error ? Localization.Format($"无法读取视频：{error}") : "正在读取预览…";
            if (_active.Info is not null) _previewReady = LoadPreview(_active);
        }
        RefreshValidation();
    }
    private async void AddClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = Localization.Text("选择多个视频"), AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(Localization.Text("视频")) { Patterns = QuickClipBatch.VideoExtensions.Select(e => "*." + e).ToArray() }, FilePickerFileTypes.All] });
        ReportSkipped(AddFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>()));
    }
    private void ReportSkipped(IReadOnlyList<string> skipped)
    { if (skipped.Count > 0) Localization.SetText(ValidationText,$"已跳过 {skipped.Count} 个非视频或不存在的文件。"); }
    private void RemoveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        foreach (var entry in FileList.SelectedItems?.Cast<BatchRotateEntry>().ToArray() ?? []) _entries.Remove(entry);
        if (_active is null || !_entries.Contains(_active))
        { FileList.SelectedIndex = _entries.Count > 0 ? 0 : -1; if (_entries.Count == 0) { _active = null; ClearPreview(); } }
        RefreshValidation();
    }
    private void SelectAllClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    { foreach (var entry in _entries) entry.Include = true; }
    private void DirectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_syncingDirection || DirectionCombo.SelectedItem is not string choice) return;
        var angle = choice switch { "无需旋转" => 0, "旋转 180°" => 180, "逆时针 90°" => 270, _ => 90 };
        if (PerFile)
        {
            if (_active is not null) { _active.Rotation = angle; _active.Detection = null; _active.DetectionMessage = null; }
        }
        else
        {
            _sharedRotation = angle;
            foreach (var entry in _entries) { entry.Rotation = angle; entry.Detection = null; entry.DetectionMessage = null; }
        }
        RefreshValidation();
    }
    private void ModeChanged(object? sender, SelectionChangedEventArgs args) => RefreshValidation();
    private async void DetectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => await DetectDirectionsAsync();
    private void CancelDetectionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => CancelDetection();
    private void FormatChanged(object? sender, SelectionChangedEventArgs args) => RefreshValidation();
    private async void BrowseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    { if (await Ui.Folder(this, "选择旋转输出目录") is { } folder) OutputInput.Text = folder; }
    private void CancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => Close(null);
    private void ConfirmClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        try { Close(CreateRequest()); }
        catch (Exception ex) { ValidationText.Text = ex.Message; ValidationText.Classes.Set("error", true); }
    }
}
