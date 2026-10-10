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

/// <summary>A fixed percentage cell: increases roll up, decreases roll down.</summary>
public sealed class RollingPercentage : TemplatedControl
{
    public static readonly StyledProperty<double> PhaseProperty = AvaloniaProperty.Register<RollingPercentage, double>(nameof(Phase), 1);
    public double Phase { get => GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }
    private int? _value;
    private string _text = "—";
    private string _previous = "—";
    private int _direction;

    static RollingPercentage()
    {
        AffectsMeasure<RollingPercentage>(FontSizeProperty, FontFamilyProperty);
        AffectsRender<RollingPercentage>(PhaseProperty, FontSizeProperty, FontFamilyProperty, FontFeaturesProperty,
            FontWeightProperty, FontStyleProperty, ForegroundProperty);
    }
    public RollingPercentage()
    {
        ClipToBounds = true; FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") };
    }

    protected override Size MeasureOverride(Size availableSize) => new(Math.Ceiling(FontSize * 4.5), Math.Ceiling(FontSize * 1.65));

    public void Update(double? value)
    {
        int? next = value is { } percent && double.IsFinite(percent) ? (int)Math.Round(Math.Clamp(percent, 0, 100)) : null;
        if (next == _value) return;
        Motion.Cancel(this);
        _previous = _text;
        var animate = next.HasValue && _value.HasValue && Motion.CanAnimate(this);
        _direction = next > _value ? -1 : 1;
        _value = next; _text = next?.ToString(CultureInfo.InvariantCulture) + (next.HasValue ? "%" : "—");
        AutomationProperties.SetHelpText(this, _text); InvalidateVisual();
        Phase = 1;
        if (!animate) return;
        var animation = new Animation { Duration = TimeSpan.FromMilliseconds(180), Easing = new CubicEaseOut(), FillMode = FillMode.None };
        animation.Children.Add(new KeyFrame { Cue = new Cue(0), Setters = { new Setter(PhaseProperty, 0d) } });
        animation.Children.Add(new KeyFrame { Cue = new Cue(1), Setters = { new Setter(PhaseProperty, 1d) } });
        Motion.Run(this, animation);
    }

    public override void Render(DrawingContext context)
    {
        var phase = Math.Clamp(Phase, 0, 1);
        using var clip = context.PushClip(new Rect(Bounds.Size));
        Draw(_text, _direction * (phase - 1) * Bounds.Height);
        if (phase < 1) Draw(_previous, _direction * phase * Bounds.Height);
        void Draw(string value, double offset)
        {
            var text = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyle, FontWeight), FontSize, Foreground ?? Brushes.Black);
            text.SetFontFeatures(FontFeatures);
            context.DrawText(text, new Point(Bounds.Width - text.WidthIncludingTrailingWhitespace, (Bounds.Height - text.Height) / 2 + offset));
        }
    }
}
