using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace AvaMedia.Desktop.Controls;

public sealed record MediaRoutePort(string Key, Point Position);
public sealed record MediaRouteInputPort(string Key, Point Position, bool Accepted);

/// <summary>Sources and tool groups share a routing trunk confined to its own lane.</summary>
public sealed class MediaRouteFlow : Control
{
    public static readonly StyledProperty<IBrush?> LineBrushProperty = AvaloniaProperty.Register<MediaRouteFlow, IBrush?>(nameof(LineBrush));
    public static readonly StyledProperty<IBrush?> AccentBrushProperty = AvaloniaProperty.Register<MediaRouteFlow, IBrush?>(nameof(AccentBrush));
    public static readonly StyledProperty<IBrush?> SurfaceBrushProperty = AvaloniaProperty.Register<MediaRouteFlow, IBrush?>(nameof(SurfaceBrush));
    public static readonly StyledProperty<IReadOnlyList<MediaRoutePort>> PortsProperty = AvaloniaProperty.Register<MediaRouteFlow, IReadOnlyList<MediaRoutePort>>(nameof(Ports), []);
    public static readonly StyledProperty<IReadOnlyList<MediaRouteInputPort>> InputsProperty = AvaloniaProperty.Register<MediaRouteFlow, IReadOnlyList<MediaRouteInputPort>>(nameof(Inputs), []);
    public static readonly StyledProperty<string> ActiveKeyProperty = AvaloniaProperty.Register<MediaRouteFlow, string>(nameof(ActiveKey), "");
    public static readonly StyledProperty<bool> HighlightedProperty = AvaloniaProperty.Register<MediaRouteFlow, bool>(nameof(Highlighted));
    public static readonly StyledProperty<bool> AnimateFeedbackProperty = AvaloniaProperty.Register<MediaRouteFlow, bool>(nameof(AnimateFeedback));
    public static readonly StyledProperty<TimeSpan> ActivateDurationProperty = AvaloniaProperty.Register<MediaRouteFlow, TimeSpan>(nameof(ActivateDuration), TimeSpan.FromMilliseconds(160));
    public static readonly StyledProperty<TimeSpan> FadeDurationProperty = AvaloniaProperty.Register<MediaRouteFlow, TimeSpan>(nameof(FadeDuration), TimeSpan.FromMilliseconds(120));
    public static readonly StyledProperty<int> AcceptedCountProperty = AvaloniaProperty.Register<MediaRouteFlow, int>(nameof(AcceptedCount));
    public IBrush? LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? SurfaceBrush { get => GetValue(SurfaceBrushProperty); set => SetValue(SurfaceBrushProperty, value); }
    public IReadOnlyList<MediaRoutePort> Ports { get => GetValue(PortsProperty); set => SetValue(PortsProperty, value); }
    public IReadOnlyList<MediaRouteInputPort> Inputs { get => GetValue(InputsProperty); set => SetValue(InputsProperty, value); }
    public string ActiveKey { get => GetValue(ActiveKeyProperty); set => SetValue(ActiveKeyProperty, value); }
    public bool Highlighted { get => GetValue(HighlightedProperty); set => SetValue(HighlightedProperty, value); }
    public bool AnimateFeedback { get => GetValue(AnimateFeedbackProperty); set => SetValue(AnimateFeedbackProperty, value); }
    public TimeSpan ActivateDuration { get => GetValue(ActivateDurationProperty); set => SetValue(ActivateDurationProperty, value); }
    public TimeSpan FadeDuration { get => GetValue(FadeDurationProperty); set => SetValue(FadeDurationProperty, value); }
    public int AcceptedCount { get => GetValue(AcceptedCountProperty); set => SetValue(AcceptedCountProperty, value); }
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1d / 60) };
    private readonly Stopwatch _clock = new();
    private readonly Dictionary<string, double> _levels = [];
    private readonly Dictionary<string, Wire> _inputs = [], _outputs = [];
    private Window? _window;
    private Size _cachedSize;
    private IReadOnlyList<MediaRoutePort>? _cachedPorts;
    private IReadOnlyList<MediaRouteInputPort>? _cachedInputs;
    private Wire? _trunk;
    private double _lastTick;
    private double TrunkX => Bounds.Width * .45;
    private bool CanAnimate => AnimateFeedback && _window is { IsVisible: true } window && window.WindowState != WindowState.Minimized
        && window.Classes.Contains("motion-enabled");
    private bool WantsHighlight => Highlighted && AcceptedCount > 0 && Ports.Any(port => port.Key == ActiveKey);

    static MediaRouteFlow() => AffectsRender<MediaRouteFlow>(LineBrushProperty, AccentBrushProperty, SurfaceBrushProperty,
        PortsProperty, InputsProperty, ActiveKeyProperty, HighlightedProperty, AcceptedCountProperty);
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
        _timer.Stop(); _clock.Reset(); _levels.Clear(); _inputs.Clear(); _outputs.Clear(); _trunk = null;
        _cachedPorts = null; _cachedInputs = null;
        if (_window is not null)
        {
            _window.Classes.CollectionChanged -= ClassesChanged;
            _window.PropertyChanged -= WindowChanged;
        }
        _window = null; base.OnDetachedFromVisualTree(e);
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
        if (change.Property == AcceptedCountProperty || change.Property == ActiveKeyProperty || change.Property == AnimateFeedbackProperty
            || change.Property == HighlightedProperty || change.Property == PortsProperty || change.Property == InputsProperty) RefreshMotion();
    }
    private void Tick()
    {
        if (!CanAnimate) { RefreshMotion(); return; }
        var now = _clock.Elapsed.TotalSeconds;
        var elapsed = Math.Clamp(now - _lastTick, 0, .1); _lastTick = now;
        foreach (var port in Ports)
        {
            var target = WantsHighlight && port.Key == ActiveKey ? 1d : 0d;
            var duration = Math.Max(.001, (target == 1 ? ActivateDuration : FadeDuration).TotalSeconds / 3);
            var next = _levels[port.Key] + (target - _levels[port.Key]) * (1 - Math.Exp(-elapsed / duration));
            _levels[port.Key] = Math.Abs(next - target) < .001 ? target : next;
        }
        if (!WantsHighlight && _levels.Values.All(level => level == 0)) Stop();
        InvalidateVisual();
    }
    private void CacheWires()
    {
        if (_cachedSize == Bounds.Size && ReferenceEquals(_cachedPorts, Ports) && ReferenceEquals(_cachedInputs, Inputs)) return;
        _cachedSize = Bounds.Size; _cachedPorts = Ports; _cachedInputs = Inputs;
        _inputs.Clear(); _outputs.Clear(); _trunk = null;
        foreach (var input in Inputs) _inputs[input.Key] = new(input.Position, new(TrunkX, input.Position.Y));
        foreach (var port in Ports) _outputs[port.Key] = new(new(TrunkX, port.Position.Y), port.Position);
        var positions = Inputs.Select(input => input.Position.Y).Concat(Ports.Select(port => port.Position.Y)).ToArray();
        if (positions.Length > 0) _trunk = new(new(TrunkX, positions.Min()), new(TrunkX, positions.Max()));
    }
    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < 1 || Bounds.Height < 1) return;
        CacheWires();
        var dashed = new Pen(LineBrush, 1, new DashStyle([2.5, 4.5], 0), PenLineCap.Round);
        var solid = new Pen(AccentBrush, 1.5, lineCap: PenLineCap.Round);
        var outline = new Pen(LineBrush, 1);
        var level = Math.Clamp(_levels.Values.Sum(), 0, 1);
        if (_trunk is not null) context.DrawGeometry(null, dashed, _trunk.Geometry);
        foreach (var input in Inputs) DrawWire(_inputs[input.Key], input.Accepted ? level : 0, 0);
        foreach (var port in Ports)
        {
            var activation = _levels.GetValueOrDefault(port.Key);
            DrawWire(_outputs[port.Key], activation, 0);
            var accepted = Inputs.Where(input => input.Accepted).Select(input => input.Position.Y).Append(port.Position.Y).ToArray();
            if (activation > 0)
            {
                using (context.PushOpacity(activation)) context.DrawLine(solid, new(TrunkX, accepted.Min()), new(TrunkX, accepted.Max()));
            }
            DrawNode(new(TrunkX, port.Position.Y), activation); DrawNode(port.Position, activation);
        }
        foreach (var input in Inputs) DrawNode(new(TrunkX, input.Position.Y), input.Accepted ? level : 0);

        void DrawNode(Point point, double activation)
        {
            if (_window?.ActualThemeVariant == Skin.MacOS9) context.DrawRectangle(SurfaceBrush, outline, new Rect(point.X - 2.5, point.Y - 2.5, 5, 5));
            else context.DrawEllipse(SurfaceBrush, outline, point, 2.5, 2.5);
            using (context.PushOpacity(activation)) context.DrawEllipse(AccentBrush, null, point, 2, 2);
        }
        void DrawWire(Wire wire, double activation, double offset)
        {
            using (context.PushOpacity(1 - activation)) context.DrawGeometry(null, dashed, wire.Geometry);
            if (activation <= 0) return;
            using (context.PushOpacity(activation)) context.DrawGeometry(null, solid, wire.Geometry);
            if (!_timer.IsEnabled || wire.Length < 1) return;
            var distance = (_clock.Elapsed.TotalSeconds * 85 + offset) % wire.Length;
            var fade = Math.Clamp(Math.Min(distance, wire.Length - distance) / 6, 0, 1);
            using (context.PushOpacity(activation * fade)) context.DrawEllipse(AccentBrush, null, wire.AtDistance(distance), 2, 2);
        }
    }

    private sealed class Wire
    {
        private readonly Point _start, _end;
        public StreamGeometry Geometry { get; } = new();
        public double Length { get; }
        public Wire(Point start, Point end)
        {
            _start = start; _end = end;
            var delta = end - start; Length = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
            using var path = Geometry.Open(); path.BeginFigure(start, false); path.LineTo(end); path.EndFigure(false);
        }
        public Point AtDistance(double distance) => _start + (_end - _start) * (distance / Length);
    }
}
