using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AvaMedia.Browser;

/// <summary>Draws square-pixel thumbnails. Editing gestures belong to the shared CropOverlay.</summary>
internal sealed class VideoRegionPreview : Control
{
    private Bitmap? _image;
    public int Rotation { get; set; }
    public bool Flip { get; set; }
    public Bitmap? Image { get => _image; set { _image = value; InvalidateVisual(); } }
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
    }
}
