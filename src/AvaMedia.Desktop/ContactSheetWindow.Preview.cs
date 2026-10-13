using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed partial class ContactSheetWindow
{
    private readonly TextBlock _previewTitle = Label("选择视频后显示预览", bold: true);
    private readonly TextBlock _videoInformationLabel = Label("", secondary: true);
    private readonly TextBlock _previewSize = Label("", secondary: true);
    private readonly TextBlock _previewMessage = Label("", secondary: true);
    private readonly TextBlock _outputDimensions = Label("", secondary: true);
    private readonly TextBlock _rangeDescription = Label("整个视频", secondary: true);
    private readonly TextBlock _samplingDescription = Label("", secondary: true);
    private readonly TextBlock _startLabel = Label("开始 00:00.000", secondary: true);
    private readonly TextBlock _endLabel = Label("结束 00:00.000", secondary: true);
    private readonly Viewbox _preview = new() { Stretch = Stretch.Uniform };
    private readonly MediaPreviewSurface _previewSurface = new();
    private readonly Filmstrip _range = new();
    private readonly Button _wholeLink = Ui.Button("使用整个视频", () => { });
    private readonly List<PreviewCell> _cells = [];
    private readonly List<Bitmap> _filmFrames = [];
    private readonly SemaphoreSlim _metadataSlots = new(2);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _previewWork;
    private CancellationTokenSource? _filmWork;
    private VideoEntry? _previewEntry;
    private bool _wholeVideo = true;
    internal Task PreviewReady { get; private set; } = Task.CompletedTask;
    internal Task FilmReady { get; private set; } = Task.CompletedTask;

    private Border CreatePreview()
    {
        var panel = new Grid { RowDefinitions = new("Auto,Auto,*,Auto,Auto,Auto"), RowSpacing = 12 };
        var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        _previewTitle.TextWrapping = TextWrapping.NoWrap; _previewTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(_previewTitle); Grid.SetColumn(_previewSize, 1); header.Children.Add(_previewSize); panel.Children.Add(header);
        Grid.SetRow(_videoInformationLabel, 1); panel.Children.Add(_videoInformationLabel);
        _previewSurface.PreviewContent = _preview;
        var stage = new Border { Child = _previewSurface, Classes = { "sheet-stage" } };
        Grid.SetRow(stage, 2); panel.Children.Add(stage);
        _outputDimensions.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(_outputDimensions, 3); panel.Children.Add(_outputDimensions);
        _previewMessage.IsVisible = false; Grid.SetRow(_previewMessage, 4); panel.Children.Add(_previewMessage);
        var timeline = new Grid { RowDefinitions = new("Auto,Auto"), RowSpacing = 4 };
        _range.Bind(HeightProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SheetFilmHeight"));
        Resource(_range, AvaMedia.MediaUi.RangeBar.TrackBrushProperty, "UiTrack");
        Resource(_range, AvaMedia.MediaUi.RangeBar.SelectionBrushProperty, "UiAccent");
        Resource(_range, AvaMedia.MediaUi.RangeBar.BorderBrushProperty, "UiBorder");
        Resource(_range, AvaMedia.MediaUi.RangeBar.HandleBrushProperty, "UiSurface");
        Resource(_range, AvaMedia.MediaUi.RangeBar.GripBrushProperty, "UiAccent");
        _range.Duration = _range.Start = _range.End = 0;
        _range.IsEnabled = _wholeLink.IsEnabled = false; _range.Focusable = true; _range.Step = .001;
        Avalonia.Automation.AutomationProperties.SetName(_range, "截取范围，拖动两端调整开始和结束时间");
        timeline.Children.Add(_range);
        var labels = new Grid { ColumnDefinitions = new("*,Auto,*") };
        labels.Children.Add(_startLabel); _wholeLink.Classes.Add("sheet-link");
        _wholeLink.Click += (_, _) =>
        {
            _wholeVideo = true; _range.Start = 0; _range.End = _range.Duration;
            _range.InvalidateVisual(); UpdateRangeLabels(); SchedulePreview();
        };
        Grid.SetColumn(_wholeLink, 1); labels.Children.Add(_wholeLink);
        _endLabel.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(_endLabel, 2); labels.Children.Add(_endLabel);
        Grid.SetRow(labels, 1); timeline.Children.Add(labels); Grid.SetRow(timeline, 5); panel.Children.Add(timeline);
        _range.Changed += (start, end) =>
        {
            _wholeVideo = start == 0 && Math.Abs(end - _range.Duration) < .001;
            UpdateRangeLabels(); SchedulePreview();
        };
        _range.KeyboardChanged += () =>
        {
            _wholeVideo = _range.Start == 0 && Math.Abs(_range.End - _range.Duration) < .001;
            UpdateRangeLabels(); SchedulePreview();
        };
        return Card(panel);
    }

    private void SelectVideo()
    {
        if (_closed) return;
        var entry = _list.SelectedItem as VideoEntry;
        if (_previewEntry != entry)
        {
            _previewEntry = entry; _wholeVideo = true;
            _range.Start = 0; _range.Duration = entry?.Info?.Duration ?? 0; _range.End = _range.Duration;
            _filmWork?.Cancel(); ClearFilm();
            _range.IsEnabled = _range.Duration > 0 && !_importing;
            _wholeLink.IsEnabled = entry is not null && !_importing;
            UpdateRangeLabels();
            if (entry is not null && !_importing) StartFilm(entry);
        }
        SchedulePreview();
    }

    private void UpdateRangeLabels()
    {
        Localization.SetText(_startLabel, $"开始 {MediaTime.Format(_range.Start)}");
        Localization.SetText(_endLabel, $"结束 {MediaTime.Format(_range.End)}");
        _rangeDescription.Text = _wholeVideo ? "整个视频" : "自定义区间";
    }

    private void SchedulePreview()
    {
        if (_closed || _importing) return;
        _previewWork?.Cancel();
        var work = new CancellationTokenSource(); _previewWork = work;
        var entry = _previewEntry;
        var options = Options();
        _updating = true; UpdatePreset(); _updating = false; InvalidatePlan(); UpdateRangeLabels();
        Localization.SetText(_samplingDescription, $"在区间内按格数均匀取帧，共 {options.Columns * options.Rows * options.SheetsPerVideo} 帧。可在预览下方拖动区间。");
        _previewTitle.Text = entry?.Name ?? "选择视频后显示预览";
        ToolTip.SetTip(_previewTitle, entry?.Path);
        Localization.SetIsUserText(_previewTitle, entry is not null);
        _previewMessage.IsVisible = false;
        _videoInformationLabel.IsVisible = entry is not null && options.VideoInformation;
        _previewSize.IsVisible = entry is not null; _outputDimensions.IsVisible = entry is null;
        BuildCells(options, options.CellWidth * options.ExportScale, options.CellHeight * options.ExportScale, entry is not null);
        if (entry is null)
        {
            StopPreviewMotion(); _previewWork = null; work.Dispose(); PreviewReady = Task.CompletedTask; return;
        }
        StartPreviewMotion();
        PreviewReady = RenderPreview(entry, options, work);
    }

    private void BuildCells(ContactSheetOptions options, int width, int height, bool loading)
    {
        ClearPreview();
        _previewSurface.BeginGrid(options.Columns, options.Rows);
        const double gap = 4;
        var cellWidth = (560d - gap * (options.Columns - 1)) / options.Columns;
        var cellHeight = cellWidth * height / width;
        var grid = new Grid
        {
            Width = 560, Height = cellHeight * options.Rows + gap * (options.Rows - 1), ColumnSpacing = gap, RowSpacing = gap,
            ColumnDefinitions = new(string.Join(",", Enumerable.Repeat("*", options.Columns))),
            RowDefinitions = new(string.Join(",", Enumerable.Repeat("*", options.Rows)))
        };
        for (var i = 0; i < options.Columns * options.Rows; i++)
        {
            var number = Label((i + 1).ToString(), secondary: true);
            number.HorizontalAlignment = HorizontalAlignment.Center; number.VerticalAlignment = VerticalAlignment.Center;
            var content = new Grid(); content.Children.Add(number);
            var face = new Border { Child = content, Classes = { "sheet-cell" } };
            Grid.SetRow(face, i / options.Columns); Grid.SetColumn(face, i % options.Columns); grid.Children.Add(face);
            var cell = new PreviewCell(face, content, number) { Loading = loading };
            _cells.Add(cell);
            face.AttachedToVisualTree += (_, _) => RefreshPreviewMotion();
            face.DetachedFromVisualTree += (_, _) => StopCellMotion(cell);
        }
        _preview.Child = grid;
        RefreshPreviewMotion();
        var outputHeight = height * options.Rows + BatchVideoTools.ContactSheetHeaderHeight(width, options);
        Localization.SetText(_previewSize, $"输出 {width * options.Columns} × {outputHeight}");
        Localization.SetText(_outputDimensions, $"输出尺寸 {width * options.Columns} × {outputHeight} 像素");
    }

    private async Task RenderPreview(VideoEntry entry, ContactSheetOptions options, CancellationTokenSource work)
    {
        var token = work.Token;
        try
        {
            await Task.Delay(300, token);
            BatchVideoTools.ValidateContactSheet(options);
            var info = entry.Info ?? await entry.Probe!.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (!info.HasVideo || !double.IsFinite(info.Duration) || info.Duration <= 0)
                throw new InvalidDataException("文件不含可截图的视频或无法读取时长。");
            var layout = BatchVideoTools.ContactSheetPreviewLayout(info, options);
            var imageSize = BatchVideoTools.ContactSheetImageSize(info, options);
            _videoInformationLabel.Text = BatchVideoTools.ContactSheetVideoInformation(info);
            _videoInformationLabel.IsVisible = options.VideoInformation;
            var end = options.EndSeconds > 0 ? Math.Min(options.EndSeconds, layout.Duration) : layout.Duration;
            if (options.StartSeconds >= end) throw new ArgumentException("开始时间超出视频时长。");
            _range.Duration = layout.Duration; if (_wholeVideo) _range.End = layout.Duration;
            _range.IsEnabled = true; _range.InvalidateVisual(); UpdateRangeLabels();
            BuildCells(options, layout.Width, layout.Height, loading: true);
            var count = options.Columns * options.Rows; var failed = 0;
            var scale = Math.Min(1, 560d / Math.Max(layout.Width * options.Columns, layout.Height * options.Rows));
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                var requested = options.StartSeconds + (end - options.StartSeconds) * (i + .5) / (count * options.SheetsPerVideo);
                var seconds = Math.Min(requested, Math.Max(0, layout.Duration - Math.Max(.08, 1 / layout.FrameRate)));
                var cell = _cells[i];
                try
                {
                    var data = await _engine.Thumbnail(entry.Path, seconds, Math.Max(1, (int)(layout.Width * scale)),
                        Math.Max(1, (int)(layout.Height * scale)), token, pad: false, videoStreamIndex: info.VideoStreamIndex);
                    token.ThrowIfCancellationRequested();
                    using var stream = new MemoryStream(data); var bitmap = new Bitmap(stream);
                    cell.Bitmap = bitmap; cell.Content.Children.Clear();
                    cell.Content.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Fill });
                    _previewSurface.SetFrame(i, data, PreviewFrameKey(entry.Path, seconds, bitmap.PixelSize));
                    cell.Face.CornerRadius = new(0);
                    if (options.Timestamps)
                    {
                        var timestamp = Label(TimeSpan.FromSeconds(seconds).ToString(layout.Duration >= 3600 ? @"h\:mm\:ss" : @"mm\:ss"));
                        timestamp.FontSize = 10; timestamp.FontWeight = FontWeight.Normal; SheetLabel(timestamp);
                        cell.Content.Children.Add(new Border
                        {
                            Child = timestamp, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                            Margin = new(5, 4), Classes = { "sheet-timestamp" }
                        });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    token.ThrowIfCancellationRequested(); failed++; cell.Number.Text = "无法读取"; cell.Number.FontSize = 11;
                }
                cell.Loading = false; StopCellMotion(cell);
                Localization.SetText(_previewSize, $"正在抽帧 {i + 1} / {count} · {imageSize.Width} × {imageSize.Height}");
            }
            Localization.SetText(_previewSize, $"预览 · 第 1 / {options.SheetsPerVideo} 张 · {imageSize.Width} × {imageSize.Height}");
            if (failed == count) ShowPreviewError("整个视频读取失败：未能读取任何预览帧。");
            else if (_preview.Child is Grid grid)
            {
                grid.ColumnSpacing = grid.RowSpacing = 0;
                grid.Height = 560d / options.Columns * layout.Height / layout.Width * options.Rows;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested && !_closed)
            {
                // Retain the structural placeholder, including the current output dimensions.
                ShowPreviewError(Localization.Format($"视频预览读取失败：{error.Message}"));
            }
        }
        finally
        {
            if (_previewWork == work)
            {
                StopPreviewMotion(); foreach (var cell in _cells) cell.Loading = false;
                _previewWork = null;
            }
            work.Dispose();
        }
    }

    private void ShowPreviewError(string message)
    {
        _previewMessage.Text = message; _previewMessage.IsVisible = true;
        Resource(_previewMessage, TextBlock.ForegroundProperty, "UiDanger");
    }

    private async Task ReadMetadata(VideoEntry entry)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, entry.MetadataWork.Token);
        var token = linked.Token; var acquired = false;
        // Share probing with the selected video's preview and filmstrip.
        entry.Probe = ProbeVideo(entry.Path, token);
        try
        {
            var info = await entry.Probe; token.ThrowIfCancellationRequested();
            await _metadataSlots.WaitAsync(token); acquired = true;
            if (!info.HasVideo || !double.IsFinite(info.Duration) || info.Duration <= 0) throw new InvalidDataException("无法读取视频时长。");
            entry.Info = info;
            var data = await _engine.Thumbnail(entry.Path, Math.Min(.1, info.Duration / 2), 92, 56, token, pad: false, videoStreamIndex: info.VideoStreamIndex);
            token.ThrowIfCancellationRequested(); using var stream = new MemoryStream(data); entry.Thumbnail = new Bitmap(stream);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!token.IsCancellationRequested && !_closed && entry.Status == "等待") entry.Status = "预览无法读取";
        }
        finally { if (acquired) _metadataSlots.Release(); entry.CompleteMetadata(); }
    }

    private async Task<MediaInfo> ProbeVideo(string path, CancellationToken token)
    {
        await _metadataSlots.WaitAsync(token);
        try { return await _engine.Probe(path, token); }
        finally { _metadataSlots.Release(); }
    }

    private void StartFilm(VideoEntry entry)
    {
        _filmWork?.Cancel(); ClearFilm();
        var work = new CancellationTokenSource(); _filmWork = work;
        FilmReady = ReadFilm(entry, work);
    }
    private async Task ReadFilm(VideoEntry entry, CancellationTokenSource work)
    {
        var token = work.Token;
        try
        {
            await Task.Delay(300, token);
            var info = entry.Info ?? await entry.Probe!.WaitAsync(token); token.ThrowIfCancellationRequested();
            if (!info.HasVideo || !double.IsFinite(info.Duration) || info.Duration <= 0) return;
            _range.Duration = BatchVideoTools.ContactSheetPreviewLayout(info, Options()).Duration;
            if (_wholeVideo) _range.End = _range.Duration;
            _range.InvalidateVisual(); UpdateRangeLabels();
            for (var i = 0; i < 10; i++)
            {
                Bitmap? bitmap = null;
                try
                {
                    var data = await _engine.Thumbnail(entry.Path, _range.Duration * (i + .5) / 10, 112, 64, token,
                        pad: false, videoStreamIndex: info.VideoStreamIndex);
                    token.ThrowIfCancellationRequested(); using var stream = new MemoryStream(data); bitmap = new Bitmap(stream);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { token.ThrowIfCancellationRequested(); }
                if (bitmap is not null) _filmFrames.Add(bitmap);
                _range.Frames[i] = bitmap; _range.InvalidateVisual();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* The main preview displays the probe error. */ }
        finally { if (_filmWork == work) _filmWork = null; work.Dispose(); }
    }

    private void ClearFilm()
    {
        Array.Clear(_range.Frames); _range.InvalidateVisual();
        foreach (var bitmap in _filmFrames) bitmap.Dispose(); _filmFrames.Clear();
    }
    private void ClearPreview()
    {
        foreach (var cell in _cells) StopCellMotion(cell);
        _previewSurface.ClearAmbient();
        _preview.Child = null;
        foreach (var cell in _cells) cell.Bitmap?.Dispose(); _cells.Clear();
    }
    private static string PreviewFrameKey(string path, double seconds, PixelSize size)
        => FormattableString.Invariant($"{path}|{seconds:R}|{size.Width}x{size.Height}");

    private sealed class PreviewCell(Border face, Grid content, TextBlock number)
    {
        public Border Face { get; } = face;
        public Grid Content { get; } = content;
        public TextBlock Number { get; } = number;
        public Bitmap? Bitmap { get; set; }
        public bool Loading { get; set; }
        public Avalonia.Rendering.Composition.CompositionVisual? PulseVisual { get; set; }
    }

    /// <summary>Uses the shared range gestures; draws real full-video frames with theme brushes.</summary>
    private sealed class Filmstrip : AvaMedia.MediaUi.RangeBar
    {
        public Bitmap?[] Frames { get; } = new Bitmap?[10];
        private bool _endFocused;
        public override void Render(DrawingContext context)
        {
            var track = new Rect(14, 0, Math.Max(1, Bounds.Width - 28), Bounds.Height);
            context.DrawRectangle(TrackBrush, null, track, 5, 5);
            using (context.PushClip(new RoundedRect(track, 5)))
            {
                for (var i = 0; i < Frames.Length; i++)
                    if (Frames[i] is { } bitmap)
                        context.DrawImage(bitmap, new Rect(bitmap.Size), new Rect(track.X + i * track.Width / Frames.Length, 0, track.Width / Frames.Length, track.Height));
            }
            var duration = Math.Max(.001, Duration);
            var left = track.X + track.Width * Start / duration; var right = track.X + track.Width * End / duration;
            context.DrawRectangle(null, new Pen(IsEnabled ? SelectionBrush : BorderBrush, 2), new Rect(left, 1, Math.Max(1, right - left), Math.Max(1, Bounds.Height - 2)), 5, 5);
            foreach (var x in new[] { left, right })
                context.DrawRectangle(IsEnabled ? SelectionBrush : BorderBrush, null, new Rect(x - 3, 2, 6, Math.Max(1, Bounds.Height - 4)), 3, 3);
            if (IsKeyboardFocusWithin)
                context.DrawRectangle(null, new Pen(HandleBrush, 1), new Rect((_endFocused ? right : left) - 4, 1, 8, Math.Max(1, Bounds.Height - 2)), 3, 3);
        }
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            Focus(); _endFocused = e.GetPosition(this).X > Bounds.Width / 2; base.OnPointerPressed(e);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Space) { _endFocused = !_endFocused; InvalidateVisual(); e.Handled = true; }
            else if (e.Key is Key.Left or Key.Right or Key.Home or Key.End)
            {
                var delta = Math.Max(.001, Duration / 1000) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1);
                var value = (_endFocused ? End : Start) + (e.Key == Key.Left ? -delta : delta);
                if (e.Key == Key.Home) value = 0; if (e.Key == Key.End) value = Duration;
                var minimum = Math.Min(.01, Duration);
                if (_endFocused) End = Math.Clamp(value, Math.Min(Duration, Start + minimum), Duration);
                else Start = Math.Clamp(value, 0, Math.Max(0, End - minimum));
                InvalidateVisual(); NotifyKeyboardChange(); e.Handled = true;
            }
            base.OnKeyDown(e);
        }
        public event Action? KeyboardChanged;
        private void NotifyKeyboardChange() => KeyboardChanged?.Invoke();
    }
}
