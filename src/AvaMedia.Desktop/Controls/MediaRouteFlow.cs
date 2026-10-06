using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace AvaMedia.Desktop.Controls;

public sealed record MediaRoutePort(string Key, Point Position);

/// <summary>Dashed tool connections with hover feedback following shared motion preferences.</summary>
public sealed class MediaRouteFlow : Control
{
    public static readonly StyledProperty<IBrush?> LineBrushProperty = AvaloniaProperty.Register<MediaRouteFlow, IBrush?>(nameof(LineBrush));
    public static readonly StyledProperty<IBrush?> AccentBrushProperty = AvaloniaProperty.Register<MediaRouteFlow, IBrush?>(nameof(AccentBrush));
    public static readonly StyledProperty<IBrush?> SurfaceBrushProperty = AvaloniaProperty.Register<MediaRouteFlow, IBrush?>(nameof(SurfaceBrush));
    public static readonly StyledProperty<IReadOnlyList<MediaRoutePort>> PortsProperty = AvaloniaProperty.Register<MediaRouteFlow, IReadOnlyList<MediaRoutePort>>(nameof(Ports), []);
    public static readonly StyledProperty<string> ActiveKeyProperty = AvaloniaProperty.Register<MediaRouteFlow, string>(nameof(ActiveKey), "");
    public static readonly StyledProperty<bool> HighlightedProperty = AvaloniaProperty.Register<MediaRouteFlow, bool>(nameof(Highlighted));
    public static readonly StyledProperty<TimeSpan> ActivateDurationProperty = AvaloniaProperty.Register<MediaRouteFlow, TimeSpan>(nameof(ActivateDuration), TimeSpan.FromMilliseconds(160));
    public static readonly StyledProperty<TimeSpan> FadeDurationProperty = AvaloniaProperty.Register<MediaRouteFlow, TimeSpan>(nameof(FadeDuration), TimeSpan.FromMilliseconds(120));
    public static readonly StyledProperty<int> InputCountProperty = AvaloniaProperty.Register<MediaRouteFlow, int>(nameof(InputCount));
    public static readonly StyledProperty<int> AcceptedCountProperty = AvaloniaProperty.Register<MediaRouteFlow, int>(nameof(AcceptedCount));
    public IBrush? LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? SurfaceBrush { get => GetValue(SurfaceBrushProperty); set => SetValue(SurfaceBrushProperty, value); }
    public IReadOnlyList<MediaRoutePort> Ports { get => GetValue(PortsProperty); set => SetValue(PortsProperty, value); }
    public string ActiveKey { get => GetValue(ActiveKeyProperty); set => SetValue(ActiveKeyProperty, value); }
    public bool Highlighted { get => GetValue(HighlightedProperty); set => SetValue(HighlightedProperty, value); }
    public TimeSpan ActivateDuration { get => GetValue(ActivateDurationProperty); set => SetValue(ActivateDurationProperty, value); }
    public TimeSpan FadeDuration { get => GetValue(FadeDurationProperty); set => SetValue(FadeDurationProperty, value); }
    public int InputCount { get => GetValue(InputCountProperty); set => SetValue(InputCountProperty, value); }
    public int AcceptedCount { get => GetValue(AcceptedCountProperty); set => SetValue(AcceptedCountProperty, value); }
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1d / 60) };
    private readonly Stopwatch _clock = new();
    private readonly Dictionary<string, double> _levels = [];
    private readonly List<Wire> _inputs = [];
    private readonly Dictionary<string, Wire> _outputs = [];
    private Window? _window;
    private Size _cachedSize;
    private int _cachedCount = -1;
    private IReadOnlyList<MediaRoutePort>? _cachedPorts;
    private double _lastTick;
    private bool CanAnimate => _window is { IsVisible: true } window && window.WindowState != WindowState.Minimized
        && window.Classes.Contains("motion-enabled");
    private bool WantsHighlight => Highlighted && AcceptedCount > 0 && Ports.Any(port => port.Key == ActiveKey);

    static MediaRouteFlow() => AffectsRender<MediaRouteFlow>(LineBrushProperty, AccentBrushProperty, SurfaceBrushProperty,
        PortsProperty, ActiveKeyProperty, HighlightedProperty, InputCountProperty, AcceptedCountProperty);
    public MediaRouteFlow() { IsHitTestVisible = false; _timer.Tick += (_, _) => Tick(); }
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
        _timer.Stop(); _clock.Reset(); _levels.Clear(); _inputs.Clear(); _outputs.Clear(); _cachedPorts = null;
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
    { if (e.Property == IsVisibleProperty || e.Property == Window.WindowStateProperty) RefreshMotion(); }
    private void Stop() { _timer.Stop(); _clock.Stop(); }
    private void RefreshMotion()
    {
        var keys = Ports.Select(port => port.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _levels.Keys.Where(key => !keys.Contains(key)).ToArray()) _levels.Remove(key);
        foreach (var key in keys) _levels.TryAdd(key, 0);
        if (!CanAnimate)
        {
            Stop();
            foreach (var key in keys) _levels[key] = WantsHighlight && key == ActiveKey ? 1 : 0;
        }
        else if (WantsHighlight || _levels.Values.Any(level => level > .001))
        {
            if (!_timer.IsEnabled) { _clock.Start(); _lastTick = _clock.Elapsed.TotalSeconds; _timer.Start(); }
        }
        else Stop();
        InvalidateVisual();
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AcceptedCountProperty || change.Property == ActiveKeyProperty
            || change.Property == HighlightedProperty || change.Property == PortsProperty) RefreshMotion();
    }
    private void Tick()
    {
        if (!CanAnimate) { RefreshMotion(); return; }
        var now = _clock.Elapsed.TotalSeconds;
        var elapsed = Math.Clamp(now - _lastTick, 0, .1); _lastTick = now;
        var highlight = WantsHighlight;
        var attack = ActivateDuration.TotalSeconds / 3;
        var decay = FadeDuration.TotalSeconds / 3;
        foreach (var port in Ports)
        {
            var target = highlight && port.Key == ActiveKey ? 1d : 0d;
            var level = _levels[port.Key];
            var next = level + (target - level) * (1 - Math.Exp(-elapsed / (target == 1 ? attack : decay)));
            _levels[port.Key] = Math.Abs(next - target) < .001 ? target : next;
        }
        if (!highlight && _levels.Values.All(level => level == 0)) Stop();
        InvalidateVisual();
    }
    private void CacheWires(Point center)
    {
        if (_cachedSize == Bounds.Size && _cachedCount == InputCount && ReferenceEquals(_cachedPorts, Ports)) return;
        _cachedSize = Bounds.Size; _cachedCount = InputCount; _cachedPorts = Ports;
        _inputs.Clear(); _outputs.Clear();
        var count = Math.Min(5, InputCount);
        var span = Math.Min(Bounds.Height * .32, 160);
        for (var index = 0; index < count; index++)
        {
            var source = new Point(0, center.Y + (count == 1 ? 0 : (index / (count - 1d) - .5) * span));
            _inputs.Add(new(source, new(center.X * .45, source.Y), new(center.X * .5, center.Y), new(center.X - 17, center.Y)));
        }
        foreach (var port in Ports)
        {
            var start = new Point(center.X + 17, center.Y);
            var reach = (port.Position.X - start.X) * .5;
            _outputs[port.Key] = new(start, new(start.X + reach, center.Y),
                new(port.Position.X - reach, port.Position.Y), port.Position);
        }
    }
    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < 1 || Bounds.Height < 1) return;
        var center = new Point(50, Bounds.Height * .5);
        CacheWires(center);
        var dashed = new Pen(LineBrush, 1, new DashStyle([2.5, 4.5], 0), PenLineCap.Round);
        var solid = new Pen(AccentBrush, 1.5, lineCap: PenLineCap.Round);
        var outline = new Pen(LineBrush, 1.2);
        var level = Math.Clamp(_levels.Values.Sum(), 0, 1);
        for (var index = 0; index < _inputs.Count; index++)
            DrawWire(_inputs[index], index < AcceptedCount ? level : 0, index * 23);
        foreach (var port in Ports)
        {
            var activation = _levels.GetValueOrDefault(port.Key);
            DrawWire(_outputs[port.Key], activation, 0);
            context.DrawEllipse(SurfaceBrush, outline, port.Position, 2.5, 2.5);
            using (context.PushOpacity(activation)) context.DrawEllipse(AccentBrush, null, port.Position, 2.5, 2.5);
        }
        DrawHub(outline);
        using (context.PushOpacity(level)) DrawHub(solid);
        context.DrawEllipse(AccentBrush, null, new(center.X - 6, center.Y - 5), 2.5, 2.5);
        context.DrawEllipse(AccentBrush, null, new(center.X - 6, center.Y + 5), 2.5, 2.5);
        context.DrawEllipse(AccentBrush, null, new(center.X + 7, center.Y), 2.5, 2.5);
        context.DrawLine(new Pen(AccentBrush, 1.2), new(center.X - 3, center.Y - 5), new(center.X + 4, center.Y));
        context.DrawLine(new Pen(AccentBrush, 1.2), new(center.X - 3, center.Y + 5), new(center.X + 4, center.Y));

        void DrawHub(IPen pen)
        {
            if (_window?.ActualThemeVariant == Skin.MacOS9) context.DrawRectangle(SurfaceBrush, pen, new Rect(center.X - 17, center.Y - 17, 34, 34));
            else context.DrawEllipse(SurfaceBrush, pen, center, 17, 17);
        }
        void DrawWire(Wire wire, double activation, double offset)
        {
            using (context.PushOpacity(1 - activation)) context.DrawGeometry(null, dashed, wire.Geometry);
            if (activation <= 0) return;
            using (context.PushOpacity(activation)) context.DrawGeometry(null, solid, wire.Geometry);
            if (!_timer.IsEnabled || wire.Length < 1) return;
            var distance = (_clock.Elapsed.TotalSeconds * 85 + offset) % wire.Length;
            for (var tail = 2; tail >= 0; tail--)
            {
                var position = distance - tail * 6;
                if (position < 0) continue;
                var fade = Math.Clamp(Math.Min(position, wire.Length - position) / 9, 0, 1);
                var opacity = activation * fade * (tail == 0 ? .95 : tail == 1 ? .25 : .09);
                using (context.PushOpacity(opacity))
                    context.DrawEllipse(AccentBrush, null, wire.AtDistance(position), tail == 0 ? 2.6 : 2, tail == 0 ? 2.6 : 2);
            }
        }
    }

    private sealed class Wire
    {
        private readonly Point[] _points = new Point[33];
        private readonly double[] _distances = new double[33];
        public StreamGeometry Geometry { get; } = new();
        public double Length => _distances[^1];
        public Wire(Point start, Point first, Point second, Point end)
        {
            using (var path = Geometry.Open()) { path.BeginFigure(start, false); path.CubicBezierTo(first, second, end); path.EndFigure(false); }
            for (var index = 0; index < _points.Length; index++)
            {
                var t = index / (double)(_points.Length - 1); var inverse = 1 - t;
                _points[index] = new(inverse * inverse * inverse * start.X + 3 * inverse * inverse * t * first.X + 3 * inverse * t * t * second.X + t * t * t * end.X,
                    inverse * inverse * inverse * start.Y + 3 * inverse * inverse * t * first.Y + 3 * inverse * t * t * second.Y + t * t * t * end.Y);
                if (index > 0)
                {
                    var segment = _points[index] - _points[index - 1];
                    _distances[index] = _distances[index - 1] + Math.Sqrt(segment.X * segment.X + segment.Y * segment.Y);
                }
            }
        }
        public Point AtDistance(double distance)
        {
            var index = Array.BinarySearch(_distances, distance);
            if (index >= 0) return _points[index];
            index = Math.Clamp(~index, 1, _points.Length - 1);
            var span = _distances[index] - _distances[index - 1];
            var fraction = span > 0 ? (distance - _distances[index - 1]) / span : 0;
            return _points[index - 1] + (_points[index] - _points[index - 1]) * fraction;
        }
    }
}
