using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace AvaMedia.Desktop.Controls;

internal interface IClassificationCoverSource
{
    Task<byte[]> ReadClassificationCoverAsync(string path, double seconds, int width, CancellationToken ct);
}

/// <summary>Realized covers own their bitmap; virtualized or removed cards cancel and release it.</summary>
internal sealed class ClassificationCover : Grid
{
    public static readonly StyledProperty<string?> PathProperty = AvaloniaProperty.Register<ClassificationCover, string?>(nameof(Path));
    public static readonly StyledProperty<double> SecondsProperty = AvaloniaProperty.Register<ClassificationCover, double>(nameof(Seconds));
    public string? Path { get => GetValue(PathProperty); set => SetValue(PathProperty, value); }
    public double Seconds { get => GetValue(SecondsProperty); set => SetValue(SecondsProperty, value); }
    public int ResolutionWidth { get; init; } = 240;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _placeholder = Ui.Text("加载封面…", "caption");
    private IClassificationCoverSource? _owner;
    private CancellationTokenSource? _request;
    private Bitmap? _bitmap;

    public ClassificationCover()
    {
        ClipToBounds = true;
        _placeholder.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        _placeholder.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Children.Add(_placeholder); Children.Add(_image);
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _owner = this.GetVisualAncestors().OfType<IClassificationCoverSource>().FirstOrDefault(); Refresh();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Release(); _owner = null; base.OnDetachedFromVisualTree(e);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PathProperty) { Release(); _placeholder.IsVisible = true; Refresh(); }
        else if (change.Property == SecondsProperty) Refresh();
    }
    private void Release()
    {
        _request?.Cancel(); _image.Source = null; _bitmap?.Dispose(); _bitmap = null;
    }
    private async void Refresh()
    {
        if (_owner is not { } owner || Path is not { Length: > 0 } path) return;
        _request?.Cancel();
        using var request = new CancellationTokenSource(); _request = request;
        _placeholder.Text = Localization.Text("加载封面…"); _placeholder.IsVisible = _bitmap is null;
        try
        {
            var bytes = await owner.ReadClassificationCoverAsync(path, Seconds, ResolutionWidth, request.Token);
            if (request.IsCancellationRequested || _request != request || _owner != owner) return;
            using var stream = new MemoryStream(bytes); var next = new Bitmap(stream);
            var previous = _bitmap; _bitmap = next; _image.Source = next; previous?.Dispose(); _placeholder.IsVisible = false;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (_request == request) { _placeholder.Text = Localization.Text("预览不可用"); _placeholder.IsVisible = _bitmap is null; }
        }
        finally { if (_request == request) _request = null; }
    }
}

internal sealed class ClassificationBasketWeave : Control
{
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<ClassificationBasketWeave, IBrush?>(nameof(Stroke));
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    static ClassificationBasketWeave() => AffectsRender<ClassificationBasketWeave>(StrokeProperty);
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var w = Bounds.Width; var h = Bounds.Height;
        if (w < 24 || h < 16) return;
        var pen = new Pen(Stroke ?? Brushes.Gray, 1.3);
        var shape = new StreamGeometry();
        using (var geometry = shape.Open())
        {
            geometry.BeginFigure(new(2, 3), false); geometry.LineTo(new(w - 2, 3));
            geometry.LineTo(new(w - 12, h - 3)); geometry.LineTo(new(12, h - 3)); geometry.EndFigure(true);
        }
        using (context.PushOpacity(.45))
        {
            for (var y = 9d; y < h - 3; y += 7)
            {
                var inset = 2 + 10 * (y - 3) / (h - 6); context.DrawLine(pen, new(inset, y), new(w - inset, y));
            }
            for (var x = 10d; x < w - 6; x += 10)
            {
                var bottom = 12 + (x - 2) / (w - 4) * (w - 24);
                context.DrawLine(pen, new(x, 4), new(bottom, h - 4));
            }
        }
        context.DrawGeometry(null, pen, shape); context.DrawLine(new Pen(Stroke ?? Brushes.Gray, 3), new(1, 3), new(w - 1, 3));
    }
}
