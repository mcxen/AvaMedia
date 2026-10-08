using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using AvaMedia.Desktop.Controls;
using Avalonia.Markup.Xaml.Styling;

namespace AvaMedia.Desktop;

public sealed class Skin : AvaloniaObject
{
    public static ThemeVariant MacOS9 { get; } = new("MacOS9", ThemeVariant.Light);
    public static ThemeVariant WindowsXP { get; } = new("WindowsXP", ThemeVariant.Light);
    public static bool UsesCustomChrome(ThemeVariant variant) => variant == MacOS9 || variant == WindowsXP;
    public static readonly AttachedProperty<bool> IsEnabledProperty = AvaloniaProperty.RegisterAttached<Skin, Window, bool>("IsEnabled");
    private static readonly ConditionalWeakTable<Window, Registration> Windows = new();

    static Skin() => IsEnabledProperty.Changed.AddClassHandler<Window>((window, change) =>
    {
        if (change.NewValue is true) Windows.GetValue(window, w => new Registration(w));
        else if (Windows.TryGetValue(window, out var registration)) { registration.Dispose(); Windows.Remove(window); }
    });

    public static bool GetIsEnabled(Window window) => window.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Window window, bool value) => window.SetValue(IsEnabledProperty, value);
    public static void ToggleShade(Window window) { if (Windows.TryGetValue(window, out var registration)) registration.ToggleShade(); }
    public static void RestoreWindow(Window window) { if (Windows.TryGetValue(window, out var registration)) registration.RestoreWindow(); }
    public static void Zoom(Window window) { if (Windows.TryGetValue(window, out var registration)) registration.Zoom(); }
    public static void Apply(string name)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (name == "MacOS9") EnsureSkinStyles("Platinum");
        if (name == "WindowsXP") EnsureSkinStyles("WindowsXP");
        Application.Current!.RequestedThemeVariant = name switch { "MacOS9" => MacOS9, "WindowsXP" => WindowsXP, "Dark" => ThemeVariant.Dark, _ => ThemeVariant.Light };
    }
    private static void EnsureSkinStyles(string stylesheet)
    {
        var app = Application.Current!;
        var source = new Uri($"avares://AvaMedia.Desktop/Styles/{stylesheet}.axaml");
        if (app.Styles.OfType<StyleInclude>().Any(style => style.Source == source)) return;
        app.Styles.Add(new StyleInclude(source) { Source = source });
    }

    private sealed class Registration : IDisposable
    {
        private readonly Window _window;
        private PlatinumWindowFrame? _frame;
        private WindowsXPWindowFrame? _xpFrame;
        private SystemDecorations _decorations;
        private readonly TextRenderingMode _textMode;
        private bool _changing;
        public Registration(Window window)
        {
            _window = window;
            _textMode = RenderOptions.GetTextRenderingMode(window);
            WindowArtwork.Enable(window);
            window.ActualThemeVariantChanged += Changed;
            window.Opened += Changed;
            window.Closed += Closed;
            window.PropertyChanged += PropertyChanged;
            Refresh();
        }
        private void Changed(object? sender, EventArgs e) => Refresh();
        public void ToggleShade() => _frame?.ToggleShade();
        public void RestoreWindow() => _frame?.RestoreShade();
        public void Zoom() { if (_xpFrame is not null) _xpFrame.Zoom(); else _frame?.Zoom(); }
        private void PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ContentControl.ContentProperty || e.Property == Window.WindowStateProperty) Refresh();
        }
        private void Refresh()
        {
            if (_changing) return;
            var platinum = _window.ActualThemeVariant == MacOS9;
            var xp = _window.ActualThemeVariant == WindowsXP;
            if (platinum) EnsureSkinStyles("Platinum");
            if (xp) EnsureSkinStyles("WindowsXP");
            var decorated = _window.WindowState != WindowState.FullScreen;
            _window.Classes.Set("mac-os9", platinum);
            _window.Classes.Set("windows-xp", xp);
            RenderOptions.SetTextRenderingMode(_window, platinum ? TextRenderingMode.Alias : _textMode);
            _changing = true;
            try
            {
                if (_frame is not null && !ReferenceEquals(_window.Content, _frame))
                {
                    _frame.ReleaseContent(); _frame = null; _window.SystemDecorations = _decorations;
                }
                if (_xpFrame is not null && !ReferenceEquals(_window.Content, _xpFrame))
                {
                    _xpFrame.ReleaseContent(); _xpFrame = null; _window.SystemDecorations = _decorations;
                }
                if ((!platinum || !decorated) && _frame is not null)
                {
                    var restoredContent = _frame.ReleaseContent();
                    _window.Content = null; _window.Content = restoredContent;
                    _window.SystemDecorations = _decorations;
                    _frame = null;
                }
                if ((!xp || !decorated) && _xpFrame is not null)
                {
                    var restoredContent = _xpFrame.ReleaseContent();
                    _window.Content = null; _window.Content = restoredContent;
                    _window.SystemDecorations = _decorations;
                    _xpFrame = null;
                }
                if (decorated && platinum && _frame is null && _window.Content is { } content)
                {
                    _decorations = _window.SystemDecorations;
                    _window.Content = null;
                    _frame = new PlatinumWindowFrame(_window, content);
                    _window.Content = _frame;
                    _window.SystemDecorations = SystemDecorations.None;
                }
                else if (decorated && xp && _xpFrame is null && _window.Content is { } xpContent)
                {
                    _decorations = _window.SystemDecorations;
                    _window.Content = null;
                    _xpFrame = new WindowsXPWindowFrame(_window, xpContent);
                    _window.Content = _xpFrame;
                    _window.SystemDecorations = SystemDecorations.None;
                }
            }
            finally { _changing = false; }
        }
        private void Closed(object? sender, EventArgs e) { Dispose(); Windows.Remove(_window); }
        public void Dispose()
        {
            _frame?.ReleaseContent(); _xpFrame?.ReleaseContent();
            _window.ActualThemeVariantChanged -= Changed; _window.Opened -= Changed;
            _window.Closed -= Closed; _window.PropertyChanged -= PropertyChanged;
        }
    }
}
