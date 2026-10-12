using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Metadata;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

/// <summary>Shared fitted preview: neutral shadows and a softly animated, optional edge-colour halo.</summary>
public sealed class MediaPreviewSurface : Panel
{
    public static readonly StyledProperty<bool> UseAmbientLightingProperty =
        AvaloniaProperty.Register<MediaPreviewSurface, bool>(nameof(UseAmbientLighting), true);
    public static readonly StyledProperty<bool> FitContentProperty =
        AvaloniaProperty.Register<MediaPreviewSurface, bool>(nameof(FitContent), true);
    private readonly Border _face = new() { ClipToBounds = true };
    private readonly Border _shadow = new() { IsHitTestVisible = false };
    private readonly AmbientHalo _halo = new() { IsHitTestVisible = false, IsVisible = false };
    private MediaPreviewColorSampler? _sampler;
    private CancellationTokenSource? _sampling;
    private MediaPreviewColors.Edges?[] _colors = [];
    private Frame?[] _frames = [];
    private int _columns = 1, _rows = 1;
    private bool _disposed;

    [Content]
    public Control? PreviewContent { get => _face.Child; set => _face.Child = value; }
    /// <summary>Disable for interactive previews: no colour decoding, sampling or animation is scheduled.</summary>
    public bool UseAmbientLighting { get => GetValue(UseAmbientLightingProperty); set => SetValue(UseAmbientLightingProperty, value); }
    /// <summary>Fit a Viewbox's measured image, or fill the existing crop/compare viewport without moving its overlays.</summary>
    public bool FitContent { get => GetValue(FitContentProperty); set => SetValue(FitContentProperty, value); }
    public bool AmbientEnabled => !_disposed && UseAmbientLighting && !Skin.UsesCustomChrome(ActualThemeVariant);

    static MediaPreviewSurface() => AffectsArrange<MediaPreviewSurface>(FitContentProperty);

    public MediaPreviewSurface()
    {
        // Keep shadow drawing outside the face's rounded clip, including for structural placeholders.
        _shadow.Bind(Border.BackgroundProperty, new DynamicResourceExtension("UiTransparent"));
        _shadow.Bind(Border.BoxShadowProperty, new DynamicResourceExtension("UiMediaPreviewShadow"));
        _shadow.Bind(Border.CornerRadiusProperty, new DynamicResourceExtension("UiMediaPreviewRadius"));
        _face.Bind(Border.CornerRadiusProperty, new DynamicResourceExtension("UiMediaPreviewRadius"));
        _halo.Bind(AmbientHalo.RadiusProperty, new DynamicResourceExtension("UiMediaPreviewRadius"));
        _halo.Bind(OpacityProperty, new DynamicResourceExtension("UiMediaPreviewAmbientOpacity"));
        _halo.Bind(AmbientHalo.BlurProperty, new DynamicResourceExtension("UiMediaPreviewAmbientBlur"));
        _halo.Bind(AmbientHalo.SpreadProperty, new DynamicResourceExtension("UiMediaPreviewAmbientSpread"));
        _halo.Transitions = [new ColorTransition { Property = AmbientHalo.ColorProperty, Duration = TimeSpan.FromMilliseconds(400) }];
        Children.Add(_halo); Children.Add(_shadow); Children.Add(_face);
        ActualThemeVariantChanged += (_, _) => RefreshTheme();
        PropertyChanged += (_, change) => { if (change.Property == UseAmbientLightingProperty) RefreshTheme(); };
        AttachedToVisualTree += (_, _) => RestartSampling();
        DetachedFromVisualTree += (_, _) => CancelSampling();
    }

    public void BeginGrid(int columns, int rows)
    {
        if (columns < 1 || rows < 1) throw new ArgumentOutOfRangeException(nameof(columns));
        CancelSampling();
        _columns = columns; _rows = rows;
        _frames = new Frame?[checked(columns * rows)]; _colors = new MediaPreviewColors.Edges?[_frames.Length];
        _halo.IsVisible = false;
        _halo.Color = Colors.Transparent;
        if (AmbientEnabled) _sampling = new();
    }

    /// <summary>Pass immutable thumbnail bytes and a cache key identifying the file revision and sampled frame.</summary>
    public void SetFrame(int index, byte[] encoded, string cacheKey)
    {
        if (_disposed) return;
        if ((uint)index >= (uint)_frames.Length) throw new ArgumentOutOfRangeException(nameof(index));
        // Interior tiles cannot affect the border colour; skip even decoding them.
        if (index / _columns != 0 && index / _columns != _rows - 1 && index % _columns != 0 && index % _columns != _columns - 1) return;
        var frame = new Frame(encoded, cacheKey); _frames[index] = frame;
        if (AmbientEnabled && _sampling is { } work) _ = SampleFrame(index, frame, work.Token);
    }

    public void ClearAmbient()
    {
        CancelSampling(); _frames = []; _colors = [];
        _halo.IsVisible = false; _halo.Color = Colors.Transparent;
    }

    public void Dispose()
    {
        ClearAmbient(); _disposed = true;
    }

    private async Task SampleFrame(int index, Frame frame, CancellationToken token)
    {
        try
        {
            var color = await (_sampler ??= new()).SampleAsync(frame.Encoded, frame.Key, token);
            if (token.IsCancellationRequested || !AmbientEnabled || _frames[index] != frame) return;
            _colors[index] = color;
            if (MediaPreviewColors.AverageBorder(_colors, _columns, _rows) is { } rgb)
            {
                _halo.IsVisible = true;
                _halo.Color = Color.FromRgb((byte)Math.Round(rgb.R), (byte)Math.Round(rgb.G), (byte)Math.Round(rgb.B));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Optional decoration must never interrupt the preview. */ }
    }

    private void RefreshTheme()
    {
        if (!AmbientEnabled) { CancelSampling(); _halo.IsVisible = false; }
        else RestartSampling();
    }

    private void RestartSampling()
    {
        CancelSampling();
        if (!AmbientEnabled) return;
        _sampling = new();
        for (var i = 0; i < _frames.Length; i++)
            if (_frames[i] is { } frame) _ = SampleFrame(i, frame, _sampling.Token);
    }

    private void CancelSampling()
    {
        _sampling?.Cancel(); _sampling?.Dispose(); _sampling = null;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _face.Measure(availableSize);
        return _face.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The shadow is outside the Viewbox, so its radius stays in display pixels at every zoom level.
        var size = FitContent ? _face.DesiredSize : finalSize;
        var rect = new Rect((finalSize.Width - size.Width) / 2, (finalSize.Height - size.Height) / 2, size.Width, size.Height);
        _face.Arrange(rect); _shadow.Arrange(rect); _halo.Arrange(rect);
        return finalSize;
    }

    private sealed record Frame(byte[] Encoded, string Key);

    private sealed class AmbientHalo : Control
    {
        public static readonly StyledProperty<Color> ColorProperty = AvaloniaProperty.Register<AmbientHalo, Color>(nameof(Color));
        public static readonly StyledProperty<CornerRadius> RadiusProperty = AvaloniaProperty.Register<AmbientHalo, CornerRadius>(nameof(Radius));
        public static readonly StyledProperty<double> BlurProperty = AvaloniaProperty.Register<AmbientHalo, double>(nameof(Blur));
        public static readonly StyledProperty<double> SpreadProperty = AvaloniaProperty.Register<AmbientHalo, double>(nameof(Spread));
        public Color Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
        public CornerRadius Radius => GetValue(RadiusProperty);
        public double Blur => GetValue(BlurProperty);
        public double Spread => GetValue(SpreadProperty);
        static AmbientHalo() => AffectsRender<AmbientHalo>(ColorProperty, RadiusProperty, BlurProperty, SpreadProperty);
        public override void Render(DrawingContext context)
        {
            if (Color.A == 0) return;
            context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size), Radius.TopLeft, Radius.TopLeft,
                new BoxShadows(new BoxShadow { Color = Color, Blur = Blur, Spread = Spread }));
        }
    }
}
