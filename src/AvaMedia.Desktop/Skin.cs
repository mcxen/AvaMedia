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
    public static void Zoom(Window window) { if (Windows.TryGetValue(window, out var registration)) registration.Zoom(); }
    public static void Apply(string name)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (name == "MacOS9") EnsureClassicStyles();
        Application.Current!.RequestedThemeVariant = name switch { "MacOS9" => MacOS9, "Dark" => ThemeVariant.Dark, _ => ThemeVariant.Light };
    }
    private static void EnsureClassicStyles()
    {
        var app = Application.Current!;
        var source = new Uri("avares://AvaMedia.Desktop/Styles/Platinum.axaml");
        if (app.Styles.OfType<StyleInclude>().Any(style => style.Source == source)) return;
        app.Styles.Add(new StyleInclude(source) { Source = source });
    }

    private sealed class Registration : IDisposable
    {
        private readonly Window _window;
        private PlatinumWindowFrame? _frame;
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
        public void Zoom() => _frame?.Zoom();
        private void PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ContentControl.ContentProperty || e.Property == Window.WindowStateProperty) Refresh();
        }
        private void Refresh()
        {
            if (_changing) return;
            var platinum = _window.ActualThemeVariant == MacOS9;
            if (platinum) EnsureClassicStyles();
            var classic = platinum && _window.WindowState != WindowState.FullScreen;
            _window.Classes.Set("mac-os9", platinum);
            RenderOptions.SetTextRenderingMode(_window, platinum ? TextRenderingMode.Alias : _textMode);
            _changing = true;
            try
            {
                if (_frame is not null && !ReferenceEquals(_window.Content, _frame))
                {
                    _frame.ReleaseContent(); _frame = null; _window.SystemDecorations = _decorations;
                }
                if (classic && _frame is null && _window.Content is { } content)
                {
                    _decorations = _window.SystemDecorations;
                    _window.Content = null;
                    _frame = new PlatinumWindowFrame(_window, content);
                    _window.Content = _frame;
                    _window.SystemDecorations = SystemDecorations.None;
                }
                else if (!classic && _frame is not null)
                {
                    var restoredContent = _frame.ReleaseContent();
                    _window.Content = null; _window.Content = restoredContent;
                    _window.SystemDecorations = _decorations;
                    _frame = null;
                }
            }
            finally { _changing = false; }
        }
        private void Closed(object? sender, EventArgs e) { Dispose(); Windows.Remove(_window); }
        public void Dispose()
        {
            _window.ActualThemeVariantChanged -= Changed; _window.Opened -= Changed;
            _window.Closed -= Closed; _window.PropertyChanged -= PropertyChanged;
        }
    }
}
