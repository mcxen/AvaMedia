using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace AvaMedia.Desktop.Controls;

/// <summary>Decorative connections to the active tool; animation follows shared motion preferences.</summary>
public sealed class MediaRouteFlow : Control
{
    public static readonly StyledProperty<IBrush?> LineBrushProperty = AvaloniaProperty.Register<MediaRouteFlow, IBrush?>(nameof(LineBrush));
    public static readonly StyledProperty<IBrush?> AccentBrushProperty = AvaloniaProperty.Register<MediaRouteFlow, IBrush?>(nameof(AccentBrush));
    public static readonly StyledProperty<IBrush?> SurfaceBrushProperty = AvaloniaProperty.Register<MediaRouteFlow, IBrush?>(nameof(SurfaceBrush));
    public static readonly StyledProperty<double> TargetYProperty = AvaloniaProperty.Register<MediaRouteFlow, double>(nameof(TargetY), double.NaN);
    public static readonly StyledProperty<int> InputCountProperty = AvaloniaProperty.Register<MediaRouteFlow, int>(nameof(InputCount));
    public static readonly StyledProperty<int> AcceptedCountProperty = AvaloniaProperty.Register<MediaRouteFlow, int>(nameof(AcceptedCount));
    public IBrush? LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? SurfaceBrush { get => GetValue(SurfaceBrushProperty); set => SetValue(SurfaceBrushProperty, value); }
    public double TargetY { get => GetValue(TargetYProperty); set => SetValue(TargetYProperty, value); }
    public int InputCount { get => GetValue(InputCountProperty); set => SetValue(InputCountProperty, value); }
    public int AcceptedCount { get => GetValue(AcceptedCountProperty); set => SetValue(AcceptedCountProperty, value); }
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Stopwatch _clock = new();
    private Window? _window;

    static MediaRouteFlow() => AffectsRender<MediaRouteFlow>(LineBrushProperty, AccentBrushProperty, SurfaceBrushProperty,
        TargetYProperty, InputCountProperty, AcceptedCountProperty);
    public MediaRouteFlow() { IsHitTestVisible = false; _timer.Tick += (_, _) => InvalidateVisual(); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
        {
            _window.Classes.CollectionChanged += ClassesChanged;
            _window.PropertyChanged += WindowChanged;
        }
        RefreshMotion();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop(); _clock.Reset();
        if (_window is not null)
        {
            _window.Classes.CollectionChanged -= ClassesChanged;
            _window.PropertyChanged -= WindowChanged;
        }
        _window = null;
        base.OnDetachedFromVisualTree(e);
    }
    private void ClassesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshMotion();
    private void WindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    { if (e.Property == IsVisibleProperty) RefreshMotion(); }
    private void RefreshMotion()
    {
        if (_window is { IsVisible: true } && _window.Classes.Contains("motion-enabled") && AcceptedCount > 0)
        { _clock.Start(); _timer.Start(); }
        else { _timer.Stop(); _clock.Reset(); }
        InvalidateVisual();
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AcceptedCountProperty) RefreshMotion();
    }
    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < 1 || Bounds.Height < 1) return;
        var center = new Point(Bounds.Width * .5, Bounds.Height * .5);
        var count = Math.Min(5, InputCount);
        var span = Math.Min(Bounds.Height * .32, 160);
        for (var index = 0; index < count; index++)
        {
            var source = new Point(0, center.Y + (count == 1 ? 0 : (index / (count - 1d) - .5) * span));
            DrawWire(source, new(center.X * .45, source.Y), new(center.X * .5, center.Y), new(center.X - 17, center.Y), index < AcceptedCount, index * .19);
        }
        if (AcceptedCount > 0 && double.IsFinite(TargetY))
            DrawWire(new(center.X + 17, center.Y), new(center.X + 35, center.Y),
                new(Bounds.Width - 20, Math.Clamp(TargetY, 12, Bounds.Height - 12)),
                new(Bounds.Width, Math.Clamp(TargetY, 12, Bounds.Height - 12)), true, 0);
        var border = new Pen(AcceptedCount > 0 ? AccentBrush : LineBrush, 1.5);
        if (_window?.ActualThemeVariant == Skin.MacOS9) context.DrawRectangle(SurfaceBrush, border, new Rect(center.X - 17, center.Y - 17, 34, 34));
        else context.DrawEllipse(SurfaceBrush, border, center, 17, 17);
        context.DrawEllipse(AccentBrush, null, new(center.X - 6, center.Y - 5), 2.5, 2.5);
        context.DrawEllipse(AccentBrush, null, new(center.X - 6, center.Y + 5), 2.5, 2.5);
        context.DrawEllipse(AccentBrush, null, new(center.X + 7, center.Y), 2.5, 2.5);
        context.DrawLine(new Pen(AccentBrush, 1.2), new(center.X - 3, center.Y - 5), new(center.X + 4, center.Y));
        context.DrawLine(new Pen(AccentBrush, 1.2), new(center.X - 3, center.Y + 5), new(center.X + 4, center.Y));

        void DrawWire(Point start, Point first, Point second, Point end, bool active, double offset)
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open()) { path.BeginFigure(start, false); path.CubicBezierTo(first, second, end); path.EndFigure(false); }
            context.DrawGeometry(null, new Pen(active ? AccentBrush : LineBrush, active ? 1.5 : 1), geometry);
            if (!active || !_timer.IsEnabled) return;
            var t = (_clock.Elapsed.TotalSeconds * .55 + offset) % 1;
            var inverse = 1 - t;
            var point = new Point(inverse * inverse * inverse * start.X + 3 * inverse * inverse * t * first.X + 3 * inverse * t * t * second.X + t * t * t * end.X,
                inverse * inverse * inverse * start.Y + 3 * inverse * inverse * t * first.Y + 3 * inverse * t * t * second.Y + t * t * t * end.Y);
            context.DrawEllipse(AccentBrush, null, point, 3, 3);
        }
    }
}
