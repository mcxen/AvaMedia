using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

/// <summary>Fixed footer slot; stage progress uses measured bytes, inference effects indicate activity only.</summary>
public sealed class ModelActivityIndicator : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<ModelActivityIndicator, IBrush?>(nameof(Foreground));
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public static readonly StyledProperty<IBrush?> ShadowBrushProperty =
        AvaloniaProperty.Register<ModelActivityIndicator, IBrush?>(nameof(ShadowBrush));
    public IBrush? ShadowBrush { get => GetValue(ShadowBrushProperty); set => SetValue(ShadowBrushProperty, value); }
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ModelActivityIndicator, IBrush?>(nameof(TrackBrush));
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<ModelActivityIndicator, IBrush?>(nameof(HighlightBrush));
    public IBrush? HighlightBrush { get => GetValue(HighlightBrushProperty); set => SetValue(HighlightBrushProperty, value); }
    private static readonly string[] Effects = ["chase", "spark", "neural", "layers", "ripple", "converge"];
    private readonly DispatcherTimer _frames = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _retire = new();
    private Dictionary<string, ModelRuntimeStatus> _known = [];
    private IReadOnlyList<ModelRuntimeStatus> _snapshot = [];
    private ModelRuntimeStatus? _current, _paint;
    private Window? _window;
    private string _root = "";
    private int _effect, _previousEffect;
    private bool _attached, _display;
    private int _refreshQueued;
    private long _effectStarted = Stopwatch.GetTimestamp(), _fadeStarted;
    private long _previousEffectStarted, _switchStarted;
    private double _nextEffectSeconds = 6;
    private double _fadeFrom;

    static ModelActivityIndicator() => AffectsRender<ModelActivityIndicator>(ForegroundProperty, ShadowBrushProperty, TrackBrushProperty, HighlightBrushProperty);

    public ModelActivityIndicator()
    {
        Width = 132; Height = 20; Opacity = 0; IsHitTestVisible = false;
        _frames.Tick += (_, _) => Frame();
        _retire.Tick += (_, _) => { _retire.Stop(); Refresh(false); };
        AttachedToVisualTree += (_, _) => Attach();
        DetachedFromVisualTree += (_, _) => Detach();
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty) PresentationChanged();
        };
    }

    public void Configure(string root)
    {
        _root = root;
        if (_attached) Refresh(false);
    }
    private bool Visible => _attached && IsEffectivelyVisible
        && _window is { IsVisible: true, WindowState: not WindowState.Minimized };
    private void Attach()
    {
        if (_attached) return;
        _attached = true; _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null) _window.PropertyChanged += WindowChanged;
        MediaTagRuntime.Changed += RuntimeChanged;
        Localization.Changed += LanguageChanged;
        Motion.PreferencesChanged += MotionChanged;
        Refresh(false);
    }
    private void Detach()
    {
        _attached = false; _frames.Stop(); _retire.Stop();
        MediaTagRuntime.Changed -= RuntimeChanged;
        Localization.Changed -= LanguageChanged;
        Motion.PreferencesChanged -= MotionChanged;
        if (_window is not null) _window.PropertyChanged -= WindowChanged;
        _window = null; _known.Clear(); _current = _paint = null; _display = false; Opacity = 0;
    }
    private void WindowChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == Window.IsVisibleProperty || change.Property == Window.WindowStateProperty)
            PresentationChanged();
    }
    private void PresentationChanged()
    {
        if (Visible) Refresh(false);
        else
        {
            _frames.Stop(); _retire.Stop();
            _current = _paint = null; _display = false; Opacity = 0; IsHitTestVisible = false;
            UpdateDescription();
        }
    }
    private void MotionChanged() { UpdateFrames(); InvalidateVisual(); }
    private void LanguageChanged(object? sender, EventArgs args) => UpdateDescription();
    private void RuntimeChanged(string root)
    {
        if (!BatchRename.PathComparer.Equals(root, _root) || Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (Visible) Refresh(true);
        }, DispatcherPriority.Background);
    }
    private void Refresh(bool showCompletion)
    {
        if (_root.Length == 0 || !_attached) return;
        _snapshot = MediaTagRuntime.Snapshot(_root);
        var active = _snapshot.FirstOrDefault(value => value.State == ModelLoadState.InUse)
            ?? _snapshot.Where(value => value.Preparing).OrderBy(value => value.StartedUtc).FirstOrDefault();
        if (active is not null) { _retire.Stop(); Present(active); }
        else
        {
            var completed = showCompletion ? _snapshot.FirstOrDefault(value =>
                _known.TryGetValue(value.Id, out var old) &&
                (value.State == ModelLoadState.Ready && old.State is not (ModelLoadState.Ready or ModelLoadState.Failed)
                || value.State == ModelLoadState.Failed && old.State != ModelLoadState.Failed)) : null;
            if (completed is not null)
            {
                Present(completed); _retire.Stop();
                _retire.Interval = TimeSpan.FromMilliseconds(completed.State == ModelLoadState.Ready ? 900 : 3000);
                if (Visible) _retire.Start();
            }
            else if (!_retire.IsEnabled || !_snapshot.Any(value => value.Id == _current?.Id && value.State == _current.State))
                Present(null);
        }
        _known = _snapshot.ToDictionary(value => value.Id);
        UpdateDescription();
    }
    private void Present(ModelRuntimeStatus? value)
    {
        if (_current?.Id != value?.Id || _current?.State != value?.State)
        {
            _effectStarted = Stopwatch.GetTimestamp(); _switchStarted = 0;
            if (value?.State == ModelLoadState.InUse) { _effect = 0; _nextEffectSeconds = 4 + Random.Shared.NextDouble() * 3; }
        }
        _current = value;
        if (value is not null) _paint = value;
        var display = value is not null;
        if (display != _display)
        {
            _fadeStarted = Stopwatch.GetTimestamp(); _fadeFrom = Opacity; _display = display;
        }
        IsHitTestVisible = display;
        UpdateDescription(); UpdateFrames(); InvalidateVisual();
    }
    private void UpdateDescription()
    {
        var lines = _display ? _snapshot.Where(value => value.Preparing || value.State == ModelLoadState.InUse || value.Id == _current?.Id)
            .Select(Describe).ToArray() : [];
        var description = string.Join(Environment.NewLine, lines);
        ToolTip.SetTip(this, description.Length == 0 ? null : description);
        AutomationProperties.SetName(this, description);
    }
    private static string Describe(ModelRuntimeStatus value)
    {
        var state = value.State switch
        {
            ModelLoadState.Loading => "加载中", ModelLoadState.Warming => "预热中", ModelLoadState.Ready => "已就绪",
            ModelLoadState.InUse => "使用中", ModelLoadState.Failed => "加载失败", _ => "未加载"
        };
        var result = Localization.Text(ModelCatalog.Find(value.Id).Name) + " · " + Localization.Text(state);
        if (value.Preparation is { } stage)
        {
            result += " · " + Localization.Text(stage.Stage);
            if (stage.Fraction is { } fraction)
                result += " · " + Localization.Text("当前阶段进度") + " " + (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
            else result += " · " + Localization.Text("当前阶段耗时未知");
        }
        if (value.Error is { Length: > 0 } error) result += " · " + error;
        return result;
    }
    private void UpdateFrames()
    {
        if (!Visible || !Motion.CanAnimate(this))
        { _frames.Stop(); Opacity = _display ? 1 : 0; if (!_display) _paint = null; return; }
        if (Math.Abs(Opacity - (_display ? 1 : 0)) > .001
            || _current is { State: ModelLoadState.InUse } || _current is { Preparing: true } && _current.Preparation?.Fraction is null)
            _frames.Start();
        else { _frames.Stop(); Opacity = _display ? 1 : 0; if (!_display) _paint = null; }
    }
    private void Frame()
    {
        if (!Visible || !Motion.CanAnimate(this)) { UpdateFrames(); return; }
        var target = _display ? 1d : 0d;
        var phase = Math.Clamp(Stopwatch.GetElapsedTime(_fadeStarted).TotalSeconds / .16, 0, 1);
        Opacity = _fadeFrom + (target - _fadeFrom) * (1 - Math.Pow(1 - phase, 3));
        if (_current?.State == ModelLoadState.InUse && Stopwatch.GetElapsedTime(_effectStarted).TotalSeconds >= _nextEffectSeconds)
        {
            _previousEffect = _effect; _previousEffectStarted = _effectStarted;
            var next = Random.Shared.Next(Effects.Length - 1);
            _effect = next >= _effect ? next + 1 : next;
            _effectStarted = _switchStarted = Stopwatch.GetTimestamp();
            _nextEffectSeconds = 4 + Random.Shared.NextDouble() * 3;
        }
        InvalidateVisual(); UpdateFrames();
    }
    public override void Render(DrawingContext context)
    {
        var value = _current ?? _paint;
        if (value is null) return;
        var brush = Foreground ?? Brushes.White;
        var classic = ActualThemeVariant == Skin.MacOS9;
        var glyphBrush = classic ? ShadowBrush ?? brush : brush;
        var pen = new Pen(glyphBrush, 1.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var top = (Bounds.Height - 16) / 2;
        if (value.State == ModelLoadState.Ready)
        {
            context.DrawLine(pen, new(1, top + 8), new(5, top + 12));
            context.DrawLine(pen, new(5, top + 12), new(13, top + 3));
        }
        else if (value.State == ModelLoadState.Failed)
        {
            context.DrawLine(pen, new(7, top + 2), new(7, top + 10));
            context.DrawEllipse(glyphBrush, null, new(7, top + 14), 1, 1);
        }
        else
        {
            context.DrawRectangle(null, pen, new Rect(2, top + 3, 10, 10), 2, 2);
            for (var pin = 0; pin < 3; pin++)
            {
                var x = 4 + pin * 3;
                context.DrawLine(pen, new(x, top), new(x, top + 3));
                context.DrawLine(pen, new(x, top + 13), new(x, top + 16));
            }
            context.DrawLine(pen, new(5, top + 6), new(9, top + 10));
            context.DrawEllipse(glyphBrush, null, new(5, top + 6), 1, 1);
            context.DrawEllipse(glyphBrush, null, new(9, top + 10), 1, 1);
        }
        var time = Stopwatch.GetElapsedTime(_effectStarted).TotalSeconds;
        var motion = Motion.CanAnimate(this);
        var fraction = value.Preparation?.Fraction;
        var radius = classic ? 1.5 : 2.5;
        var classicShadow = classic && ShadowBrush is { } shadow ? new Pen(shadow, 1, new DashStyle([1, 1], 0)) : null;
        var classicOutline = classicShadow is not null ? new Pen(ShadowBrush, .8) : null;
        var highlight = classic ? HighlightBrush ?? brush : brush;
        var bevelPen = classic ? new Pen(highlight, .8) : null;
        for (var i = 0; i < 24; i++)
        {
            var column = i % 12; var row = i / 12;
            var rect = new Rect((classic ? 18.5 : 20) + column * 9.5, top + row * 9, 7.5, 7);
            if (classicShadow is not null)
            {
                context.DrawLine(classicShadow, new(rect.Right + 1, rect.Y + 4), new(rect.Right + 1, rect.Bottom + 1));
                context.DrawLine(classicShadow, new(rect.X + 2, rect.Bottom + 1), new(rect.Right + 1, rect.Bottom + 1));
            }
            if (classic) context.DrawRectangle(TrackBrush ?? brush, null, rect, radius, radius);
            else using (context.PushOpacity(.18)) context.DrawRectangle(brush, null, rect, radius, radius);
            var fill = value.Preparing ? fraction is { } progress ? Math.Clamp(progress * 24 - i, 0, 1) : 0 : value.Loaded ? 1 : 0;
            if (fill > 0)
            {
                using var clip = context.PushClip(new Rect(rect.X, rect.Y, rect.Width * fill, rect.Height));
                using var opacity = context.PushOpacity(classic ? 1 : value.State == ModelLoadState.InUse ? .55 : .9);
                context.DrawRectangle(brush, null, rect, radius, radius);
            }
            var light = value.State == ModelLoadState.InUse
                ? motion ? ActivityLight(column, row, time) * .45 : (column is 4 or 7 ? .35 : 0)
                : value.Preparing && fraction is null
                    ? motion ? Gaussian(column - (time * 4 % 16 - 2), 1.15) * .7 : (column is 5 or 6 ? .55 : 0) : 0;
            if (light > .005)
            {
                using var opacity = context.PushOpacity(light);
                context.DrawRectangle(highlight, null, rect, radius, radius);
            }
            if (classicOutline is not null)
            {
                context.DrawRectangle(null, classicOutline, rect, radius, radius);
                context.DrawLine(bevelPen!, new(rect.X + 2, rect.Y + .8), new(rect.Right - 2, rect.Y + .8));
            }
        }
    }
    private double ActivityLight(int column, int row, double time)
    {
        var next = Brightness(Effects[_effect], column, row, time);
        if (_switchStarted == 0) return next;
        var blend = Math.Clamp(Stopwatch.GetElapsedTime(_switchStarted).TotalSeconds / .24, 0, 1);
        if (blend >= 1) return next;
        blend = blend * blend * (3 - 2 * blend);
        return Brightness(Effects[_previousEffect], column, row, Stopwatch.GetElapsedTime(_previousEffectStarted).TotalSeconds)
            * (1 - blend) + next * blend;
    }
    private static double Brightness(string effect, int column, int row, double time)
    {
        switch (effect)
        {
            case "chase":
                var distance = (time * 4.5 - (row == 0 ? column : 11 - column)) % 12;
                if (distance < 0) distance += 12;
                return distance < 3.5 ? Math.Pow(1 - distance / 3.5, 2) : 0;
            case "spark":
                var tick = (int)(time * 3); var pulse = Math.Sin(time * 3 % 1 * Math.PI);
                var cell = column + row * 12;
                return cell == (tick * 7 + 3) % 24 || cell == (tick * 11 + 17) % 24 ? pulse : 0;
            case "neural":
                return Math.Max(Seed(2, 0, .08), Math.Max(Seed(9, 1, .56), Seed(5, 0, 1.05)));
                double Seed(int x, int y, double at)
                {
                    var d = Math.Abs(column - x) + Math.Abs(row - y) * 1.4;
                    return Gaussian(time % 2.2 - at - d * .115, .105) * Math.Max(.35, 1 - d * .065);
                }
            case "layers":
                var local = time % 2.6 - (row == 0 ? 0 : 1.15);
                return local is < 0 or > 1.3 ? 0 : Gaussian((row == 0 ? column : 11 - column) - (local / 1.3 * 15 - 1.5), .85);
            case "ripple":
                return Gaussian(Math.Abs(column - 5.5) + row * .28 - (time % 2.3 / 2.3 * 8 - .6), .65);
            default:
                var cycle = time % 2.7; var d = Math.Abs(column - 5.5);
                return cycle < 2.05 ? Gaussian(d - (6 - cycle / 2.05 * 6 - row * .25), .65)
                    : Gaussian(d, 1.25) * Math.Max(0, Math.Sin((cycle - 2.05) / .65 * Math.PI));
        }
    }
    private static double Gaussian(double distance, double width) => Math.Exp(-distance * distance / (2 * width * width));
}
