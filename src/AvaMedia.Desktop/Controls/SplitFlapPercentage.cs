using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace AvaMedia.Desktop.Controls;

/// <summary>Three fixed digit slots. Only changed digits fold around their centre hinge.</summary>
public sealed class SplitFlapPercentage : TemplatedControl
{
    private const double FlipMilliseconds = 190, StaggerMilliseconds = 16;
    public static readonly StyledProperty<double> PhaseProperty = AvaloniaProperty.Register<SplitFlapPercentage, double>(nameof(Phase), 1);
    public double Phase { get => GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }
    private int? _value;
    private string _digits = "  —", _previous = "  —";
    private readonly double[] _delays = new double[3];
    private double _durationMilliseconds = FlipMilliseconds;
    private bool _increasing;
    private readonly Dictionary<char, FormattedText> _glyphs = [];
    private (double Digit, double Gap, double Suffix, double Height)? _metrics;

    static SplitFlapPercentage()
    {
        AffectsMeasure<SplitFlapPercentage>(FontSizeProperty, FontFamilyProperty, FontFeaturesProperty, FontWeightProperty, FontStyleProperty);
        AffectsRender<SplitFlapPercentage>(PhaseProperty, FontSizeProperty, FontFamilyProperty, FontFeaturesProperty,
            FontWeightProperty, FontStyleProperty, ForegroundProperty, BackgroundProperty, BorderBrushProperty);
    }
    public SplitFlapPercentage()
    {
        ClipToBounds = true; FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") };
        AutomationProperties.SetHelpText(this, "—");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontSizeProperty || change.Property == FontFamilyProperty || change.Property == FontFeaturesProperty
            || change.Property == FontWeightProperty || change.Property == FontStyleProperty || change.Property == ForegroundProperty)
        { _glyphs.Clear(); _metrics = null; }
    }

    private FormattedText Glyph(char value)
    {
        if (_glyphs.TryGetValue(value, out var text)) return text;
        text = new(value.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, FontWeight), FontSize, Foreground ?? Brushes.Black);
        text.SetFontFeatures(FontFeatures); _glyphs.Add(value, text); return text;
    }

    private (double Digit, double Gap, double Suffix, double Height) Metrics()
    {
        if (_metrics is { } measured) return measured;
        // Measure the entire range, including an unavailable reading, never just the current value.
        var widest = "0123456789—".Max(character => Glyph(character).WidthIncludingTrailingWhitespace);
        return (_metrics = (Math.Ceiling(widest + 2), Math.Max(1, FontSize * .08), Glyph('%').WidthIncludingTrailingWhitespace,
            Math.Ceiling(Math.Max(FontSize * 1.65, Glyph('0').Height + 2)))).Value;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (digit, gap, suffix, height) = Metrics();
        return new(Math.Ceiling(Math.Max(FontSize * 4.5, digit * 3 + gap * 2 + suffix + 2)), height);
    }

    private double DigitPhase(int index, double phase) => Math.Clamp((phase * _durationMilliseconds - _delays[index]) / FlipMilliseconds, 0, 1);

    public void Update(double? value)
    {
        int? next = value is { } percent && double.IsFinite(percent) ? (int)Math.Round(Math.Clamp(percent, 0, 100)) : null;
        if (next == _value) return;
        var phase = Math.Clamp(Phase, 0, 1);
        // A newer sample replaces an unfinished turn instead of queueing stale readings.
        _previous = new string(Enumerable.Range(0, 3).Select(index => DigitPhase(index, phase) < .5 ? _previous[index] : _digits[index]).ToArray());
        Motion.Cancel(this);
        var animate = next.HasValue && _value.HasValue && Motion.CanAnimate(this);
        _increasing = next > _value;
        _value = next; _digits = next?.ToString(CultureInfo.InvariantCulture).PadLeft(3) ?? "  —";
        AutomationProperties.SetHelpText(this, next.HasValue ? next.Value.ToString(CultureInfo.InvariantCulture) + "%" : "—");
        Phase = 1;
        var changed = 0;
        for (var index = 2; index >= 0; index--)
            _delays[index] = _previous[index] == _digits[index] ? 0 : changed++ * StaggerMilliseconds;
        _durationMilliseconds = FlipMilliseconds + Math.Max(0, changed - 1) * StaggerMilliseconds;
        InvalidateVisual();
        if (!animate || changed == 0) return;
        var animation = new Animation { Duration = TimeSpan.FromMilliseconds(_durationMilliseconds), Easing = new LinearEasing(), FillMode = FillMode.None };
        animation.Children.Add(new KeyFrame { Cue = new Cue(0), Setters = { new Setter(PhaseProperty, 0d) } });
        animation.Children.Add(new KeyFrame { Cue = new Cue(1), Setters = { new Setter(PhaseProperty, 1d) } });
        Motion.Run(this, animation);
    }

    public override void Render(DrawingContext context)
    {
        var phase = Math.Clamp(Phase, 0, 1);
        var (digit, gap, suffix, _) = Metrics();
        var left = Bounds.Width - (digit * 3 + gap * 2 + suffix + 2);
        var face = Background ?? Brushes.White;
        var pen = new Pen(BorderBrush ?? Foreground ?? Brushes.Gray, .5);
        using var clip = context.PushClip(new Rect(Bounds.Size));
        for (var index = 0; index < 3; index++)
        {
            var tile = new Rect(left + index * (digit + gap), 1, digit, Math.Max(0, Bounds.Height - 2));
            var progress = DigitPhase(index, phase);
            if (_previous[index] == _digits[index] || progress >= 1) DrawFace(_digits[index], tile);
            else if (progress <= 0) DrawFace(_previous[index], tile);
            else
            {
                // Fixed halves behind the turning flap; increasing folds upward, decreasing downward.
                DrawHalf(_increasing ? _previous[index] : _digits[index], tile, top: true);
                DrawHalf(_increasing ? _digits[index] : _previous[index], tile, top: false);
                var outgoing = progress < .5;
                var scale = outgoing ? Math.Cos(progress * Math.PI) : -Math.Cos(progress * Math.PI);
                if (scale > .001)
                {
                    var hinge = tile.Center.Y;
                    using var turn = context.PushTransform(Matrix.CreateScale(1, scale) * Matrix.CreateTranslation(0, hinge * (1 - scale)));
                    DrawHalf(outgoing ? _previous[index] : _digits[index], tile, top: outgoing != _increasing, shade: (1 - scale) * .25);
                }
            }
            // A fixed centre seam and frame make the separate flaps legible at status-bar size.
            context.DrawRectangle(null, pen, tile, 1.5, 1.5);
            context.DrawLine(pen, new Point(tile.Left + .5, tile.Center.Y), new Point(tile.Right - .5, tile.Center.Y));
        }
        if (_value.HasValue) DrawGlyph('%', new Rect(Bounds.Width - suffix, 0, suffix, Bounds.Height));

        void DrawGlyph(char value, Rect tile)
        {
            if (value == ' ') return;
            var text = Glyph(value);
            context.DrawText(text, new Point(tile.Center.X - text.WidthIncludingTrailingWhitespace / 2, tile.Center.Y - text.Height / 2));
        }
        void DrawFace(char value, Rect tile)
        {
            context.DrawRectangle(face, null, tile, 1.5, 1.5);
            DrawGlyph(value, tile);
            using var lowerShade = context.PushOpacity(.035);
            context.DrawRectangle(Brushes.Black, null, new Rect(tile.Left, tile.Center.Y, tile.Width, tile.Height / 2));
        }
        void DrawHalf(char value, Rect tile, bool top, double shade = 0)
        {
            var half = new Rect(tile.Left, top ? tile.Top : tile.Center.Y, tile.Width, tile.Height / 2);
            using var halfClip = context.PushClip(half);
            DrawFace(value, tile);
            using var shadow = context.PushOpacity(shade);
            context.DrawRectangle(Brushes.Black, null, half);
        }
    }
}
