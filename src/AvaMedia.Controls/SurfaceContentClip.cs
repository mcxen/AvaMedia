using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace AvaMedia.Controls;

/// <summary>Clips a rounded surface's child without clipping the surface shadow.</summary>
public sealed class SurfaceContentClip : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<SurfaceContentClip, Border, bool>("IsEnabled");
    private static readonly AttachedProperty<State?> StateProperty =
        AvaloniaProperty.RegisterAttached<SurfaceContentClip, Border, State?>("State");
    public static bool GetIsEnabled(Border border) => border.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Border border, bool value) => border.SetValue(IsEnabledProperty, value);
    static SurfaceContentClip() => IsEnabledProperty.Changed.AddClassHandler<Border>((border, _) =>
    {
        border.GetValue(StateProperty)?.Dispose();
        border.SetValue(StateProperty, GetIsEnabled(border) ? new State(border) : null);
    });

    private sealed class State : IDisposable
    {
        private readonly Border _border;
        private Control? _child;
        private IDisposable? _clip;
        private Size _size;
        private CornerRadius _radius;
        private Matrix _transform;
        private Geometry? _baseClip;
        public State(Border border)
        {
            _border = border;
            border.LayoutUpdated += Layout;
            border.PropertyChanged += Changed;
            border.DetachedFromVisualTree += Detached;
        }
        private void Changed(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Decorator.ChildProperty) Restore();
        }
        private void Layout(object? sender, EventArgs e)
        {
            var child = _border.Child;
            if (child is null || !_border.IsAttachedToVisualTree()) { Restore(); return; }
            if (_child != child) { Restore(); _child = child; }
            var transform = _border.TransformToVisual(child);
            if (transform is null) return;
            var size = _border.Bounds.Size;
            var radius = _border.CornerRadius;
            var baseClip = child.GetBaseValue(Visual.ClipProperty).GetValueOrDefault();
            if (_clip is not null && size == _size && radius == _radius
                && transform.Value == _transform && ReferenceEquals(baseClip, _baseClip)) return;
            _size = size; _radius = radius; _transform = transform.Value; _baseClip = baseClip;
            Geometry shape = Shape(size, radius);
            shape.Transform = new MatrixTransform(transform.Value);
            if (baseClip is not null) shape = new CombinedGeometry(GeometryCombineMode.Intersect, baseClip, shape);
            var previous = _clip;
            _clip = child.SetValue(Visual.ClipProperty, shape, BindingPriority.Animation);
            previous?.Dispose();
        }
        private void Detached(object? sender, VisualTreeAttachmentEventArgs e) => Restore();
        private void Restore() { _clip?.Dispose(); _clip = null; _child = null; _baseClip = null; }
        public void Dispose()
        {
            _border.LayoutUpdated -= Layout; _border.PropertyChanged -= Changed;
            _border.DetachedFromVisualTree -= Detached; Restore();
        }
    }

    private static Geometry Shape(Size size, CornerRadius radius)
    {
        var w = Math.Max(0, size.Width); var h = Math.Max(0, size.Height);
        var tl = radius.TopLeft; var tr = radius.TopRight; var br = radius.BottomRight; var bl = radius.BottomLeft;
        var scale = 1d;
        void Limit(double length, double sum) { if (sum > 0) scale = Math.Min(scale, length / sum); }
        Limit(w, tl + tr); Limit(w, bl + br); Limit(h, tl + bl); Limit(h, tr + br);
        tl *= scale; tr *= scale; br *= scale; bl *= scale;
        var shape = new StreamGeometry();
        using var g = shape.Open();
        void Corner(Point point, double r)
        {
            if (r > 0) g.ArcTo(point, new Size(r, r), 0, false, SweepDirection.Clockwise);
            else g.LineTo(point);
        }
        g.BeginFigure(new Point(tl, 0), true);
        g.LineTo(new Point(w - tr, 0)); Corner(new Point(w, tr), tr);
        g.LineTo(new Point(w, h - br)); Corner(new Point(w - br, h), br);
        g.LineTo(new Point(bl, h)); Corner(new Point(0, h - bl), bl);
        g.LineTo(new Point(0, tl)); Corner(new Point(tl, 0), tl);
        g.EndFigure(true);
        return shape;
    }
}
