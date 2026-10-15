using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using AvaMedia.Controls;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop.MultiStage;

/// <summary>Shared building blocks for multi-stage content slots (UISPEC 10.16); every brush and size comes from theme resources.</summary>
internal static class MsUi
{
    public static Border Card(Control child, bool padded = true)
    {
        var card = new Border { Classes = { "ms-card" }, Child = child, ClipToBounds = !padded };
        if (padded) card.Classes.Add("ms-section");
        return card;
    }
    /// <summary>Keep overflow discoverable and clear of the card's rounded bottom edge.</summary>
    public static ScrollViewer Scroll(Control content) => new()
    {
        Classes = { "ms-scroll" }, Content = content, AllowAutoHide = false,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        Margin = new(0, 0, 0, 12)
    };

    public static void ListViewport(ListBox list)
    {
        list.Classes.Add("ms-scroll");
        ScrollViewer.SetAllowAutoHide(list, false);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        list.Padding = new(0, 0, 0, 12);
    }
    public static TextBlock Heading(string text) => new() { Classes = { "ms-heading" }, Text = Localization.Text(text), VerticalAlignment = VerticalAlignment.Center };
    public static TextBlock Caption(string text = "") => new() { Classes = { "ms-caption" }, Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    public static TextBlock Small(string text = "") => new() { Classes = { "ms-small" }, Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    public static Grid HeadRow(Control title, Control? right = null, double bottom = 9)
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8, Margin = new(0, 0, 0, bottom) };
        grid.Children.Add(title);
        if (right is not null) { right.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(right, 1); grid.Children.Add(right); }
        return grid;
    }
    public static Grid Columns(string definition, params Control[] children)
    {
        var grid = new Grid { ColumnDefinitions = new(definition) };
        grid.Bind(Grid.ColumnSpacingProperty, new DynamicResourceExtension("AcMsGap"));
        for (var i = 0; i < children.Length; i++) { Grid.SetColumn(children[i], i); grid.Children.Add(children[i]); }
        return grid;
    }
    public static void Brush(AvaloniaObject target, AvaloniaProperty property, string key) => target.Bind(property, new DynamicResourceExtension(key));
    public static Run Strong(string text)
    {
        var run = new Run(text) { FontWeight = FontWeight.SemiBold };
        run.Bind(TextElement.ForegroundProperty, new DynamicResourceExtension("UiText"));
        return run;
    }

    public static string Time(double seconds) => PersonTaskView.Time(Math.Max(0, seconds), 1);
    public static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    public static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
    public static string Codec(string value) => value.ToLowerInvariant() switch { "h264" => "H.264", "hevc" or "h265" => "H.265", "av1" => "AV1", "" => "", _ => value.ToUpperInvariant() };
    public static string Display(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && path.StartsWith(home, StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
    }
    public static string MediaDetail(MediaInfo? info, params string[] extra) => Localization.Join(" · ", new[]
    {
        info is { Width: > 0, Height: > 0 } ? $"{info.Width}×{info.Height}" : "", info is null ? "" : Codec(info.VideoCodec),
        info is { Duration: > 0 } ? Time(info.Duration) : ""
    }.Concat(extra).Where(s => s.Length > 0));

    public static string CurrentTitle(MultiStageTaskSnapshot snapshot) =>
        snapshot.CurrentStageIndex >= 0 && snapshot.CurrentStageIndex < snapshot.Stages.Count ? snapshot.Stages[snapshot.CurrentStageIndex].Stage.Title : "处理";

    /// <summary>Generic error title and recovery text from the interrupted stage, retained stages and the retry command.</summary>
    public static (string Title, string Recovery) Error(MultiStageTaskSnapshot snapshot)
    {
        var stages = snapshot.Stages;
        var failed = stages.FirstOrDefault(stage => stage.Stage.Id == snapshot.InterruptedStageId)?.Stage;
        var title = failed is null ? "" : Localization.Format($"{Localization.Text(failed.Title)}失败：");
        var retained = stages.Where(stage => snapshot.RetainedStageIds.Contains(stage.Stage.Id)).Select(stage => Localization.Text(stage.Stage.Title)).ToArray();
        var retry = stages.FirstOrDefault(stage => stage.Stage.Id == snapshot.Commands.RetryStageId)?.Stage.Title;
        var recovery = retry is null ? (snapshot.Commands.CanRerun ? Localization.Text("可重新执行全部阶段。") : "")
            : (retained.Length > 0 ? Localization.Format($"{string.Join(Localization.Text("与"), retained)}的结果已保留，") : "")
              + Localization.Format($"可从{Localization.Text(retry)}阶段重试。");
        return (title, recovery);
    }

    public static string Plan(MultiStageTaskSnapshot snapshot, string model) =>
        Localization.Join(" · ", new[] { Localization.Format($"{snapshot.Stages.Count} 个阶段"), model }.Where(s => s.Length > 0));

    public static string LiveNote(MultiStageTaskSnapshot snapshot, bool hasPartial) => snapshot.State switch
    {
        MultiStageTaskState.Configuring => Localization.Text("开始前确认参数"),
        MultiStageTaskState.Running or MultiStageTaskState.Stopping => Localization.Format($"正在{Localization.Text(CurrentTitle(snapshot))} · 结果随进度增加"),
        MultiStageTaskState.Paused => Localization.Text("已暂停 · 已有结果保留"),
        MultiStageTaskState.Failed or MultiStageTaskState.Cancelled => Localization.Text(hasPartial ? "部分结果可查看" : "没有可查看的结果"),
        _ => ""
    };

    /// <summary>Footer action without auto icon; failures surface as a message on the owning window.</summary>
    public static Button Action(string text, Func<Task>? action, bool primary = false)
    {
        Button? button = null;
        button = Ui.Button(text, () => _ = Run(button, action));
        button.Classes.Add("text-action"); if (primary) button.Classes.Add("primary");
        return button;
    }
    public static async Task Run(Control? anchor, Func<Task>? action)
    {
        if (action is null) return;
        try { await action(); }
        catch (Exception error) when (anchor is not null && TopLevel.GetTopLevel(anchor) is Window window) { await Ui.Message(window, "任务结果", error.Message); }
        catch (Exception error) { AppDiagnostics.Record("Multi-stage action", error); }
    }

    public static string GlyphName(StageState state) => state switch
    {
        StageState.Active => "active", StageState.Paused => "paused", StageState.Done => "done", StageState.Failed => "failed",
        StageState.Skipped => "skipped", StageState.Cancelled => "cancelled", _ => "pending"
    };
}

/// <summary>2×2 statistics card with hairline separators.</summary>
internal sealed class MsStatGrid : Border
{
    private readonly TextBlock[] _labels = Enumerable.Range(0, 4).Select(_ => new TextBlock { Classes = { "ms-small" } }).ToArray();
    private readonly TextBlock[] _values = Enumerable.Range(0, 4).Select(_ => new TextBlock { Classes = { "ms-stat" }, TextTrimming = TextTrimming.CharacterEllipsis }).ToArray();
    public MsStatGrid(bool compact = false)
    {
        Classes.Add("ms-card"); ClipToBounds = true;
        var grid = new Grid { ColumnDefinitions = new("*,*"), RowDefinitions = new("Auto,Auto") };
        for (var i = 0; i < 4; i++)
        {
            var cell = new Border { Classes = { "ms-row-line" }, Padding = new(14, compact ? 5 : 9), BorderThickness = new(i % 2 == 1 ? .5 : 0, i >= 2 ? .5 : 0, 0, 0) };
            var stack = new StackPanel { Spacing = 1 }; stack.Children.Add(_labels[i]); stack.Children.Add(_values[i]);
            cell.Child = stack; Grid.SetRow(cell, i / 2); Grid.SetColumn(cell, i % 2); grid.Children.Add(cell);
        }
        Child = grid;
    }
    public void Set(int index, string label, string value, string suffix = "")
    {
        _labels[index].Text = Localization.Text(label);
        var block = _values[index];
        block.Inlines = [new Run(value.Length > 0 ? value : "—")];
        if (suffix.Length == 0) return;
        var small = new Run(" " + suffix) { FontSize = 11, FontWeight = FontWeight.Normal };
        small.Bind(TextElement.ForegroundProperty, new DynamicResourceExtension("UiTextSecondary"));
        block.Inlines.Add(small);
    }
}

/// <summary>Read-only settings group (label left, value right, hairline rows).</summary>
internal sealed class MsSettingsGroup : Border
{
    private readonly StackPanel _rows = new();
    public MsSettingsGroup() { Classes.Add("ms-group"); Child = _rows; }
    public void Update(IReadOnlyList<(string Label, string Value, string Unit)> rows)
    {
        var key = string.Join("|", rows.Select(row => row.Label + row.Value + row.Unit));
        if (Tag as string == key) return;
        Tag = key; _rows.Children.Clear();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var grid = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12, MinHeight = 30, Margin = new(14, 0) };
            grid.Children.Add(new TextBlock { Classes = { "ms-body" }, Text = Localization.Text(row.Label), VerticalAlignment = VerticalAlignment.Center });
            var text = new TextBlock { Classes = { "ms-caption" }, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
            text.Inlines = [new Run(row.Value.Length > 0 ? Localization.Text(row.Value) : "—") { FontWeight = FontWeight.Medium, [!TextElement.ForegroundProperty] = new DynamicResourceExtension("UiText") }];
            if (row.Unit.Length > 0) text.Inlines.Add(new Run(Localization.Text(row.Unit)));
            ToolTip.SetTip(text, row.Value);
            Grid.SetColumn(text, 1); grid.Children.Add(text);
            _rows.Children.Add(new Border { Classes = { "ms-row-line" }, BorderThickness = new(0, i == 0 ? 0 : .5, 0, 0), Child = grid });
        }
    }
}

/// <summary>Footer output line: accent folder icon, 输出到, path and an optional 更改… link (only while configuring).</summary>
internal sealed class MsOutputLine : StackPanel
{
    private readonly TextBlock _path = MsUi.Caption(), _prefix = MsUi.Caption();
    private readonly Button _change;
    private readonly IMultiStageTask _task;
    public MsOutputLine(IMultiStageTask task)
    {
        _task = task; Orientation = Orientation.Horizontal; Spacing = 7; VerticalAlignment = VerticalAlignment.Center;
        var icon = new LucideIcon { Kind = "folder-closed", Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(LucideIcon.ForegroundProperty, new DynamicResourceExtension("UiAccent"));
        _prefix.Text = Localization.Text("输出到");
        Children.Add(icon); Children.Add(_prefix);
        _path.MaxWidth = 420; MsUi.Brush(_path, TextBlock.ForegroundProperty, "UiText"); Localization.SetIsUserText(_path, true);
        Children.Add(_path);
        _change = Ui.Button("更改…", () => _ = ChangeAsync()); _change.Classes.Add("link"); _change.Classes.Add("text-action");
        Children.Add(_change);
    }
    public void Update(TaskOutputView output, MultiStageTaskSnapshot snapshot, string fixedLabel = "应用内结果")
    {
        var finished = snapshot.State == MultiStageTaskState.Completed && output.Exists;
        var path = output.IsFixedLocation && !finished ? "" : finished || output.Folder.Length == 0 ? output.Path
            : output.Folder.TrimEnd('/', '\\') + System.IO.Path.DirectorySeparatorChar;
        _path.Text = path.Length > 0 ? MsUi.Display(path) : Localization.Text(fixedLabel);
        _prefix.IsVisible = path.Length > 0;
        Localization.SetIsUserText(_path, path.Length > 0);
        ToolTip.SetTip(_path, path.Length > 0 ? path : null);
        _change.IsVisible = snapshot.State == MultiStageTaskState.Configuring && snapshot.Commands.CanChangeOutput && !output.IsFixedLocation;
        IsVisible = output.Path.Length > 0 || output.Folder.Length > 0 || output.IsFixedLocation;
    }
    private async Task ChangeAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Localization.Text("选择输出文件夹"), AllowMultiple = false });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } folder) await _task.ChangeOutputFolderAsync(folder);
        }
        catch (Exception error) when (top is Window window) { await Ui.Message(window, "输出位置", error.Message); }
    }
}

/// <summary>Preview card: media surface with optional chips and a caption row; decoding runs off the UI thread.</summary>
internal sealed class MsPreviewCard : Border, IDisposable
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly LayoutTransformControl _rotated = new();
    private int _rotation;
    private readonly MediaPreviewSurface _surface = new() { UseAmbientLighting = false };
    private readonly TextBlock _placeholder = new() { Classes = { "ms-caption" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _dim = new() { IsHitTestVisible = false, IsVisible = false, Opacity = .55 };
    private readonly PreviewChip _left = new(HorizontalAlignment.Left), _right = new(HorizontalAlignment.Right);
    private readonly TextBlock _caption = MsUi.Caption(), _captionRight = MsUi.Caption();
    private readonly PreviewRequest _request = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Grid _frame = new();
    private string _key = "";
    private bool _disposed;
    public MsPreviewCard(Control? overlay = null)
    {
        Classes.Add("ms-card"); Classes.Add("ms-section");
        MsUi.Brush(_frame, Panel.BackgroundProperty, "AcPreviewFill");
        _rotated.Child = _image;
        _frame.Children.Add(_rotated); _frame.Children.Add(_placeholder);
        MsUi.Brush(_dim, Border.BackgroundProperty, "UiMediaOverlay"); _frame.Children.Add(_dim);
        if (overlay is not null) _frame.Children.Add(overlay);
        _frame.Children.Add(_left); _frame.Children.Add(_right);
        _placeholder.Text = Localization.Text("暂无预览");
        _surface.PreviewContent = _frame;
        var stack = new StackPanel(); stack.Children.Add(_surface);
        stack.SizeChanged += (_, e) => { if (e.NewSize.Width > 0) _surface.Height = Math.Round(e.NewSize.Width * (FollowSourceAspect && _image.Source is { } source ? source.Size.Height / source.Size.Width : 9d / 16)); };
        _captionRight.HorizontalAlignment = HorizontalAlignment.Right;
        var caption = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Margin = new(0, 10, 0, 0) };
        caption.Children.Add(_caption); Grid.SetColumn(_captionRight, 1); caption.Children.Add(_captionRight);
        stack.Children.Add(caption); Child = stack;
    }
    public bool Dimmed { set => _dim.IsVisible = value; }
    public bool FollowSourceAspect { get; set; }
    /// <summary>Clockwise preview rotation applied to the image only (orientation correction preview).</summary>
    public int Rotation
    {
        set
        {
            var rotation = ((value % 360) + 360) % 360;
            if (_rotation == rotation) return;
            _rotation = rotation; _rotated.LayoutTransform = rotation == 0 ? null : new RotateTransform(rotation);
            UpdateContentSize();
        }
    }
    /// <summary>Surface proportions follow the displayed (rotated) frame.</summary>
    private void UpdateContentSize()
    {
        if (_image.Source is not { } source) { _surface.ClearValue(MediaPreviewSurface.ContentSizeProperty); return; }
        var size = source.Size;
        if (FollowSourceAspect && _surface.Bounds.Width > 0) _surface.Height = _surface.Bounds.Width * size.Height / size.Width;
        _surface.ContentSize = _rotation is 90 or 270 ? new Size(size.Height, size.Width) : size;
    }
    public void SetChips(string left, string right = "") { _left.Text = left; _right.Text = right; }
    public void SetCaption(string title, string detail, string right = "")
    {
        _caption.Inlines = [new Run(Localization.Text(title)) { FontWeight = FontWeight.Medium, [!TextElement.ForegroundProperty] = new DynamicResourceExtension("UiText") }];
        if (detail.Length > 0) _caption.Inlines.Add(new Run("  ·  " + detail));
        _captionRight.Text = right;
    }
    public void ShowEncoded(byte[]? image)
    {
        if (image is null) { Clear(); return; }
        var key = "e:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(image).ToString(CultureInfo.InvariantCulture);
        if (_key == key) return; _key = key;
        _ = LoadAsync(_ => { using var memory = new MemoryStream(image); return Task.FromResult(new Bitmap(memory)); });
    }
    public void ShowFrame(IMediaEngine engine, string path, double seconds, int stream = 0)
    {
        var key = $"t:{path}:{seconds:0.000}:{stream}";
        if (_key == key || path.Length == 0 || !File.Exists(path)) { if (path.Length == 0 || !File.Exists(path)) Clear(); return; }
        _key = key;
        _ = LoadAsync(async token =>
        {
            var data = await engine.Thumbnail(path, seconds, VideoPreviewSizes.Compact.Width, VideoPreviewSizes.Compact.Height, token, pad: false, videoStreamIndex: stream).ConfigureAwait(false);
            using var memory = new MemoryStream(data); return new Bitmap(memory);
        });
    }
    public void ShowFile(string path)
    {
        var key = "f:" + path;
        if (_key == key) return; _key = key;
        _ = LoadAsync(_ => Task.FromResult(new Bitmap(path)));
    }
    private void Clear()
    {
        _key = ""; _request.Cancel();
        var old = _image.Source as Bitmap; _image.Source = null; old?.Dispose(); _placeholder.IsVisible = true;
        UpdateContentSize();
    }
    private async Task LoadAsync(Func<CancellationToken, Task<Bitmap>> decode)
    {
        if (_disposed) return;
        var token = _request.Restart(_lifetime.Token);
        try
        {
            var bitmap = await Task.Run(() => decode(token), token);
            if (_disposed || !_request.IsCurrent(token)) { bitmap.Dispose(); return; }
            var old = _image.Source as Bitmap; _image.Source = bitmap; old?.Dispose(); _placeholder.IsVisible = false;
            UpdateContentSize();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            AppDiagnostics.Record("Multi-stage preview", error);
            if (_request.IsCurrent(token)) Clear();
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _request.Cancel();
        var old = _image.Source as Bitmap; _image.Source = null; old?.Dispose();
        _surface.Dispose(); _lifetime.Dispose();
    }
}

/// <summary>Kinds of ranges painted on a <see cref="MsBand"/>.</summary>
internal enum MsBandKind { Accent, Soft, Track, Danger }

/// <summary>Source-time band: track up to the observed boundary, an explicit unscanned remainder, coloured ranges and a boundary mark.</summary>
internal sealed class MsBand : Control
{
    private static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<MsBand, IBrush?>("Accent");
    private static readonly StyledProperty<IBrush?> SoftProperty = AvaloniaProperty.Register<MsBand, IBrush?>("Soft");
    private static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<MsBand, IBrush?>("Track");
    private static readonly StyledProperty<IBrush?> UnscannedProperty = AvaloniaProperty.Register<MsBand, IBrush?>("Unscanned");
    private static readonly StyledProperty<IBrush?> HairlineProperty = AvaloniaProperty.Register<MsBand, IBrush?>("Hairline");
    private static readonly StyledProperty<IBrush?> InkProperty = AvaloniaProperty.Register<MsBand, IBrush?>("Ink");
    private static readonly StyledProperty<IBrush?> DangerProperty = AvaloniaProperty.Register<MsBand, IBrush?>("Danger");
    private IReadOnlyList<(double Start, double End, MsBandKind Kind)> _ranges = [];
    private double _duration, _observed;
    private bool _partial, _failed;
    static MsBand() => AffectsRender<MsBand>(AccentProperty, SoftProperty, TrackProperty, UnscannedProperty, HairlineProperty, InkProperty, DangerProperty);
    public MsBand(string name)
    {
        Height = 26;
        Bind(AccentProperty, new DynamicResourceExtension("UiAccent")); Bind(SoftProperty, new DynamicResourceExtension("AcSelection"));
        Bind(TrackProperty, new DynamicResourceExtension("UiTrack")); Bind(UnscannedProperty, new DynamicResourceExtension("AcUnscannedFill"));
        Bind(HairlineProperty, new DynamicResourceExtension("AcHairline")); Bind(InkProperty, new DynamicResourceExtension("UiText"));
        Bind(DangerProperty, new DynamicResourceExtension("UiDanger"));
        Avalonia.Automation.AutomationProperties.SetName(this, Localization.Text(name));
    }
    public void Update(IReadOnlyList<(double Start, double End, MsBandKind Kind)> ranges, double duration, double observed, bool partial, bool failed)
    {
        _ranges = ranges; _duration = double.IsFinite(duration) ? Math.Max(0, duration) : 0;
        _observed = partial ? Math.Clamp(double.IsFinite(observed) ? observed : 0, 0, _duration) : _duration;
        _partial = partial; _failed = failed; InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0) return;
        using (context.PushClip(new RoundedRect(bounds, 6)))
        {
            context.FillRectangle(GetValue(UnscannedProperty) ?? Brushes.Transparent, bounds);
            if (_duration <= 0) return;
            double X(double seconds) => Math.Clamp(seconds / _duration, 0, 1) * bounds.Width;
            var boundary = X(_observed);
            context.FillRectangle(GetValue(TrackProperty) ?? Brushes.Transparent, new Rect(0, 0, boundary, bounds.Height));
            foreach (var (start, end, kind) in _ranges)
            {
                var left = X(start); var right = Math.Max(left + 1.5, X(end));
                var brush = kind switch { MsBandKind.Soft => SoftProperty, MsBandKind.Track => TrackProperty, MsBandKind.Danger => DangerProperty, _ => AccentProperty };
                context.FillRectangle(GetValue(brush) ?? Brushes.Transparent, new Rect(left, 0, right - left, bounds.Height));
            }
            if (_partial && boundary < bounds.Width)
                context.DrawLine(new Pen(GetValue(_failed ? DangerProperty : InkProperty), 2), new Point(boundary, 0), new Point(boundary, bounds.Height));
        }
        context.DrawRectangle(null, new Pen(GetValue(HairlineProperty), .5), new RoundedRect(bounds.Deflate(.25), 6));
    }
}

/// <summary>Five evenly spaced time ticks under a band.</summary>
internal sealed class MsTicks : Grid
{
    private readonly TextBlock[] _ticks = Enumerable.Range(0, 5).Select(_ => new TextBlock { Classes = { "ms-tick" } }).ToArray();
    public MsTicks()
    {
        ColumnDefinitions = new("*,*,*,*,*"); Margin = new(0, 5, 0, 0);
        for (var i = 0; i < 5; i++)
        {
            _ticks[i].HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : i == 4 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            SetColumn(_ticks[i], i); Children.Add(_ticks[i]);
        }
    }
    public void Update(double duration) { for (var i = 0; i < 5; i++) _ticks[i].Text = duration > 0 ? PersonTaskView.Time(duration * i / 4, 0) : "—"; }
}

/// <summary>Legend item: small rounded swatch and caption.</summary>
internal static class MsLegend
{
    public static StackPanel Create(params (string Text, string Brush, bool Outline)[] items)
    {
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (text, brush, outline) in items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            var dot = new Border { Width = 8, Height = 8, CornerRadius = new(2), VerticalAlignment = VerticalAlignment.Center };
            MsUi.Brush(dot, Border.BackgroundProperty, brush);
            if (outline) { dot.BorderThickness = new(.5); MsUi.Brush(dot, Border.BorderBrushProperty, "AcHairline"); }
            row.Children.Add(dot); row.Children.Add(MsUi.Small(Localization.Text(text)));
            legend.Children.Add(row);
        }
        return legend;
    }
}
