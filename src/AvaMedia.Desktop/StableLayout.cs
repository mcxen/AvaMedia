using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Media;

namespace AvaMedia.Desktop;

/// <summary>Reserve dynamic text before its first update, including translated button states.</summary>
public sealed class StableLayout : AvaloniaObject
{
    public static readonly AttachedProperty<string?> SamplesProperty =
        AvaloniaProperty.RegisterAttached<StableLayout, Control, string?>("Samples");
    public static readonly AttachedProperty<int> StatusLinesProperty =
        AvaloniaProperty.RegisterAttached<StableLayout, TextBlock, int>("StatusLines");
    private static readonly ConditionalWeakTable<Control, Reservation> Reservations = new();

    static StableLayout()
    {
        SamplesProperty.Changed.AddClassHandler<Control>((control, _) => Register(control));
        StatusLinesProperty.Changed.AddClassHandler<TextBlock>((control, _) => Register(control));
    }

    public static string? GetSamples(Control control) => control.GetValue(SamplesProperty);
    public static void SetSamples(Control control, string? value) => control.SetValue(SamplesProperty, value);
    public static int GetStatusLines(TextBlock control) => control.GetValue(StatusLinesProperty);
    public static void SetStatusLines(TextBlock control, int value) => control.SetValue(StatusLinesProperty, value);
    public static void Reserve(Control control, params string[] samples) => SetSamples(control,
        string.Join('|', new[] { GetSamples(control) }.Concat(samples).Where(value => !string.IsNullOrEmpty(value))));

    private static void Register(Control control) => Reservations.GetValue(control, c => new Reservation(c)).Update();

    private sealed class Reservation
    {
        private readonly Control _control;
        private bool _attached, _updating;
        public Reservation(Control control)
        {
            _control = control;
            control.AttachedToVisualTree += (_, _) => { Attach(); Update(); };
            control.DetachedFromVisualTree += (_, _) =>
            { if (_attached) Localization.Changed -= LanguageChanged; _attached = false; };
            control.PropertyChanged += (_, change) =>
            {
                if (change.Property.Name is "FontFamily" or "FontSize" or "FontWeight" or "FontStyle" or "FontFeatures"
                    or "Padding" or "BorderThickness" or "MinWidth") Update();
            };
            if (TopLevel.GetTopLevel(control) is not null) Attach();
        }
        private void Attach()
        { if (_attached) return; _attached = true; Localization.Changed += LanguageChanged; }
        private void LanguageChanged(object? sender, EventArgs args) => Update();

        public void Update()
        {
            if (_updating) return;
            _updating = true;
            try
            {
                var text = _control as TextBlock;
                var field = _control as TemplatedControl;
                if (text is null && field is null) return;
                var family = text?.FontFamily ?? field!.FontFamily;
                var size = text?.FontSize ?? field!.FontSize;
                var typeface = new Typeface(family, text?.FontStyle ?? field!.FontStyle, text?.FontWeight ?? field!.FontWeight);
                if (GetSamples(_control) is { Length: > 0 } samples)
                {
                    var width = samples.Split('|').Max(sample =>
                    {
                        var measured = new FormattedText(Localization.Text(sample), CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight, typeface, size, Brushes.Black);
                        if ((text?.FontFeatures ?? field?.FontFeatures) is { } features) measured.SetFontFeatures(features);
                        return measured.WidthIncludingTrailingWhitespace;
                    });
                    if (field is not null) width += field.Padding.Left + field.Padding.Right + field.BorderThickness.Left + field.BorderThickness.Right;
                    _control.SetCurrentValue(Control.WidthProperty, Math.Max(_control.MinWidth, Math.Ceiling(width) + 2));
                }
                if (text is not null && GetStatusLines(text) is > 0 and var lines)
                {
                    text.LineHeight = Math.Ceiling(size * 1.5);
                    text.Height = text.LineHeight * lines;
                    text.MaxLines = lines; text.TextWrapping = TextWrapping.Wrap;
                    text.TextTrimming = TextTrimming.CharacterEllipsis;
                    if (ToolTip.GetTip(text) is null)
                        text.Bind(ToolTip.TipProperty, new Binding(nameof(TextBlock.Text)) { Source = text });
                }
            }
            finally { _updating = false; }
        }
    }
}
