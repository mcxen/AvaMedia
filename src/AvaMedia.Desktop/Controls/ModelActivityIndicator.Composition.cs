using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

public sealed partial class ModelActivityIndicator
{
    private CompositionCustomVisual? _activityVisual;
    private bool _activityRunning;
    private ActivityState? _activityState;
    private sealed record ActivityState(string Id, ModelLoadState State, bool Running, bool InUse,
        bool Classic, bool Xp, double Height, IImmutableBrush Brush);

    private void UpdateActivityVisual()
    {
        var value = _current;
        var running = Visible && Motion.CanAnimate(this) && value is not null
            && (value.State == ModelLoadState.InUse || value.Preparing && value.Preparation?.Fraction is null);
        if (_activityVisual is null && running && ElementComposition.GetElementVisual(this) is { } visual)
        {
            _activityVisual = visual.Compositor.CreateCustomVisual(new ActivityHandler());
            ElementComposition.SetElementChildVisual(this, _activityVisual);
        }
        _activityRunning = running && _activityVisual is not null;
        if (_activityVisual is null) return;
        _activityVisual.Size = new(Bounds.Width, Bounds.Height);
        var state = new ActivityState(value?.Id ?? "", value?.State ?? ModelLoadState.Unloaded,
            _activityRunning, value?.State == ModelLoadState.InUse, ActualThemeVariant == Skin.MacOS9,
            ActualThemeVariant == Skin.WindowsXP, Bounds.Height, (HighlightBrush ?? Foreground ?? Brushes.White).ToImmutable());
        // Only immutable state crosses to the render thread; it never reads controls,
        // mutable theme brushes, model workers or application state.
        if (state == _activityState) return;
        _activityState = state;
        _activityVisual.SendHandlerMessage(state);
    }

    private void DetachActivityVisual()
    {
        if (_activityVisual is not null && _activityState is { } state)
            _activityVisual.SendHandlerMessage(state with { Running = false });
        ElementComposition.SetElementChildVisual(this, null);
        _activityVisual = null; _activityState = null; _activityRunning = false;
    }

    private sealed class ActivityHandler : CompositionCustomVisualHandler
    {
        private ActivityState? _state;
        private int _effect, _previousEffect;
        private double _started, _previousStarted, _switchStarted, _nextEffect = 6, _lastFrame;
        private bool _framePending;

        public override void OnMessage(object message)
        {
            if (message is not ActivityState state) return;
            if (_state is null || _state.Id != state.Id || _state.State != state.State || _state.Running != state.Running)
            {
                _started = CompositionNow.TotalSeconds; _switchStarted = 0; _effect = 0;
                _nextEffect = 4 + Random.Shared.NextDouble() * 3;
            }
            _state = state;
            Invalidate();
            ScheduleFrame();
        }

        private void ScheduleFrame()
        {
            if (_framePending || _state is not { Running: true }) return;
            _framePending = true;
            RegisterForNextAnimationFrameUpdate();
        }

        public override void OnAnimationFrameUpdate()
        {
            _framePending = false;
            if (_state is not { Running: true }) return;
            var now = CompositionNow.TotalSeconds;
            // Preserve the original 30 fps effect budget on high-refresh-rate screens.
            if (now - _lastFrame >= 1d / 30)
            {
                _lastFrame = now;
                if (_state.InUse && now - _started >= _nextEffect)
                {
                    _previousEffect = _effect; _previousStarted = _started;
                    var next = Random.Shared.Next(Effects.Length - 1);
                    _effect = next >= _effect ? next + 1 : next;
                    _started = _switchStarted = now; _nextEffect = 4 + Random.Shared.NextDouble() * 3;
                }
                Invalidate();
            }
            ScheduleFrame();
        }

        public override void OnRender(ImmediateDrawingContext context)
        {
            if (_state is not { Running: true } state) return;
            var now = CompositionNow.TotalSeconds;
            var time = now - _started;
            var top = (state.Height - 16) / 2;
            var radius = state.Classic ? 1.5 : state.Xp ? 2 : 2.5;
            for (var i = 0; i < 24; i++)
            {
                var column = i % 12; var row = i / 12;
                var light = state.InUse ? ActivityLight(column, row, time, now) * (state.Classic ? .45 : .55)
                    : Gaussian(column - (time * 4 % 16 - 2), 1.15) * .7;
                if (light <= .005) continue;
                var rect = new Rect((state.Classic ? 18.5 : 20) + column * 9.5, top + row * 9, 7.5, 7);
                using var opacity = context.PushOpacity(light, rect);
                context.DrawRectangle(state.Brush, null, rect, radius, radius);
            }
        }

        private double ActivityLight(int column, int row, double time, double now)
        {
            var next = Brightness(Effects[_effect], column, row, time);
            if (_switchStarted == 0) return next;
            var blend = Math.Clamp((now - _switchStarted) / .24, 0, 1);
            if (blend >= 1) return next;
            blend = blend * blend * (3 - 2 * blend);
            return Brightness(Effects[_previousEffect], column, row, now - _previousStarted) * (1 - blend) + next * blend;
        }
    }
}
