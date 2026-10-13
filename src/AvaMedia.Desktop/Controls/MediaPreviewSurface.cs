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
    public static readonly StyledProperty<Size> ContentSizeProperty =
        AvaloniaProperty.Register<MediaPreviewSurface, Size>(nameof(ContentSize));
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<MediaPreviewSurface, string?>(nameof(Title));
    private readonly Border _face = new() { ClipToBounds = true };
    private readonly Border _shadow = new() { IsHitTestVisible = false };
    private readonly AmbientHalo _halo = new() { IsHitTestVisible = false, IsVisible = false };
    private readonly TextBlock _titleText = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
    private readonly Border _title = new() { IsHitTestVisible = false, Opacity = 0, Padding = new(8, 6), Margin = new(8), [!Border.CornerRadiusProperty] = new DynamicResourceExtension("UiSurfaceRadius") };
    private Size _naturalSize;
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
    /// <summary>Display dimensions for a custom viewport with overlays; empty uses the content's natural size.</summary>
    public Size ContentSize { get => GetValue(ContentSizeProperty); set => SetValue(ContentSizeProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public bool AmbientEnabled => !_disposed && UseAmbientLighting && !Skin.UsesCustomChrome(ActualThemeVariant);

    static MediaPreviewSurface() => AffectsMeasure<MediaPreviewSurface>(FitContentProperty, ContentSizeProperty);

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
        _title.Child = _titleText;
        _title.Bind(Border.BackgroundProperty, new DynamicResourceExtension("UiMediaOverlay"));
        _titleText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiMediaText"));
        _titleText.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension("UiFontCaption"));
        Localization.SetIsUserText(_titleText, true);
        _title.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) }];
        Children.Add(_halo); Children.Add(_shadow); Children.Add(_face); Children.Add(_title);
        PointerEntered += (_, _) => _title.Opacity = 1;
        PointerExited += (_, _) => _title.Opacity = 0;
        ActualThemeVariantChanged += (_, _) => RefreshTheme();
        PropertyChanged += (_, change) =>
        {
            if (change.Property == UseAmbientLightingProperty) RefreshTheme();
            if (change.Property == TitleProperty)
            {
                _titleText.Text = Title; ToolTip.SetTip(this, Title);
                _title.IsVisible = !string.IsNullOrEmpty(Title); InvalidateMeasure();
            }
        };
        _title.IsVisible = false;
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
        if (!FitContent) { _face.Measure(availableSize); return _face.DesiredSize; }
        _naturalSize = ContentSize;
        if (_naturalSize.Width <= 0 || _naturalSize.Height <= 0)
        {
            // Measuring unconstrained preserves the source ratio, including layout rotation and sheet grids.
            _face.Measure(Size.Infinity); _naturalSize = _face.DesiredSize;
        }
        var size = Contain(_naturalSize, availableSize);
        _face.Measure(size); _title.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The shadow is outside the Viewbox, so its radius stays in display pixels at every zoom level.
        var size = FitContent ? Contain(_naturalSize, finalSize) : finalSize;
        var rect = new Rect((finalSize.Width - size.Width) / 2, (finalSize.Height - size.Height) / 2, size.Width, size.Height);
        _face.Arrange(rect); _shadow.Arrange(rect); _halo.Arrange(rect);
        var titleHeight = Math.Min(size.Height, _title.DesiredSize.Height);
        _title.Arrange(new Rect(rect.X, rect.Bottom - titleHeight, size.Width, titleHeight));
        return finalSize;
    }

    private static Size Contain(Size source, Size available)
    {
        if (source.Width <= 0 || source.Height <= 0 || !double.IsFinite(source.Width) || !double.IsFinite(source.Height))
            return new Size(double.IsFinite(available.Width) ? available.Width : 0, double.IsFinite(available.Height) ? available.Height : 0);
        var scale = Math.Min(available.Width / source.Width, available.Height / source.Height);
        if (!double.IsFinite(scale)) scale = 1;
        return source * Math.Max(0, scale);
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
