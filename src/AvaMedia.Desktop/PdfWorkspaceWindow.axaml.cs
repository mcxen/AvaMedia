using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;
using SkiaSharp;

namespace AvaMedia.Desktop;

public sealed record PdfWorkspaceRequest(string[] Files, string OutputFolder, ConversionOptions Options);

internal sealed class PdfWorkspacePage(string path, string renderPath, int inputIndex, PdfPageInfo info)
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Path { get; } = path;
    public string RenderPath { get; } = renderPath;
    public int InputIndex { get; } = inputIndex;
    public PdfPageInfo Info { get; } = info;
    public bool Included { get; set; } = true;
    public int Rotation { get; set; }
    public bool BreakAfter { get; set; }
    public PdfPageSelection Selection => new(InputIndex, Info.Number, Rotation, BreakAfter);
}

public partial class PdfWorkspaceWindow : Window
{
    private const int BoardSize = 24;
    private readonly Feature _feature;
    private readonly IMediaEngine _engine;
    private readonly List<string> _files = [];
    private readonly List<PdfWorkspacePage> _pages = [];
    private readonly List<string> _temporary = [];
    private readonly List<Bitmap> _thumbnails = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Stack<(Guid Id, bool Included, int Rotation, bool Cut)[]> _undo = new();
    private CancellationTokenSource? _boardWork;
    private CancellationTokenSource? _previewWork;
    private PdfWorkspacePage? _active;
    private Bitmap? _preview;
    private int _boardStart;
    private bool _loading;
    private bool _ready;
    private bool _closed;
    private bool _applyingPreset;
    private bool _textLayoutPending;
    private CancellationTokenSource? _textLayoutWork;
    private readonly HashSet<Task> _work = [];
    private bool IsCompress => _feature.Operation == Operation.PdfCompress;
    private bool IsExtraction => _feature.Operation is Operation.PdfText or Operation.PdfDocx or Operation.PdfXlsx;
    private bool IsSingleInput => IsCompress || _feature.Operation is Operation.PdfSplit or Operation.TextPdf;
    private bool CanArrange => !IsCompress;

    public PdfWorkspaceWindow() : this(Catalog.Find("pdf-merge"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)) { }

    public PdfWorkspaceWindow(Feature feature, string outputFolder, IEnumerable<string>? initial = null, ConversionOptions? initialOptions = null, IMediaEngine? engine = null)
    {
        _feature = feature; _engine = engine ?? new MediaEngine(new()); InitializeComponent();
        Title = WorkspaceTitle.Text = feature.Label; OutputFolder.Text = outputFolder;
        WindowArtwork.SetKind(this, feature.Icon);
        LayoutPanel.IsVisible = feature.Operation is Operation.TextPdf or Operation.ImagesPdf;
        SourceSizeItem.IsVisible = feature.Operation == Operation.ImagesPdf;
        FontSizeRow.IsVisible = feature.Operation == Operation.TextPdf;
        PaperSizeSetting.SelectedIndex = 0;
        SplitPanel.IsVisible = feature.Operation == Operation.PdfSplit;
        AgePanel.IsVisible = feature.Operation == Operation.PdfAge;
        CompressPanel.IsVisible = IsCompress;
        RasterSettings.IsVisible = IsCompress || AgePanel.IsVisible;
        RasterNote.IsVisible = AgePanel.IsVisible;
        ExtractionPanel.IsVisible = IsExtraction;
        SelectAllButton.IsVisible = InvertButton.IsVisible = UndoButton.IsVisible = CanArrange;
        SplitMode.SelectedIndex = 0; PaperStyle.SelectedIndex = 0; CompressionPreset.SelectedIndex = 1;
        if (initialOptions?.Pdf is {} saved)
        {
            PaperSizeSetting.SelectedIndex = saved.PageSize switch { "source" => 2, "letter" => 1, _ => 0 };
            LandscapeSetting.IsChecked = saved.Landscape; PageMargin.Value = saved.Margin; TextFontSize.Value = saved.FontSize;
            SplitMode.SelectedIndex = saved.ExtractAsOne ? 2 : saved.SplitEveryPage ? 1 : 0;
            PaperStyle.SelectedIndex = saved.Paper switch { "gray" => 1, "sepia" => 2, _ => 0 };
            AgeAmount.Value = saved.Age; GrainAmount.Value = saved.Grain; SkewAmount.Value = (decimal)saved.Skew;
            JpegQuality.Value = saved.Quality; RasterEdge.Value = saved.LongestEdge; RasterizeSetting.IsChecked = saved.Rasterize;
        }
        if (IsCompress) CompressionPreset.SelectedIndex = (JpegQuality.Value, RasterEdge.Value) switch { (90m,2400m) => 0, (75m,1800m) => 1, (50m,1200m) => 2, _ => 3 };
        _ready = true;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => { if (e.DataTransfer.TryGetFiles() is not null) { e.DragEffects = _loading ? DragDropEffects.None : DragDropEffects.Copy; e.Handled = true; } }, RoutingStrategies.Bubble);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (e.DataTransfer.TryGetFiles() is not {} files) return;
            e.Handled = true; await LoadFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>());
        }, RoutingStrategies.Bubble);
        Opened += async (_, _) =>
        {
            await LoadFiles(initial ?? []);
            if (initialOptions?.Pdf is {} draft && !_closed)
            {
                if (IsCompress) return;
                var ordered = new List<PdfWorkspacePage>();
                foreach (var selection in draft.Pages)
                {
                    var initialPaths = initial?.ToArray() ?? [];
                    var sourcePath = initialPaths.ElementAtOrDefault(selection.InputIndex);
                    var page = _pages.FirstOrDefault(p => p.Path == sourcePath && p.Info.Number == selection.PageNumber);
                    if (page is null) continue;
                    page.Rotation = selection.Rotation; page.BreakAfter = selection.BreakAfter; ordered.Add(page);
                }
                foreach (var page in _pages.Except(ordered)) { page.Included = false; ordered.Add(page); }
                _pages.Clear(); _pages.AddRange(ordered); RebuildBoard();
            }
        };
        Closed += (_, _) =>
        {
            _closed = true; _lifetime.Cancel(); _boardWork?.Cancel(); _previewWork?.Cancel(); _textLayoutWork?.Cancel();
            ClearBitmaps(); _preview?.Dispose(); _preview = null;
            // Background loaders may still finish: they own cleanup of their uncommitted temporary files.
            var temporary = _temporary.ToArray();
            _ = CleanupAsync(_work.ToArray(), temporary);
        };
        RebuildBoard();
    }

    private async Task LoadFiles(IEnumerable<string> paths)
    {
        if (_loading || _closed) return;
        var candidates = paths.Select(Path.GetFullPath).Distinct(VideoFolderScanner.PathComparer).ToArray();
        if (candidates.Length == 0) return;
        if (IsSingleInput && (candidates.Length != 1 || _files.Count > 0)) { StatusText.Text = Localization.Text("此工具一次处理一个文件，请先清空。"); return; }
        _loading = true; AddButton.IsEnabled = ClearButton.IsEnabled = ConfirmButton.IsEnabled = false;
        _boardWork?.Cancel();
        var errors = new List<string>();
        try
        {
            foreach (var path in candidates)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                if (_files.Contains(path, VideoFolderScanner.PathComparer)) continue;
                StatusText.Text = Localization.Text("正在读取页面…");
                string? temporary = null;
                try
                {
                    var extension = Path.GetExtension(path).ToLowerInvariant();
                    if (!File.Exists(path) || !Accepts(extension)) throw new ArgumentException(Localization.Text("文件类型不适用于此工具。"));
                    var renderPath = path;
                    IReadOnlyList<PdfPageInfo> info;
                    if (_feature.Operation == Operation.TextPdf)
                    {
                        temporary = Path.Combine(Path.GetTempPath(), "AvaMedia-preview-" + Guid.NewGuid() + ".pdf");
                        var output = temporary;
                        var layout = Options();
                        await Task.Run(() => DocumentEngine.CreateTextPdf(path, output, layout, _lifetime.Token));
                        renderPath = temporary;
                    }
                    if (_feature.Operation == Operation.ImagesPdf)
                    {
                        if(extension is ".jpg" or ".jpeg" or ".png") info=[await Task.Run(()=>PdfTools.ReadImagePage(path),_lifetime.Token)];
                        else
                        {
                            var metadata = await _engine.Probe(path, _lifetime.Token);
                            if (metadata.Width <= 0 || metadata.Height <= 0) throw new InvalidDataException("无法读取图片。");
                            info = [new(1, metadata.Width, metadata.Height)];
                        }
                    }
                    else info = await Task.Run(() => PdfRasterizer.ReadPages(renderPath, _lifetime.Token));
                    _lifetime.Token.ThrowIfCancellationRequested();
                    var inputIndex = _files.Count; _files.Add(path);
                    _pages.AddRange(info.Select(page => new PdfWorkspacePage(path, renderPath, inputIndex, page)));
                    if (temporary is not null) { _temporary.Add(temporary); temporary = null; }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add(Path.GetFileName(path) + ": " + ex.Message); }
                finally { if (temporary is not null) TryDelete(temporary); }
            }
            if (_pages.Count > 0) _active ??= _pages[0];
            _undo.Clear(); _boardStart = Math.Min(_boardStart, Math.Max(0, (_pages.Count - 1) / BoardSize * BoardSize));
        }
        catch (OperationCanceledException) { }
        finally
        {
            _loading = false;
            if (!_closed)
            {
                AddButton.IsEnabled = ClearButton.IsEnabled = true; RebuildBoard();
                StatusText.Text = errors.Count > 0 ? string.Join("\n", errors) : "";
                UpdatePreview();
            }
        }
    }

    private bool Accepts(string extension) => _feature.Operation switch
    {
        Operation.TextPdf => extension == ".txt",
        Operation.ImagesPdf => extension is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".tiff" or ".tif" or ".gif" or ".heic" or ".heif" or ".avif",
        _ => extension == ".pdf"
    };

    private PdfToolOptions Options() => new()
    {
        Pages = _pages.Where(p => IsCompress || p.Included).Select(p => p.Selection).ToList(),
        SplitEveryPage = SplitMode.SelectedIndex == 1, ExtractAsOne = SplitMode.SelectedIndex == 2,
        Age = (int)(AgeAmount.Value ?? 35), Grain = (int)(GrainAmount.Value ?? 15), Skew = (double)(SkewAmount.Value ?? .2m),
        Paper = PaperStyle.SelectedIndex switch { 1 => "gray", 2 => "sepia", _ => "warm" },
        PageSize = PaperSizeSetting.SelectedIndex switch { 1 => "letter", 2 => "source", _ => "a4" }, Landscape = LandscapeSetting.IsChecked == true, Margin = (int)(PageMargin.Value ?? 20), FontSize = (int)(TextFontSize.Value ?? 12),
        Quality = (int)(JpegQuality.Value ?? 75), LongestEdge = (int)(RasterEdge.Value ?? 1800), Rasterize = RasterizeSetting.IsChecked == true
    };

    private void Remember()
    {
        _undo.Push(_pages.Select(p => (p.Id, p.Included, p.Rotation, p.BreakAfter)).ToArray());
        if (_undo.Count > 40)
        {
            var recent = _undo.Take(40).Reverse().ToArray(); _undo.Clear(); foreach (var snapshot in recent) _undo.Push(snapshot);
        }
        UndoButton.IsEnabled = true;
    }

    private void RebuildBoard()
    {
        if (_closed) return;
        _boardWork?.Cancel(); _boardWork = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _boardWork.Token;
        PageBoard.Children.Clear(); ClearBitmaps();
        EmptyText.IsVisible = _pages.Count == 0;
        var visible = _pages.Skip(_boardStart).Take(BoardSize).ToArray();
        var renderTargets = new List<(PdfWorkspacePage Page, Image Image)>();
        foreach (var page in visible)
        {
            var card = new Border { Width = 154, Margin = new(0, 0, 10, 10), Padding = new(8), Classes = { "pdf-card" } };
            if (page == _active) card.Classes.Add("active");
            if (page.BreakAfter) card.Classes.Add("cut");
            var content = new StackPanel { Spacing = 6 }; card.Child = content;
            var select = new CheckBox { Content = Localization.Format($"第 {page.Info.Number} 页"), IsChecked = page.Included, IsEnabled = CanArrange };
            select.IsCheckedChanged += (_, _) => { if (page.Included == (select.IsChecked == true)) return; Remember(); page.Included = select.IsChecked == true; RefreshSummary(); };
            content.Children.Add(select);
            var image = new Image { Height = 156, Stretch = Stretch.Uniform };
            var previewButton = new Button { Content = image, Padding = new(2), HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brushes.White };
            previewButton.Click += (_, _) => { _active = page; HighlightActive(); UpdatePreview(); };
            content.Children.Add(previewButton); renderTargets.Add((page, image));
            var name = new TextBlock { Text = Path.GetFileName(page.Path), TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "caption" } };
            Localization.SetIsUserText(name, true); ToolTip.SetTip(name, page.Path); content.Children.Add(name);
            if (CanArrange)
            {
                var actions = new Grid { ColumnDefinitions = new(IsExtraction ? "*,*" : "*,*,*"), ColumnSpacing = 4 };
                var left = Ui.Button("←", () => Move(page, -1)); var right = Ui.Button("→", () => Move(page, 1));
                left.Padding = right.Padding = new(2);
                var rotate = Ui.Button("↻", () => { Remember(); page.Rotation = (page.Rotation + 90) % 360; RebuildBoard(); UpdatePreview(); });
                rotate.Padding = new(2);
                left.IsEnabled = _pages.IndexOf(page) > 0; right.IsEnabled = _pages.IndexOf(page) < _pages.Count - 1;
                ToolTip.SetTip(left, Localization.Text("前移")); ToolTip.SetTip(right, Localization.Text("后移")); ToolTip.SetTip(rotate, Localization.Text("旋转页面"));
                Grid.SetColumn(right, 1); Grid.SetColumn(rotate, 2); actions.Children.Add(left); actions.Children.Add(right); if(!IsExtraction) actions.Children.Add(rotate); content.Children.Add(actions);
                if (_feature.Operation == Operation.PdfSplit)
                {
                    var cut = new CheckBox { Content = Localization.Text("在此拆分"), IsChecked = page.BreakAfter, IsEnabled = SplitMode.SelectedIndex == 0 };
                    cut.IsCheckedChanged += (_, _) => { Remember(); page.BreakAfter = cut.IsChecked == true; card.Classes.Set("cut", page.BreakAfter); RefreshSummary(); };
                    content.Children.Add(cut);
                }
                AddReordering(card, previewButton, page);
            }
            card.Tag = page; PageBoard.Children.Add(card);
        }
        BoardRange.Text = _pages.Count == 0 ? "" : Localization.Format($"{_boardStart + 1}–{_boardStart + visible.Length} / {_pages.Count} 页");
        PreviousButton.IsEnabled = _boardStart > 0; NextButton.IsEnabled = _boardStart + BoardSize < _pages.Count;
        RefreshSummary(); Track(RenderCards(renderTargets, token));
    }

    private async Task RenderCards(List<(PdfWorkspacePage Page, Image Image)> cards, CancellationToken token)
    {
        foreach (var (page, image) in cards)
        {
            Bitmap? bitmap = null;
            try
            {
                token.ThrowIfCancellationRequested(); var rotation = page.Rotation;
                var bytes = await RenderPage(page, 240, rotation, token);
                token.ThrowIfCancellationRequested(); using var stream = new MemoryStream(bytes); bitmap = new Bitmap(stream);
                image.Source = bitmap; _thumbnails.Add(bitmap); bitmap = null;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { if (!token.IsCancellationRequested && !_closed) StatusText.Text = ex.Message; }
            finally { bitmap?.Dispose(); }
        }
    }

    private async Task<byte[]> RenderPage(PdfWorkspacePage page, int edge, int rotation, CancellationToken token, PdfToolOptions? effect = null)
    {
        if (_feature.Operation == Operation.ImagesPdf)
        {
            var layout = effect ?? Options();
            var bytes = await _engine.Thumbnail(page.Path, 0, edge, edge, token, pad: false);
            return await Task.Run(() =>
            {
                using var source = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("无法读取图片。");
                var sideways = rotation is 90 or 270;
                var size = PdfTools.PaperSize(sideways ? page.Info.Height : page.Info.Width, sideways ? page.Info.Width : page.Info.Height, layout);
                var dpi = edge / Math.Max(size.Width,size.Height);
                using var rotated = new SKBitmap(Math.Max(1,(int)(size.Width*dpi)),Math.Max(1,(int)(size.Height*dpi)));
                using (var canvas = new SKCanvas(rotated))
                {
                    canvas.Clear(SKColors.White); canvas.Translate(rotated.Width / 2f, rotated.Height / 2f); canvas.RotateDegrees(rotation);
                    var scale=Math.Min((size.Width-layout.Margin*2)*dpi/(sideways?page.Info.Height:page.Info.Width),(size.Height-layout.Margin*2)*dpi/(sideways?page.Info.Width:page.Info.Height));
                    canvas.DrawBitmap(source,new SKRect((float)(-page.Info.Width*scale/2),(float)(-page.Info.Height*scale/2),(float)(page.Info.Width*scale/2),(float)(page.Info.Height*scale/2)));
                }
                using var image = SKImage.FromBitmap(rotated); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
            }, token);
        }
        return await Task.Run(() =>
        {
            using var rendered = PdfRasterizer.Render(page.RenderPath, page.Info.Number, edge, rotation, token);
            using var processed = effect is not null && _feature.Operation == Operation.PdfAge ? PdfTools.Aged(rendered, effect, page.Info.Number, token) : rendered.Copy();
            using var image = SKImage.FromBitmap(processed);
            using var data = image.Encode(effect is not null && (_feature.Operation == Operation.PdfAge || effect.Rasterize) ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, effect?.Quality ?? 100);
            return data.ToArray();
        }, token);
    }

    private void UpdatePreview() => Track(UpdatePreviewAsync());

    private async Task UpdatePreviewAsync()
    {
        if (!_ready || _active is not {} page || _closed) return;
        _previewWork?.Cancel(); _previewWork = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _previewWork.Token; var options = Options();
        try
        {
            await Task.Delay(180, token);
            var edge = _feature.Operation == Operation.PdfAge || IsCompress && options.Rasterize ? options.LongestEdge : 900;
            var bytes = await RenderPage(page, edge, page.Rotation, token, options);
            token.ThrowIfCancellationRequested(); using var stream = new MemoryStream(bytes); var bitmap = new Bitmap(stream);
            PagePreview.Source = bitmap; _preview?.Dispose(); _preview = bitmap;
            var width=page.Info.Width;var height=page.Info.Height;
            if(page.Rotation is 90 or 270)(width,height)=(height,width);
            if(_feature.Operation==Operation.ImagesPdf)(width,height)=PdfTools.PaperSize(width,height,options);
            PreviewTitle.Text = IsCompress && !options.Rasterize ? Localization.Format($"原页预览 · 第 {page.Info.Number} 页") : Localization.Format($"第 {page.Info.Number} 页 · {width:0} × {height:0} pt");
            if (IsExtraction)
            {
                var text = await Task.Run(() =>
                {
                    using var pdf = UglyToad.PdfPig.PdfDocument.Open(page.RenderPath);
                    return UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(pdf.GetPage(page.Info.Number));
                }, token);
                token.ThrowIfCancellationRequested(); ExtractedText.Text = text; Localization.SetIsUserText(ExtractedText, true);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested && !_closed) StatusText.Text = ex.Message; }
    }

    private void AddReordering(Border card, Button handle, PdfWorkspacePage page)
    {
        Point? pressed = null;
        handle.PointerPressed += (_, e) => { if (e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) pressed = e.GetPosition(handle); };
        handle.PointerReleased += (_, _) => pressed = null;
        handle.PointerMoved += async (_, e) =>
        {
            if (pressed is not {} start || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed || Math.Abs(e.GetPosition(handle).X - start.X) + Math.Abs(e.GetPosition(handle).Y - start.Y) < 6) return;
            pressed = null; var transfer = new DataTransfer(); transfer.Add(DataTransferItem.CreateText(page.Id.ToString()));
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
        };
        DragDrop.SetAllowDrop(card, true);
        card.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (!Guid.TryParse(e.DataTransfer.TryGetText(), out var id) || !_pages.Any(p => p.Id == id)) return;
            e.DragEffects = DragDropEffects.Move; e.Handled = true;
        });
        card.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (_loading || !Guid.TryParse(e.DataTransfer.TryGetText(), out var id) || _pages.Find(p => p.Id == id) is not {} dragged) return;
            e.Handled = true; var target = _pages.IndexOf(page);
            if (dragged == page) return;
            Remember(); _pages.Remove(dragged); _pages.Insert(target, dragged); RebuildBoard();
        });
    }

    private void Move(PdfWorkspacePage page, int delta)
    {
        var index = _pages.IndexOf(page); var target = index + delta;
        if (target < 0 || target >= _pages.Count) return;
        Remember(); _pages.RemoveAt(index); _pages.Insert(target, page); _boardStart = target / BoardSize * BoardSize; RebuildBoard();
    }
    private void HighlightActive()
    { foreach (var card in PageBoard.Children.OfType<Border>()) card.Classes.Set("active", card.Tag == _active); }
    private void RefreshSummary()
    {
        var selected = _pages.Count(p => p.Included);
        PageSummary.Text = Localization.Format($"已选 {selected} / {_pages.Count} 页");
        if (SplitPanel.IsVisible) GroupSummary.Text = Localization.Format($"将生成 {PdfTools.Groups(Options()).Count} 个 PDF");
        ConfirmButton.IsEnabled = !_loading && !_textLayoutPending && selected > 0;
    }
    private void ClearBitmaps() { foreach (var bitmap in _thumbnails) bitmap.Dispose(); _thumbnails.Clear(); }
    private async void Track(Task task)
    {
        _work.Add(task);
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) StatusText.Text = ex.Message; }
        finally { _work.Remove(task); }
    }
    private static async Task CleanupAsync(Task[] work, string[] paths)
    {
        try { await Task.WhenAll(work); } catch (Exception) { }
        foreach (var path in paths) TryDelete(path);
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private async void AddClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Localization.Text("添加文件"), AllowMultiple = !IsSingleInput,
            FileTypeFilter = [new FilePickerFileType(_feature.Operation == Operation.TextPdf ? "TXT" : _feature.Operation == Operation.ImagesPdf ? Localization.Text("图片") : "PDF")
            { Patterns = _feature.Operation == Operation.TextPdf ? ["*.txt"] : _feature.Operation == Operation.ImagesPdf ? ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp", "*.tif", "*.tiff", "*.gif", "*.heic", "*.heif", "*.avif"] : ["*.pdf"] }]
        });
        await LoadFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }
    private void ClearClick(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _boardWork?.Cancel(); _previewWork?.Cancel(); _pages.Clear(); _files.Clear(); _undo.Clear(); _active = null; _boardStart = 0;
        UndoButton.IsEnabled = false; PagePreview.Source = null; _preview?.Dispose(); _preview = null; ExtractedText.Text = "";
        _textLayoutWork?.Cancel(); _ = CleanupAsync(_work.ToArray(), _temporary.ToArray()); _temporary.Clear(); RebuildBoard(); StatusText.Text = "";
    }
    private void SelectAllClick(object? sender, RoutedEventArgs e) { Remember(); foreach (var page in _pages) page.Included = true; RebuildBoard(); }
    private void InvertClick(object? sender, RoutedEventArgs e) { Remember(); foreach (var page in _pages) page.Included = !page.Included; RebuildBoard(); }
    private void UndoClick(object? sender, RoutedEventArgs e)
    {
        if (!_undo.TryPop(out var snapshot)) return;
        var lookup = _pages.ToDictionary(p => p.Id); _pages.Clear();
        foreach (var saved in snapshot) if (lookup.TryGetValue(saved.Id, out var page)) { page.Included = saved.Included; page.Rotation = saved.Rotation; page.BreakAfter = saved.Cut; _pages.Add(page); }
        UndoButton.IsEnabled = _undo.Count > 0; RebuildBoard(); UpdatePreview();
    }
    private void PreviousClick(object? sender, RoutedEventArgs e) { _boardStart = Math.Max(0, _boardStart - BoardSize); RebuildBoard(); }
    private void NextClick(object? sender, RoutedEventArgs e) { if (_boardStart + BoardSize < _pages.Count) _boardStart += BoardSize; RebuildBoard(); }
    private void SplitModeChanged(object? sender, SelectionChangedEventArgs e) { if (_ready) RebuildBoard(); }
    private void GroupClick(object? sender, RoutedEventArgs e)
    {
        Remember(); var size = (int)(GroupSize.Value ?? 5); var count = 0;
        foreach (var page in _pages) page.BreakAfter = page.Included && ++count % size == 0;
        SplitMode.SelectedIndex = 0; RebuildBoard();
    }
    private void EffectChanged(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (IsCompress && !_applyingPreset && (sender == JpegQuality || sender == RasterEdge)) CompressionPreset.SelectedIndex = 3;
        UpdatePreview();
    }
    private void CompressionPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || CompressionPreset.SelectedIndex == 3) return;
        _applyingPreset = true;
        (JpegQuality.Value, RasterEdge.Value) = CompressionPreset.SelectedIndex switch { 0 => (90m, 2400m), 2 => (50m, 1200m), _ => (75m, 1800m) }; _applyingPreset = false; UpdatePreview();
    }
    private void LayoutChanged(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        LandscapeSetting.IsEnabled = PaperSizeSetting.SelectedIndex != 2;
        if (_feature.Operation == Operation.ImagesPdf) { RebuildBoard(); UpdatePreview(); }
        else Track(UpdateTextLayoutAsync());
    }
    private async Task UpdateTextLayoutAsync()
    {
        _textLayoutWork?.Cancel();
        if (_files.Count == 0 || _loading || _closed) return;
        var work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _textLayoutWork = work;
        var token = work.Token; _textLayoutPending = true; RefreshSummary(); string? temporary = null;
        try
        {
            await Task.Delay(250,token); var layout = Options(); var path = _files[0];
            temporary = Path.Combine(Path.GetTempPath(), "AvaMedia-layout-" + Guid.NewGuid() + ".pdf"); var output = temporary;
            await Task.Run(() => DocumentEngine.CreateTextPdf(path,output,layout,token),token);
            var info = await Task.Run(() => PdfRasterizer.ReadPages(output,token),token); token.ThrowIfCancellationRequested();
            _boardWork?.Cancel(); _previewWork?.Cancel();
            _pages.Clear(); _pages.AddRange(info.Select(page => new PdfWorkspacePage(path,output,0,page))); _active = _pages[0]; _boardStart = 0;
            _undo.Clear(); UndoButton.IsEnabled = false;
            var oldFiles=_temporary.ToArray(); _temporary.Clear(); _temporary.Add(temporary); temporary = null;
            _=CleanupAsync(_work.ToArray(),oldFiles);
            RebuildBoard(); UpdatePreview(); StatusText.Text = "";
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) { if(!token.IsCancellationRequested && !_closed) StatusText.Text = ex.Message; }
        finally
        {
            if(temporary is not null) TryDelete(temporary);
            if(_textLayoutWork==work) { _textLayoutWork=null; _textLayoutPending=false; if(!_closed) RefreshSummary(); }
            work.Dispose();
        }
    }
    private async void BrowseClick(object? sender, RoutedEventArgs e) { if (await Ui.Folder(this, "选择输出目录") is {} folder) OutputFolder.Text = folder; }
    private void CancelClick(object? sender, RoutedEventArgs e) => Close(null);
    private async void ConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (_loading || _textLayoutPending) return;
        try
        {
            if (string.IsNullOrWhiteSpace(OutputFolder.Text)) throw new ArgumentException(Localization.Text("请选择输出目录。"));
            var options = new ConversionOptions { Format = _feature.Format, Pdf = Options() };
            var selectedInputs = options.Pdf!.Pages.Select(page => page.InputIndex).Distinct().ToArray();
            var remap = selectedInputs.Select((input,index) => (input,index)).ToDictionary(item => item.input,item => item.index);
            var files = selectedInputs.Select(index => _files[index]).ToArray();
            options.Pdf.Pages = options.Pdf.Pages.Select(page => page with { InputIndex = remap[page.InputIndex] }).ToList();
            PdfTools.Validate(new Job { FeatureId = _feature.Id, Inputs = files, Options = options });
            Close(new PdfWorkspaceRequest(files, Path.GetFullPath(OutputFolder.Text), options));
        }
        catch (Exception ex) { await Ui.Message(this, "参数错误", ex.Message); }
    }
}
