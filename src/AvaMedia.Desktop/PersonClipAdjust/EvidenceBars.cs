using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaMedia.Core;

namespace AvaMedia.Desktop.PersonClipAdjust;

internal sealed class EvidenceBars : Control
{
    private static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<EvidenceBars, IBrush?>("Accent");
    private static readonly StyledProperty<IBrush?> OutsideProperty = AvaloniaProperty.Register<EvidenceBars, IBrush?>("Outside");
    private static readonly StyledProperty<IBrush?> UncertainProperty = AvaloniaProperty.Register<EvidenceBars, IBrush?>("Uncertain");
    private IReadOnlyList<PersonClipSample> _samples = [];
    private double _start, _end;
    static EvidenceBars() => AffectsRender<EvidenceBars>(AccentProperty, OutsideProperty, UncertainProperty);
    public EvidenceBars()
    {
        Height = 26;
        Bind(AccentProperty, new DynamicResourceExtension("UiAccent"));
        Bind(OutsideProperty, new DynamicResourceExtension("AcTextTertiary"));
        Bind(UncertainProperty, new DynamicResourceExtension("UiWarning"));
        Avalonia.Automation.AutomationProperties.SetName(this, Localization.Text("检测依据"));
    }
    public void Update(IReadOnlyList<PersonClipSample> samples, double start, double end) { _samples = samples; _start = start; _end = end; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        if (_samples.Count == 0 || Bounds.Width <= 0) return;
        var width = Bounds.Width / _samples.Count;
        for (var i = 0; i < _samples.Count; i++)
        {
            var sample = _samples[i]; var height = Math.Max(2, Math.Clamp(sample.Score, 0, 1) * Bounds.Height);
            var property = sample.Flags.HasFlag(PersonClipSampleFlags.Uncertain) ? UncertainProperty : sample.Seconds >= _start && sample.Seconds <= _end && sample.Analysed ? AccentProperty : OutsideProperty;
            context.DrawRectangle(GetValue(property), null, new Rect(i * width, Bounds.Height - height, Math.Max(.5, width - 2), height), 1, 1);
        }
    }
}
