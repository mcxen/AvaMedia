using System.Runtime.CompilerServices;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

// Translate presentation properties only. ItemsSource, selections, models and user inputs retain their values.
public sealed class Localization : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Localization, Control, bool>("IsEnabled");
    public static readonly AttachedProperty<bool> IsUserTextProperty =
        AvaloniaProperty.RegisterAttached<Localization, Control, bool>("IsUserText", inherits: true);
    private static readonly ConditionalWeakTable<Control, Registration> Registrations = new();
    // Keep the explicit source of formatted binding values by object identity. No text pattern matching.
    private static readonly ConditionalWeakTable<string, TextSource> Sources = new();
    private sealed record TextSource(object Value);
    private sealed record LabelKey(string Value);
    private static readonly List<WeakReference<Registration>> Live = [];
    private static int _registrationsSinceSweep;
    private static readonly Dictionary<string, string> English = LoadEnglish();
    public static event EventHandler? Changed;

    static Localization()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((control, change) =>
        {
            if (change.NewValue is true)
                Register(control);
            else if (Registrations.TryGetValue(control, out var registration)) { registration.Dispose(); Registrations.Remove(control); }
        });
    }

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);
    public static bool GetIsUserText(Control control) => control.GetValue(IsUserTextProperty);
    public static void SetIsUserText(Control control, bool value) => control.SetValue(IsUserTextProperty, value);
    private static Registration Register(Control control) => Registrations.GetValue(control, target =>
    {
        if (++_registrationsSinceSweep >= 256)
        { Live.RemoveAll(reference => !reference.TryGetTarget(out var registration) || registration.IsDisposed); _registrationsSinceSweep = 0; }
        var registration = new Registration(target); Live.Add(new(registration)); return registration;
    });

    public static void Apply(string preference)
    {
        Dispatcher.UIThread.VerifyAccess();
        var previous = AppLanguage.Current;
        AppLanguage.Apply(preference);
        if (previous == AppLanguage.Current) return;
        // Weak registrations allow closed windows and virtualized rows to be collected.
        for (var index = Live.Count - 1; index >= 0; index--)
            if (Live[index].TryGetTarget(out var registration)) registration.Refresh(); else Live.RemoveAt(index);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    // Labels must match a complete resource. Never infer a translation from a prefix, suffix or arbitrary fragment.
    private static string Literal(string source) => !AppLanguage.IsChinese && English.TryGetValue(source, out var translated) ? translated : source;
    private static string Remember(object source, string display)
    {
        if (display.Length == 0) return display;
        var value = new string(display.AsSpan()); Sources.Add(value, new(source)); return value;
    }
    public static string Text(string source) => English.ContainsKey(source) ? Remember(source, Literal(source)) : source;
    public static string Lines(string source) => English.ContainsKey(source) ? Text(source) : string.Join("\n", source.Replace("\r\n", "\n").Split('\n').Select(Text));
    public static string Join(string separator, IEnumerable<string> clauses)
    {
        var arguments = clauses.Select(value => Sources.TryGetValue(value, out var tagged) ? tagged.Value : (object)new LabelKey(value)).ToArray();
        return Format(System.Runtime.CompilerServices.FormattableStringFactory.Create(string.Join(separator, arguments.Select((_, i) => "{" + i + "}")), arguments));
    }
    public static object Key(string source) => new LabelKey(source);
    public static string OrientationReason(VideoOrientationResult result) => result.IsCertain
        ? Format($"有效 {result.ValidFrames}/{result.SampledFrames} 帧，{result.AgreeingFrames} 帧方向一致。") : Text(result.Reason);
    public static string Format(FormattableString source) => Remember(source, Render(source));
    private static string Render(object source)
    {
        if (source is LabelKey label) return Literal(label.Value);
        if (source is string literal) return Literal(literal);
        var template = (FormattableString)source;
        return RenderTemplate(template);
    }
    private static string RenderTemplate(FormattableString source)
    {
        var key = Regex.Replace(source.Format, @"\{(\d+)(?:,[^}:]+)?(?::[^}]+)?\}", "{$1}");
        var template = source.Format;
        var formats = Regex.Matches(source.Format, @"\{(\d+)(?:,[^}:]+)?(?::[^}]+)?\}")
            .GroupBy(match => match.Groups[1].Value).ToDictionary(group => group.Key, group => group.First().Value);
        if (!AppLanguage.IsChinese && English.TryGetValue(key, out var translated))
            template = Regex.Replace(translated, @"\{(\d+)\}", match => formats[match.Groups[1].Value]);
        var arguments = source.GetArguments().Select(value => value is LabelKey or FormattableString ? Render(value) : value is string text && Sources.TryGetValue(text, out var tagged) ? Render(tagged.Value) : value).ToArray();
        return string.Format(CultureInfo.InvariantCulture, template, arguments);
    }
    public static void SetText(TextBlock control, FormattableString source) => Register(control).SetTemplate(TextBlock.TextProperty, source);
    public static void SetText(TextBox control, FormattableString source) => Register(control).SetTemplate(TextBox.TextProperty, source);
    public static void SetTitle(Window control, FormattableString source) => Register(control).SetTemplate(Window.TitleProperty, source);
    public static void SetContent(ContentControl control, FormattableString source) => Register(control).SetTemplate(ContentControl.ContentProperty, source);

    private static Dictionary<string, string> LoadEnglish()
    {
        using var stream = typeof(Localization).Assembly.GetManifestResourceStream("AvaMedia.Desktop.Strings.en-US.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    private sealed class Registration : IDisposable
    {
        private readonly Control _control;
        private readonly Dictionary<AvaloniaProperty, object> _source = [];
        private readonly HashSet<AvaloniaProperty> _properties;
        private bool _writing;
        private bool _disposed;
        public bool IsDisposed => _disposed;
        public Registration(Control control)
        {
            _control = control;
            _properties = Properties(control).ToHashSet();
            control.PropertyChanged += PropertyChanged;
            foreach (var property in _properties) Capture(property);
        }
        private static IEnumerable<AvaloniaProperty> Properties(Control control)
        {
            yield return ToolTip.TipProperty;
            yield return AutomationProperties.NameProperty;
            yield return AutomationProperties.HelpTextProperty;
            if (control is Window) yield return Window.TitleProperty;
            if (control is TextBlock) yield return TextBlock.TextProperty;
            if (control is TextBox box)
            {
                yield return TextBox.WatermarkProperty;
                if (box.IsReadOnly) yield return TextBox.TextProperty;
            }
            if (control is Button or Label) yield return ContentControl.ContentProperty;
            if (control is HeaderedContentControl) yield return HeaderedContentControl.HeaderProperty;
            if (control is HeaderedSelectingItemsControl) yield return HeaderedSelectingItemsControl.HeaderProperty;
        }
        private void PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
        {
            if (_writing) return;
            if (change.Property == IsUserTextProperty) { Refresh(); return; }
            if (change.Property == TextBox.IsReadOnlyProperty)
            {
                if (_control is TextBox { IsReadOnly: true }) { _properties.Add(TextBox.TextProperty); Capture(TextBox.TextProperty); }
                else
                {
                    _properties.Remove(TextBox.TextProperty);
                    if (_source.Remove(TextBox.TextProperty, out var original)) _control.SetCurrentValue(TextBox.TextProperty, original is string text ? text : Render(original));
                }
                return;
            }
            if (_properties.Contains(change.Property)) Capture(change.Property);
        }
        private void Capture(AvaloniaProperty property)
        {
            if (_control.GetValue(property) is string value) { var source = Sources.TryGetValue(value, out var tagged) ? tagged.Value : value; _source[property] = source; Write(property, source); }
            else _source.Remove(property);
        }
        public void Refresh()
        {
            if (_disposed) return;
            foreach (var entry in _source) Write(entry.Key, entry.Value);
        }
        public void SetTemplate(AvaloniaProperty property, FormattableString source)
        { _source[property] = source; Write(property, source); }
        private void Write(AvaloniaProperty property, object source)
        {
            var userValue = GetIsUserText(_control) && (property == TextBlock.TextProperty || property == TextBox.TextProperty || property == ToolTip.TipProperty);
            var display = userValue ? source.ToString() : source is string text && _control is TextBox { IsReadOnly: true } && property == TextBox.TextProperty ? Lines(text) : Render(source);
            _writing = true;
            try { _control.SetCurrentValue(property, userValue || display is null ? display : Remember(source, display)); }
            finally { _writing = false; }
        }
        public void Dispose() { _disposed = true; _source.Clear(); _control.PropertyChanged -= PropertyChanged; }
    }
}
