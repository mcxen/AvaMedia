using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using AvaMedia.Core;
using SkiaSharp;

namespace AvaMedia.Desktop.Controls;

public sealed class PanoramaView : Control, IDisposable
{
    private SKImage? _frame;
    private PanoramaSettings _view = new();
    private IPointer? _pointer;
    private Point _lastPoint;
    private int _generation;
    private bool _errorReported, _disposed;
    public event Action? ViewChanged;
    public event Action<Exception>? RenderFailed;
    public bool HasFrame => _frame is not null;
    public bool IsDragging => _pointer is not null;
    public PanoramaSettings View
    {
        get => _view;
        set { var next = value.Normalize(); if (_view == next) return; _view = next; InvalidateVisual(); ViewChanged?.Invoke(); }
    }

    public PanoramaView() { ClipToBounds = true; Focusable = true; }

    public void SetFrame(Bitmap frame)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) return;
        SKImage image;
        var size = frame.PixelSize;
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        if (frame is WriteableBitmap writable)
        {
            using var buffer = writable.Lock();
            image = SKImage.FromPixelCopy(info, buffer.Address, buffer.RowBytes);
        }
        else
        {
            using var pixels = new SKBitmap(info);
            using var buffer = new LockedFramebuffer(pixels.GetPixels(), size, pixels.RowBytes, new Vector(96, 96), PixelFormat.Bgra8888, () => { });
            frame.CopyPixels(buffer, AlphaFormat.Opaque);
            using var pixmap = pixels.PeekPixels(); image = SKImage.FromPixelCopy(pixmap);
        }
        var old = _frame; _frame = image; old?.Dispose(); InvalidateVisual();
    }

    public void ClearFrame()
    {
        _generation++; _errorReported = false; CancelDrag();
        _frame?.Dispose(); _frame = null; InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_disposed || _frame is null || !View.IsImmersive || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        try
        {
            var generation = _generation;
            context.Custom(new PanoramaDraw(new Rect(Bounds.Size), PanoramaRenderer.CreateShader(_frame, View, (float)Bounds.Width,
                (float)Bounds.Height), error => ReportError(error, generation)));
        }
        catch (Exception error) { ReportError(error, _generation); }
    }

    private void ReportError(Exception error, int generation) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || generation != _generation || _errorReported || !View.IsImmersive) return;
        _errorReported = true; RenderFailed?.Invoke(error);
    });

    public void SaveView(Stream output, double renderScale)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_frame is null || !View.IsImmersive || Bounds.Width <= 0 || Bounds.Height <= 0) throw new InvalidOperationException("没有可截取的 VR 画面。");
        var scale = Math.Min(Math.Clamp(renderScale, 1, 3), Math.Min(3840 / Bounds.Width, 2160 / Bounds.Height));
        var width = Math.Max(1, (int)Math.Round(Bounds.Width * scale)); var height = Math.Max(1, (int)Math.Round(Bounds.Height * scale));
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var shader = PanoramaRenderer.CreateShader(_frame, View, width, height);
        using var paint = new SKPaint { Shader = shader, FilterQuality = SKFilterQuality.Low };
        surface.Canvas.DrawRect(0, 0, width, height, paint);
        using var snapshot = surface.Snapshot(); using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100); data.SaveTo(output);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!View.IsImmersive || _frame is null || _pointer is not null || e.ClickCount == 2 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus(NavigationMethod.Pointer); _pointer = e.Pointer; _lastPoint = e.GetPosition(this);
        e.Pointer.Capture(this); Cursor = new(StandardCursorType.SizeAll); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pointer != e.Pointer) return;
        var point = e.GetPosition(this); var delta = point - _lastPoint; _lastPoint = point;
        var degrees = View.FieldOfView / Math.Max(1, Bounds.Height);
        View = e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            ? View with { Roll = View.Roll + delta.X * degrees }
            : View with { Yaw = View.Yaw - delta.X * degrees, Pitch = View.Pitch + delta.Y * degrees };
        e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    { base.OnPointerReleased(e); if (_pointer != e.Pointer) return; CancelDrag(); e.Handled = true; }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    { base.OnPointerCaptureLost(e); _pointer = null; Cursor = null; }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!View.IsImmersive || _frame is null) return;
        View = View with { FieldOfView = View.FieldOfView - e.Delta.Y * 5 }; e.Handled = true;
    }
    public void CancelDrag() { var pointer = _pointer; _pointer = null; pointer?.Capture(null); Cursor = null; }
    public void Dispose() { if (_disposed) return; _disposed = true; ClearFrame(); }

    private sealed class PanoramaDraw(Rect bounds, SKShader shader, Action<Exception> failed) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;
        private readonly SKPaint _paint = new() { Shader = shader, FilterQuality = SKFilterQuality.Low };
        public void Render(ImmediateDrawingContext context)
        {
            try
            {
                var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>() ?? throw new NotSupportedException("VR requires the Skia renderer.");
                using var lease = feature.Lease();
                lease.SkCanvas.DrawRect(0, 0, (float)Bounds.Width, (float)Bounds.Height, _paint);
            }
            catch (Exception error) { failed(error); }
        }
        public bool HitTest(Point point) => Bounds.Contains(point);
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { _paint.Dispose(); shader.Dispose(); }
    }
}
