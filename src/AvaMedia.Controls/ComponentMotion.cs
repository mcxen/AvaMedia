using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;

namespace AvaMedia.Controls;

/// <summary>宿主减少动态效果的继承开关；不依赖宿主应用设置类型。</summary>
public sealed class ComponentMotion : AvaloniaObject
{
    public static readonly AttachedProperty<bool> ReducedProperty = AvaloniaProperty.RegisterAttached<ComponentMotion, Control, bool>("Reduced", inherits: true);
    public static bool GetReduced(Control control) => control.GetValue(ReducedProperty);
    public static void SetReduced(Control control, bool value) => control.SetValue(ReducedProperty, value);
}

/// <summary>覆盖式滚动条在滚动/悬停时显示，停止活动一秒后淡出。</summary>
public sealed class ScrollVisibility : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsEnabledProperty = AvaloniaProperty.RegisterAttached<ScrollVisibility, ScrollBar, bool>("IsEnabled");
    public static readonly AttachedProperty<bool> PinnedProperty = AvaloniaProperty.RegisterAttached<ScrollVisibility, ScrollBar, bool>("Pinned");
    private static readonly AttachedProperty<State?> StateProperty = AvaloniaProperty.RegisterAttached<ScrollVisibility, ScrollBar, State?>("State");
    static ScrollVisibility() => IsEnabledProperty.Changed.AddClassHandler<ScrollBar>((bar, _) =>
    {
        bar.GetValue(StateProperty)?.Dispose(); bar.SetValue(StateProperty, GetIsEnabled(bar) ? new State(bar) : null);
    });
    public static bool GetIsEnabled(ScrollBar bar) => bar.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(ScrollBar bar, bool enabled) => bar.SetValue(IsEnabledProperty, enabled);
    public static bool GetPinned(ScrollBar bar) => bar.GetValue(PinnedProperty);
    public static void SetPinned(ScrollBar bar, bool value) => bar.SetValue(PinnedProperty, value);
    private sealed class State : IDisposable
    {
        private readonly ScrollBar _bar;
        private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(1) };
        public State(ScrollBar bar)
        {
            _bar = bar; bar.PropertyChanged += Changed; bar.PointerEntered += Entered; bar.PointerExited += Exited;
            bar.DetachedFromVisualTree += Detached; bar.AttachedToVisualTree += Attached;
            _idle.Tick += (_, _) => { _idle.Stop(); if (!_bar.IsPointerOver && !GetPinned(_bar)) _bar.Classes.Add("ac-hidden"); };
            Show();
        }
        private void Show() { _bar.Classes.Remove("ac-hidden"); _idle.Stop(); if (!_bar.IsPointerOver && !GetPinned(_bar)) _idle.Start(); }
        private void Changed(object? sender, AvaloniaPropertyChangedEventArgs e) { if (e.Property == RangeBase.ValueProperty || e.Property == PinnedProperty) Show(); }
        private void Entered(object? sender, Avalonia.Input.PointerEventArgs e) => Show();
        private void Exited(object? sender, Avalonia.Input.PointerEventArgs e) => Show();
        private void Attached(object? sender, VisualTreeAttachmentEventArgs e) => Show();
        private void Detached(object? sender, VisualTreeAttachmentEventArgs e) => _idle.Stop();
        public void Dispose()
        {
            _idle.Stop(); _bar.PropertyChanged -= Changed; _bar.PointerEntered -= Entered; _bar.PointerExited -= Exited;
            _bar.DetachedFromVisualTree -= Detached; _bar.AttachedToVisualTree -= Attached; _bar.Classes.Remove("ac-hidden");
        }
    }
}
