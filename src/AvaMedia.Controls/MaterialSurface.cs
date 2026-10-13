using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace AvaMedia.Controls;

/// <summary>A popup surface with native macOS blur and a theme brush fallback.</summary>
public sealed class MaterialSurface : ContentControl
{
    public static readonly StyledProperty<bool> MaterialEnabledProperty =
        AvaloniaProperty.Register<MaterialSurface, bool>(nameof(MaterialEnabled), true);
    public static readonly StyledProperty<Color> MaterialTintColorProperty =
        AvaloniaProperty.Register<MaterialSurface, Color>(nameof(MaterialTintColor));
    public static readonly StyledProperty<double> MaterialTintOpacityProperty =
        AvaloniaProperty.Register<MaterialSurface, double>(nameof(MaterialTintOpacity), .8);
    public static readonly StyledProperty<double> MaterialOpacityProperty =
        AvaloniaProperty.Register<MaterialSurface, double>(nameof(MaterialOpacity), .5);
    public static readonly StyledProperty<BoxShadows> BoxShadowProperty = Border.BoxShadowProperty.AddOwner<MaterialSurface>();
    public static readonly StyledProperty<bool> IsAcrylicActiveProperty =
        AvaloniaProperty.Register<MaterialSurface, bool>(nameof(IsAcrylicActive));
    private TopLevel? _root;
    private IReadOnlyList<WindowTransparencyLevel>? _previousLevels;
    private IDisposable? _rootBackground;
    private MacPopupMaterial? _nativePopup;
    private bool _nativeClipUnavailable;
    public bool MaterialEnabled { get => GetValue(MaterialEnabledProperty); set => SetValue(MaterialEnabledProperty, value); }
    public Color MaterialTintColor { get => GetValue(MaterialTintColorProperty); set => SetValue(MaterialTintColorProperty, value); }
    public double MaterialTintOpacity { get => GetValue(MaterialTintOpacityProperty); set => SetValue(MaterialTintOpacityProperty, value); }
    public double MaterialOpacity { get => GetValue(MaterialOpacityProperty); set => SetValue(MaterialOpacityProperty, value); }
    public BoxShadows BoxShadow { get => GetValue(BoxShadowProperty); set => SetValue(BoxShadowProperty, value); }
    public bool IsAcrylicActive => GetValue(IsAcrylicActiveProperty);
    // Each surface owns its material: ExperimentalAcrylicBorder adjusts compensation per root.
    public ExperimentalAcrylicMaterial Material { get; } = new();

    public MaterialSurface() => Bind(ThemeProperty, new DynamicResourceExtension("AcMaterialSurface"));

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _root = TopLevel.GetTopLevel(this);
        if (_root is not null) _root.PropertyChanged += RootChanged;
        LayoutUpdated += SurfaceLayout;
        Refresh();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_root is not null) _root.PropertyChanged -= RootChanged;
        LayoutUpdated -= SurfaceLayout;
        RestoreRoot(); _root = null;
        SetValue(IsAcrylicActiveProperty, false);
        base.OnDetachedFromVisualTree(e);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MaterialEnabledProperty || change.Property == MaterialTintColorProperty
            || change.Property == MaterialTintOpacityProperty || change.Property == MaterialOpacityProperty
            || change.Property == BackgroundProperty || change.Property == CornerRadiusProperty) Refresh();
    }
    private void RootChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.ActualTransparencyLevelProperty) UpdateActive();
    }
    private void SurfaceLayout(object? sender, EventArgs e) => UpdateActive();
    private void Refresh()
    {
        if (Background is ISolidColorBrush fallback) Material.FallbackColor = fallback.Color;
        Material.TintColor = MaterialTintColor;
        Material.TintOpacity = MaterialTintOpacity;
        Material.MaterialOpacity = MaterialOpacity;
        // Native popup hosts can blur what lies behind the popup. An inline popover or
        // overlay popup cannot blur its parent window using the Avalonia 11 native API.
        if (OperatingSystem.IsMacOS() && _root is PopupRoot popup && MaterialEnabled)
        {
            if (_previousLevels is null)
            {
                _previousLevels = _root.TransparencyLevelHint;
                _rootBackground = _root.SetValue(TopLevel.BackgroundProperty, Brushes.Transparent, Avalonia.Data.BindingPriority.Animation);
                _nativePopup = MacPopupMaterial.TryCreate(popup);
            }
            // Native blur must be clipped too; otherwise its rectangular host shows
            // through the transparent corners of the Avalonia surface.
            WindowTransparencyLevel[] levels = _nativePopup is not null && HasUniformRadius && !_nativeClipUnavailable
                ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Transparent]
                : [WindowTransparencyLevel.Transparent];
            // Reapplying the same hints makes the Avalonia 11 native backend advance
            // to the next fallback, so tint/radius changes must not resend them.
            if (!_root.TransparencyLevelHint.SequenceEqual(levels)) _root.TransparencyLevelHint = levels;
        }
        else if (!MaterialEnabled) RestoreRoot();
        UpdateActive();
    }
    private bool HasUniformRadius => CornerRadius.TopLeft == CornerRadius.TopRight
        && CornerRadius.TopLeft == CornerRadius.BottomRight && CornerRadius.TopLeft == CornerRadius.BottomLeft;
    private void UpdateActive()
    {
        var active = MaterialEnabled && HasUniformRadius && _nativePopup is not null && !_nativeClipUnavailable
            && _root is PopupRoot && (_root.ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur
                || _root.ActualTransparencyLevel == WindowTransparencyLevel.Blur);
        if (active && Bounds.Width > 0 && Bounds.Height > 0)
        {
            var origin = this.TranslatePoint(default, _root!);
            var end = this.TranslatePoint(new Point(Bounds.Width, Bounds.Height), _root!);
            active = origin.HasValue && end.HasValue && _nativePopup!.Apply(new Rect(origin.Value, end.Value), CornerRadius.TopLeft);
            if (!active)
            {
                _nativeClipUnavailable = true;
                if (!_root!.TransparencyLevelHint.SequenceEqual(new[] { WindowTransparencyLevel.Transparent }))
                    _root.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            }
        }
        else if (active) active = false;
        else _nativePopup?.Restore();
        SetValue(IsAcrylicActiveProperty, active);
    }
    private void RestoreRoot()
    {
        var nativePopup = _nativePopup; _nativePopup = null;
        nativePopup?.Dispose();
        _nativeClipUnavailable = false;
        if (_previousLevels is not null && _root is not null) _root.TransparencyLevelHint = _previousLevels;
        _previousLevels = null; _rootBackground?.Dispose(); _rootBackground = null;
    }
}
