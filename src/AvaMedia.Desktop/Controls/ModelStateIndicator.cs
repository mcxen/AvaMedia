using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

/// <summary>A quiet matrix for model activity; it never represents a completion percentage.</summary>
internal sealed class ModelStateIndicator : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<ModelStateIndicator, IBrush?>(nameof(Foreground));
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    private const double CycleSeconds = 1.2;
    private readonly List<Visual> _ancestors = [];
    private ModelLoadState _state;
    private CompositionCustomVisual? _visual;
    private MatrixState? _sent;
    private bool _attached, _running;
    private sealed record MatrixState(ModelLoadState State, bool Running, Vector Size, IImmutableBrush Brush);

    static ModelStateIndicator() => AffectsRender<ModelStateIndicator>(ForegroundProperty);

    public ModelStateIndicator()
    {
        Width = Height = 18; IsHitTestVisible = false;
        AttachedToVisualTree += (_, _) => Attach();
        DetachedFromVisualTree += (_, _) => Detach();
        ActualThemeVariantChanged += (_, _) => RefreshVisual();
    }

    public void Update(ModelLoadState state)
    {
        if (_state == state) return;
        _state = state; RefreshVisual(); InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty || change.Property == ForegroundProperty
            || change.Property == IsVisibleProperty || change.Property == AvaMedia.Controls.ComponentMotion.ReducedProperty)
            RefreshVisual();
    }

    private void Attach()
    {
        _attached = true;
        foreach (var ancestor in this.GetVisualAncestors())
        {
            _ancestors.Add(ancestor); ancestor.PropertyChanged += PresentationChanged;
        }
        Motion.PreferencesChanged += RefreshVisual;
        RefreshVisual();
    }

    private void Detach()
    {
        _attached = false;
        if (_visual is not null && _sent is { } state)
            _visual.SendHandlerMessage(state with { Running = false });
        ElementComposition.SetElementChildVisual(this, null);
        _visual = null; _sent = null; _running = false;
        foreach (var ancestor in _ancestors) ancestor.PropertyChanged -= PresentationChanged;
        _ancestors.Clear(); Motion.PreferencesChanged -= RefreshVisual;
    }

    private void PresentationChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == IsVisibleProperty || change.Property == Window.WindowStateProperty)
            RefreshVisual();
    }

    private bool Active => _state is ModelLoadState.Loading or ModelLoadState.Warming or ModelLoadState.InUse;

    private void RefreshVisual()
    {
        var running = _attached && Active && Motion.CanAnimate(this)
            && !AvaMedia.Controls.ComponentMotion.GetReduced(this) && !Skin.UsesCustomChrome(ActualThemeVariant);
        if (_visual is null && running && ElementComposition.GetElementVisual(this) is { } visual)
        {
            _visual = visual.Compositor.CreateCustomVisual(new MatrixHandler());
            ElementComposition.SetElementChildVisual(this, _visual);
        }
        _running = running && _visual is not null;
        if (_visual is not null)
        {
            _visual.Size = new(Bounds.Width, Bounds.Height);
            var state = new MatrixState(_state, _running, _visual.Size, (GetValue(ForegroundProperty) ?? Brushes.Transparent).ToImmutable());
            if (state != _sent) { _sent = state; _visual.SendHandlerMessage(state); }
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var brush = GetValue(ForegroundProperty);
        if (!Active)
        {
            LucideIcon.Draw(context, Bounds.Size, brush, _state switch
            {
                ModelLoadState.Ready => "check", ModelLoadState.Failed => "circle-alert", _ => "minus"
            });
            return;
        }
        for (var index = 0; index < 16; index++)
        {
            var centre = index is 5 or 6 or 9 or 10;
            using var opacity = context.PushOpacity(!_running && centre ? .8 : .18);
            context.DrawRectangle(brush, null, Dot(index, new(Bounds.Width, Bounds.Height)), .5, .5);
        }
    }

    private static Rect Dot(int index, Vector size) => new(
        (size.X - 14) / 2 + index % 4 * 4, (size.Y - 14) / 2 + index / 4 * 4, 2, 2);

    private sealed class MatrixHandler : CompositionCustomVisualHandler
    {
        private static readonly int[] Perimeter = [0, 1, 2, 3, 7, 11, 15, 14, 13, 12, 8, 4];
        private MatrixState? _state;
        private double _started, _lastFrame;
        private bool _framePending;

        public override void OnMessage(object message)
        {
            if (message is not MatrixState state) return;
            if (_state is null || _state.State != state.State || _state.Running != state.Running)
                _started = CompositionNow.TotalSeconds;
            _state = state; Invalidate(); ScheduleFrame();
        }

        private void ScheduleFrame()
        {
            if (_framePending || _state is not { Running: true }) return;
            _framePending = true; RegisterForNextAnimationFrameUpdate();
        }

        public override void OnAnimationFrameUpdate()
        {
            _framePending = false;
            if (_state is not { Running: true }) return;
            var now = CompositionNow.TotalSeconds;
            if (now - _lastFrame >= 1d / 30) { _lastFrame = now; Invalidate(); }
            ScheduleFrame();
        }

        public override void OnRender(ImmediateDrawingContext context)
        {
            if (_state is not { Running: true } state) return;
            var time = (CompositionNow.TotalSeconds - _started) / CycleSeconds;
            for (var index = 0; index < 16; index++)
            {
                var ringIndex = Array.IndexOf(Perimeter, index);
                var delay = state.State switch
                {
                    ModelLoadState.Loading => index % 4 * .1,
                    ModelLoadState.Warming => index is 5 or 6 or 9 or 10 ? 0 : .16,
                    _ => ringIndex >= 0 ? (double)ringIndex / Perimeter.Length : 0
                };
                var phase = (time - delay) % 1;
                if (phase < 0) phase += 1;
                var light = state.State == ModelLoadState.InUse && ringIndex < 0 ? .25
                    : phase < .15 ? Smooth(phase / .15)
                    : phase < .45 ? 1 - Smooth((phase - .15) / .3) : 0;
                if (light <= .005) continue;
                var rect = Dot(index, state.Size);
                using var opacity = context.PushOpacity(light * .82, rect);
                context.DrawRectangle(state.Brush, null, rect, .5, .5);
            }
        }

        private static double Smooth(double value) => value * value * (3 - 2 * value);
    }
}
