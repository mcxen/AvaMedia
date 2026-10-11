using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AvaMedia.Desktop;

/// <summary>Interruptible sidebar motion; all skins share the same navigation timing.</summary>
internal sealed class NavigationMotion : IDisposable
{
    private sealed record Pending(CancellationTokenSource Cancellation, Action? Completed);
    private readonly Dictionary<Control, Pending> _running = [];

    public NavigationMotion() => Motion.PreferencesChanged += FinishAll;

    public static Vector Translation(Control control) => control.RenderTransform is { } transform
        ? new(transform.Value.M31, transform.Value.M32) : default;

    public void Animate(Control control, int milliseconds, Vector translation = default, double opacity = 1,
        Vector? from = null, double? fromOpacity = null, bool enabled = true, Action? completed = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        var interrupted = _running.ContainsKey(control);
        var start = interrupted ? Translation(control) : from ?? Translation(control);
        var startOpacity = interrupted ? control.Opacity : fromOpacity ?? control.Opacity;
        Cancel(control);
        // The base is always the final state, so disabling motion can finish immediately.
        control.RenderTransform = Transform(translation);
        control.Opacity = opacity;
        if (!enabled || !Motion.CanAnimate(control)) { completed?.Invoke(); return; }
        var animation = new Animation { Duration = TimeSpan.FromMilliseconds(milliseconds),
            Easing = new CubicEaseOut(), FillMode = FillMode.None };
        var first = new KeyFrame { Cue = new Cue(0) };
        first.Setters.Add(new Setter(Visual.RenderTransformProperty, Transform(start)));
        first.Setters.Add(new Setter(Visual.OpacityProperty, startOpacity));
        var last = new KeyFrame { Cue = new Cue(1) };
        last.Setters.Add(new Setter(Visual.RenderTransformProperty, Transform(translation)));
        last.Setters.Add(new Setter(Visual.OpacityProperty, opacity));
        animation.Children.Add(first); animation.Children.Add(last);
        var pending = new Pending(new CancellationTokenSource(), completed);
        _running.Add(control, pending);
        control.DetachedFromVisualTree += Detached;
        _ = RunAsync();

        void Detached(object? sender, VisualTreeAttachmentEventArgs args) => Finish(control);
        async Task RunAsync()
        {
            try { await animation.RunAsync(control, pending.Cancellation.Token); }
            catch (OperationCanceledException) { }
            finally
            {
                control.DetachedFromVisualTree -= Detached;
                if (_running.TryGetValue(control, out var current) && ReferenceEquals(current, pending))
                {
                    _running.Remove(control);
                    pending.Completed?.Invoke();
                }
                pending.Cancellation.Dispose();
            }
        }
    }

    public void Cancel(Control control)
    {
        if (_running.Remove(control, out var pending)) pending.Cancellation.Cancel();
    }

    public void Finish(Control control)
    {
        if (!_running.Remove(control, out var pending)) return;
        pending.Cancellation.Cancel(); pending.Completed?.Invoke();
    }

    public void FinishAll()
    {
        foreach (var control in _running.Keys.ToArray()) Finish(control);
    }

    private static TransformOperations Transform(Vector translation) => TransformOperations.Parse(
        string.Create(CultureInfo.InvariantCulture, $"translate({translation.X}px,{translation.Y}px)"));

    public void Dispose()
    {
        Motion.PreferencesChanged -= FinishAll;
        FinishAll();
    }
}
