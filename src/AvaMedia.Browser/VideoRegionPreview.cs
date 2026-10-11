using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AvaMedia.Browser;

/// <summary>Draws square-pixel thumbnails; selection is relative to the displayed image, excluding letterbox margins.</summary>
internal sealed class VideoRegionPreview : Control
{
    private Bitmap? _image;
    private Rect _region;
    private Point? _anchor;
    public event Action<Rect>? RegionSelected;
    public bool CanSelect { get; set; }
    public int Rotation { get; set; }
    public bool Flip { get; set; }
    public Bitmap? Image { get => _image; set { _image = value; InvalidateVisual(); } }
    public Rect Region { get => _region; set { _region = value; InvalidateVisual(); } }
    private Rect ImageRect()
    {
        if (_image is null) return new();
        var quarterTurn = Rotation is 90 or 270;
        var scale = Math.Min(Bounds.Width / (quarterTurn ? _image.Size.Height : _image.Size.Width), Bounds.Height / (quarterTurn ? _image.Size.Width : _image.Size.Height));
        var size = new Size(_image.Size.Width * scale, _image.Size.Height * scale);
        return new((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2, size.Width, size.Height);
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        if (_image is null) return;
        var target = ImageRect();
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        using var transform = context.PushTransform(Matrix.CreateTranslation(-center.X, -center.Y)
            * Matrix.CreateRotation(Rotation * Math.PI / 180) * Matrix.CreateScale(Flip ? -1 : 1, 1)
            * Matrix.CreateTranslation(center.X, center.Y));
        context.DrawImage(_image, new Rect(_image.Size), target);
        if (_region.Width > 0 && _region.Height > 0)
        {
            using var clip = context.PushClip(target);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(35, 40, 180, 250)), new Pen(Brushes.DeepSkyBlue, 2),
                new Rect(target.X + _region.X * target.Width, target.Y + _region.Y * target.Height, _region.Width * target.Width, _region.Height * target.Height));
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!CanSelect || _image is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !ImageRect().Contains(e.GetPosition(this))) return;
        _anchor = Normalize(e.GetPosition(this)); e.Pointer.Capture(this); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_anchor is not { } start) return;
        var end = Normalize(e.GetPosition(this)); Region = new Rect(start, end).Normalize();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_anchor is null) return;
        _anchor = null; e.Pointer.Capture(null); RegionSelected?.Invoke(_region); e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { _anchor = null; base.OnPointerCaptureLost(e); }
    private Point Normalize(Point point)
    {
        var rect = ImageRect();
        return new(Math.Clamp((point.X - rect.X) / rect.Width, 0, 1), Math.Clamp((point.Y - rect.Y) / rect.Height, 0, 1));
    }
}
