using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AvaMedia.Desktop;

/// <summary>Shared, cancellable UI motion. Business actions never wait for animation.</summary>
public sealed class Motion : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Motion, Window, bool>("IsEnabled");

    private static readonly ConditionalWeakTable<Window, WindowMotion> Registrations = new();
    private static readonly HashSet<Window> OpenWindows = [];
    private static readonly Dictionary<Control, CancellationTokenSource> Animations = [];
    private static bool _userReducedMotion;
    private static bool _systemReducedMotion;

    static Motion()
    {
        IsEnabledProperty.Changed.AddClassHandler<Window>((window, change) =>
        {
            if (change.NewValue is true)
                Registrations.GetValue(window, w => new WindowMotion(w));
            else if (Registrations.TryGetValue(window, out var registration))
            {
                registration.Dispose();
                Registrations.Remove(window);
            }
        });
    }

    public static bool GetIsEnabled(Window window) => window.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Window window, bool value) => window.SetValue(IsEnabledProperty, value);

    public static void SetReducedMotion(bool reduced)
    {
        Dispatcher.UIThread.VerifyAccess();
        _userReducedMotion = reduced;
        RefreshPreferences();
    }

    private static void RefreshPreferences()
    {
        // Avalonia 11.3 has no cross-platform reduced-motion setting in IPlatformSettings.
        _systemReducedMotion = OperatingSystem.IsWindows()
            && SystemParametersInfo(0x1042, 0, out var enabled, 0) && !enabled;
        var allowMotion = !_userReducedMotion && !_systemReducedMotion;
        foreach (var window in OpenWindows)
            window.Classes.Set("motion-enabled", allowMotion && !Skin.UsesCustomChrome(window.ActualThemeVariant));
        if (!allowMotion)
            foreach (var animation in Animations.Values.ToArray())
                animation.Cancel();
    }

    public static void Reveal(Control control, string durationResource = "MotionNavigate")
    {
        Dispatcher.UIThread.VerifyAccess();
        if (Skin.UsesCustomChrome(control.ActualThemeVariant)) { Cancel(control); return; }
        if (TopLevel.GetTopLevel(control) is not Window window
            || !window.IsVisible || !control.IsVisible || !window.Classes.Contains("motion-enabled"))
            return;

        Cancel(control);
        if (!control.TryFindResource(durationResource, out var resource) || resource is not TimeSpan duration)
            throw new InvalidOperationException($"Missing motion duration: {durationResource}");

        var cancellation = new CancellationTokenSource();
        Animations[control] = cancellation;
        // FillMode.None restores the original opacity on completion and cancellation.
        var animation = new Animation { Duration = duration, Easing = new CubicEaseOut(), FillMode = FillMode.None };
        var start = new KeyFrame { Cue = new Cue(0) };
        start.Setters.Add(new Setter(Visual.OpacityProperty, Math.Min(0.82, control.Opacity)));
        var end = new KeyFrame { Cue = new Cue(1) };
        end.Setters.Add(new Setter(Visual.OpacityProperty, control.Opacity));
        animation.Children.Add(start);
        animation.Children.Add(end);
        control.DetachedFromVisualTree += Detached;
        control.PropertyChanged += VisibilityChanged;
        _ = Run();

        void Detached(object? sender, VisualTreeAttachmentEventArgs args) => cancellation.Cancel();
        void VisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == Visual.IsVisibleProperty && !control.IsVisible)
                cancellation.Cancel();
        }
        async Task Run()
        {
            try { await animation.RunAsync(control, cancellation.Token); }
            catch (OperationCanceledException) { }
            finally
            {
                control.DetachedFromVisualTree -= Detached;
                control.PropertyChanged -= VisibilityChanged;
                if (Animations.TryGetValue(control, out var current) && ReferenceEquals(current, cancellation))
                    Animations.Remove(control);
                cancellation.Dispose();
            }
        }
    }

    private static void Cancel(Control control)
    {
        if (Animations.TryGetValue(control, out var cancellation))
            cancellation.Cancel();
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);

    private sealed class WindowMotion : IDisposable
    {
        private readonly Window _window;
        public WindowMotion(Window window)
        {
            _window = window;
            window.Opened += Opened;
            window.Activated += Activated;
            window.Closed += Closed;
            window.PropertyChanged += VisibilityChanged;
            window.ActualThemeVariantChanged += ThemeChanged;
            if (window.IsVisible) Opened(window, EventArgs.Empty);
        }
        private void Opened(object? sender, EventArgs args)
        {
            OpenWindows.Add(_window);
            RefreshPreferences();
            if (_window.Content is Control content) Reveal(content, "MotionEnter");
        }
        private void Activated(object? sender, EventArgs args) => RefreshPreferences();
        private void ThemeChanged(object? sender, EventArgs args)
        { RefreshPreferences(); if (Skin.UsesCustomChrome(_window.ActualThemeVariant)) CancelWindowAnimations(); }
        private void Closed(object? sender, EventArgs args) => Dispose();
        private void VisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == Visual.IsVisibleProperty && !_window.IsVisible)
                CancelWindowAnimations();
        }
        private void CancelWindowAnimations()
        {
            foreach (var control in Animations.Keys.Where(c => TopLevel.GetTopLevel(c) == _window).ToArray())
                Cancel(control);
        }
        public void Dispose()
        {
            _window.Opened -= Opened;
            _window.Activated -= Activated;
            _window.Closed -= Closed;
            _window.PropertyChanged -= VisibilityChanged;
            _window.ActualThemeVariantChanged -= ThemeChanged;
            OpenWindows.Remove(_window);
            _window.Classes.Remove("motion-enabled");
            CancelWindowAnimations();
        }
    }
}
