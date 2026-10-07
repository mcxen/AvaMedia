using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;

/// <summary>Shared viewport and zoom make both sides compare the same image location.</summary>
public sealed class ImageCompareView : Control
{
    public static readonly StyledProperty<IImage?> SourceProperty = AvaloniaProperty.Register<ImageCompareView, IImage?>(nameof(Source));
    public static readonly StyledProperty<IImage?> ResultProperty = AvaloniaProperty.Register<ImageCompareView, IImage?>(nameof(Result));
    public static readonly StyledProperty<double> SplitProperty = AvaloniaProperty.Register<ImageCompareView, double>(nameof(Split), .5);
    public static readonly StyledProperty<bool> SideBySideProperty = AvaloniaProperty.Register<ImageCompareView, bool>(nameof(SideBySide));
    public static readonly StyledProperty<IBrush?> SurfaceProperty = AvaloniaProperty.Register<ImageCompareView, IBrush?>(nameof(Surface));
    public static readonly StyledProperty<IBrush?> CheckerProperty = AvaloniaProperty.Register<ImageCompareView, IBrush?>(nameof(Checker));
    public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<ImageCompareView, IBrush?>(nameof(Accent));
    public IImage? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public IImage? Result { get => GetValue(ResultProperty); set => SetValue(ResultProperty, value); }
    public double Split { get => GetValue(SplitProperty); set => SetValue(SplitProperty, value); }
    public bool SideBySide { get => GetValue(SideBySideProperty); set => SetValue(SideBySideProperty, value); }
    public IBrush? Surface { get => GetValue(SurfaceProperty); set => SetValue(SurfaceProperty, value); }
    public IBrush? Checker { get => GetValue(CheckerProperty); set => SetValue(CheckerProperty, value); }
    public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    private double _zoom = 1;
    private Vector _pan;
    private Point _last;
    private bool _dragging, _movingSplit;
    private static readonly Cursor SplitCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor PanCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor DefaultCursor = new(StandardCursorType.Arrow);

    static ImageCompareView() => AffectsRender<ImageCompareView>(SourceProperty, ResultProperty, SplitProperty, SideBySideProperty,
        SurfaceProperty, CheckerProperty, AccentProperty);
    public ImageCompareView() { Focusable = true; ClipToBounds = true; }
    public void ResetView() { _zoom = 1; _pan = default; InvalidateVisual(); }
    public void Zoom(double multiplier)
    {
        _zoom = Math.Clamp(_zoom * multiplier, .05, 32);
        if (_zoom <= 1) _pan = default;
        InvalidateVisual();
    }
    public void ActualSize()
    {
        if (Source is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var fit = Math.Min((SideBySide ? Bounds.Width / 2 : Bounds.Width) / Source.Size.Width, Bounds.Height / Source.Size.Height);
        _zoom = 1 / fit; _pan = default;
        InvalidateVisual();
    }
    private void Background(DrawingContext context, Rect area)
    {
        context.DrawRectangle(Surface, null, area);
        for (var y = 0d; y < area.Height; y += 16)
            for (var x = 0d; x < area.Width; x += 16)
                if (((int)(x / 16) + (int)(y / 16)) % 2 == 0)
                    context.DrawRectangle(Checker, null, new Rect(x, y, 16, 16));
    }
    public override void Render(DrawingContext context)
    {
        var area = new Rect(Bounds.Size);
        Background(context, area);
        if (Source is null) return;
        if (SideBySide)
        {
            var left = new Rect(0, 0, area.Width / 2, area.Height);
            var right = new Rect(area.Width / 2, 0, area.Width / 2, area.Height);
            Draw(context, Source, left);
            if (Result is not null) Draw(context, Result, right);
            context.DrawLine(new Pen(Accent, 1), new(area.Width / 2, 0), new(area.Width / 2, area.Height));
        }
        else
        {
            Draw(context, Source, area);
            if (Result is null) return;
            var position = area.Width * Math.Clamp(Split, 0, 1);
            using (context.PushClip(new Rect(position, 0, Math.Max(0, area.Width - position), area.Height)))
            { Background(context, area); Draw(context, Result, area); }
            var pen = new Pen(Accent, IsFocused ? 3 : 2);
            context.DrawLine(pen, new(position, 0), new(position, area.Height));
            context.DrawRectangle(Surface, pen, new Rect(position - 14, area.Height / 2 - 18, 28, 36), 3, 3);
            context.DrawLine(pen, new(position - 4, area.Height / 2 - 6), new(position - 9, area.Height / 2));
            context.DrawLine(pen, new(position - 9, area.Height / 2), new(position - 4, area.Height / 2 + 6));
            context.DrawLine(pen, new(position + 4, area.Height / 2 - 6), new(position + 9, area.Height / 2));
            context.DrawLine(pen, new(position + 9, area.Height / 2), new(position + 4, area.Height / 2 + 6));
        }
    }
    private void Draw(DrawingContext context, IImage image, Rect viewport)
    {
        if (image.Size.Width <= 0 || image.Size.Height <= 0) return;
        var basis = Source!.Size;
        var scale = Math.Min(viewport.Width / basis.Width, viewport.Height / basis.Height) * _zoom;
        // Match geometry even when the compressed output has fewer pixels.
        var width = basis.Width * scale; var height = basis.Height * scale;
        var destination = new Rect(viewport.Center.X - width / 2 + _pan.X, viewport.Center.Y - height / 2 + _pan.Y, width, height);
        using (context.PushClip(viewport)) context.DrawImage(image, new Rect(image.Size), destination);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus(); _last = e.GetPosition(this); _dragging = true;
        _movingSplit = !SideBySide && (_zoom <= 1 || Math.Abs(_last.X - Bounds.Width * Split) < 20);
        if (_movingSplit) SetCurrentValue(SplitProperty, Math.Clamp(_last.X / Math.Max(1, Bounds.Width), 0, 1));
        e.Pointer.Capture(this); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_dragging)
        {
            if (_movingSplit) SetCurrentValue(SplitProperty, Math.Clamp(point.X / Math.Max(1, Bounds.Width), 0, 1));
            else { _pan += point - _last; InvalidateVisual(); }
            _last = point; e.Handled = true;
        }
        Cursor = !SideBySide && Math.Abs(point.X - Bounds.Width * Split) < 20 ? SplitCursor : _zoom > 1 ? PanCursor : DefaultCursor;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); _dragging = false; e.Pointer.Capture(null); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); _dragging = false; }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) { base.OnPointerWheelChanged(e); Zoom(e.Delta.Y > 0 ? 1.25 : .8); e.Handled = true; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Left or Key.Right) SetCurrentValue(SplitProperty, Math.Clamp(Split + (e.Key == Key.Left ? -.02 : .02), 0, 1));
        else if (e.Key == Key.Home) ResetView();
        else if (e.Key is Key.Add or Key.OemPlus) Zoom(1.25);
        else if (e.Key is Key.Subtract or Key.OemMinus) Zoom(.8);
        else return;
        e.Handled = true;
    }
}
