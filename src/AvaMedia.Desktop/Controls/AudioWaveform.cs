using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;

public sealed class AudioWaveform : Control
{
    public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<AudioWaveform, IBrush?>(nameof(Accent));
    public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    private double[] _peaks = [];
    public AudioWaveform() { MinHeight = 100; Bind(AccentProperty, new DynamicResourceExtension("UiAccent")); }
    public void SetSamples(byte[] pcm)
    {
        var count = pcm.Length / 2; var bars = Math.Min(180,count); _peaks = new double[bars];
        for (var bar = 0; bar < bars; bar++)
            for (var index = bar * count / bars; index < (bar + 1) * count / bars; index++)
                _peaks[bar] = Math.Max(_peaks[bar], Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(index * 2, 2))) / 32768d);
        InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context); if (_peaks.Length == 0) return;
        var step = Bounds.Width / _peaks.Length; var center = Bounds.Height / 2;
        for (var index = 0; index < _peaks.Length; index++)
        { var height = Math.Max(2, _peaks[index] * Bounds.Height * .8); context.DrawRectangle(Accent,null,new Rect(index * step,center - height / 2,Math.Max(1,step - 1),height),1,1); }
    }
}
