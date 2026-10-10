using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AvaMedia.Desktop.Controls;

/// <summary>A single camera for fit, actual size, cursor-anchored zoom, panning and rotated spreads.</summary>
public sealed class ImageViewport : Control
{
    private Bitmap? _first, _second;
    private Vector _pan;
    private Point? _drag;
    private IPointer? _pointer;
    private double _zoom = 1;
    private int _rotation;
    private bool _flipped;
    public event Action? ViewChanged;
    public double Zoom => _zoom;
    public int Rotation => _rotation;
    public bool Flipped => _flipped;
    public string FitMode { get; private set; } = "fit";
    private double PictureWidth => (_first?.PixelSize.Width ?? 0) + (_second?.PixelSize.Width ?? 0);
    private double PictureHeight => Math.Max(_first?.PixelSize.Height ?? 0, _second?.PixelSize.Height ?? 0);
    public ImageViewport()
    {
        Focusable = true; ClipToBounds = true;
        PointerWheelChanged += (_, e) =>
        {
            if (_first is null) return;
            ZoomAt(_zoom * Math.Pow(1.2, e.Delta.Y), e.GetPosition(this)); e.Handled = true;
        };
        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _pointer is not null) return;
            Focus(); _drag = e.GetPosition(this); _pointer = e.Pointer; e.Pointer.Capture(this); e.Handled = true;
        };
        PointerMoved += (_, e) =>
        {
            if (_drag is not { } start || e.Pointer != _pointer) return;
            var now = e.GetPosition(this); _pan += now - start; _drag = now; FitMode = "custom";
            InvalidateVisual(); e.Handled = true;
        };
        PointerReleased += (_, e) => { if (_pointer == e.Pointer) { _pointer = null; _drag = null; e.Pointer.Capture(null); } };
        PointerCaptureLost += (_, _) => { _pointer = null; _drag = null; };
        DoubleTapped += (_, e) => { Fit(FitMode == "actual" ? "fit" : "actual"); e.Handled = true; };
        SizeChanged += (_, _) => { if (FitMode != "custom") Fit(FitMode); };
    }
    public void SetImages(Bitmap? first, Bitmap? second = null, bool reset = true)
    {
        _first = first; _second = second;
        if (reset) { _rotation = 0; _flipped = false; _pan = default; }
        if (reset || FitMode != "custom") Fit(FitMode == "custom" ? "fit" : FitMode);
        else InvalidateVisual();
    }
    public void Fit(string mode)
    {
        FitMode = mode; _pan = default;
        var swapped = _rotation % 180 != 0;
        var width = swapped ? PictureHeight : PictureWidth; var height = swapped ? PictureWidth : PictureHeight;
        if (width <= 0 || height <= 0) { InvalidateVisual(); return; }
        var fit = Math.Min(Math.Max(1, Bounds.Width - 16) / width, Math.Max(1, Bounds.Height - 16) / height);
        _zoom = mode switch { "actual" => 1, "down" => Math.Min(1, fit), "width" => Math.Max(1, Bounds.Width - 16) / width, _ => fit };
        InvalidateVisual(); ViewChanged?.Invoke();
    }
    public void ZoomBy(double ratio) => ZoomAt(_zoom * ratio, new Point(Bounds.Width / 2, Bounds.Height / 2));
    public void ZoomAt(double value, Point anchor)
    {
        if (_first is null) return;
        value = Math.Clamp(value, .01, 32);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var relative = anchor - center - _pan;
        _pan = anchor - center - relative * (value / _zoom);
        _zoom = value; FitMode = "custom"; InvalidateVisual(); ViewChanged?.Invoke();
    }
    public void Rotate(int degrees)
    {
        _rotation = ((_rotation + degrees) % 360 + 360) % 360;
        if (FitMode != "custom") Fit(FitMode); else { InvalidateVisual(); ViewChanged?.Invoke(); }
    }
    public void Flip() { _flipped = !_flipped; InvalidateVisual(); ViewChanged?.Invoke(); }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_first is null) return;
        var transform = Matrix.CreateTranslation(-PictureWidth / 2, -PictureHeight / 2)
            * Matrix.CreateScale(_flipped ? -1 : 1, 1) * Matrix.CreateRotation(_rotation * Math.PI / 180)
            * Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(Bounds.Width / 2 + _pan.X, Bounds.Height / 2 + _pan.Y);
        using (context.PushTransform(transform))
        {
            context.DrawImage(_first, new Rect(0, (PictureHeight - _first.PixelSize.Height) / 2, _first.PixelSize.Width, _first.PixelSize.Height));
            if (_second is not null) context.DrawImage(_second,
                new Rect(_first.PixelSize.Width, (PictureHeight - _second.PixelSize.Height) / 2, _second.PixelSize.Width, _second.PixelSize.Height));
        }
    }
}
