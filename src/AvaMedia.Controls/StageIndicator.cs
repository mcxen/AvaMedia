using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static AvaMedia.Controls.ComponentResources;

namespace AvaMedia.Controls;

/// <summary>状态符号：完成为实心圆白勾；进行中为强调色弧；错误为圈内感叹号。</summary>
public sealed class StatusGlyph : Control
{
    public static readonly StyledProperty<string> StateProperty = AvaloniaProperty.Register<StatusGlyph, string>(nameof(State), "pending");
    public static readonly StyledProperty<double> AngleProperty = AvaloniaProperty.Register<StatusGlyph, double>(nameof(Angle));
    public double Angle { get => GetValue(AngleProperty); set => SetValue(AngleProperty, value); }
    public static readonly StyledProperty<IBrush?> BrushProperty = AvaloniaProperty.Register<StatusGlyph, IBrush?>(nameof(Brush));
    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<StatusGlyph, IBrush?>("Track");
    public static readonly StyledProperty<IBrush?> ContrastProperty = AvaloniaProperty.Register<StatusGlyph, IBrush?>(nameof(Contrast));
    public string State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public IBrush? Brush => GetValue(BrushProperty);
    public IBrush? Contrast => GetValue(ContrastProperty);
    static StatusGlyph() => AffectsRender<StatusGlyph>(StateProperty, BrushProperty, ContrastProperty, AngleProperty, TrackProperty);
    public StatusGlyph()
    {
        Bind(WidthProperty, Resource("AcStepIcon")); Bind(HeightProperty, Resource("AcStepIcon"));
        Bind(ContrastProperty, Resource("UiAccentText")); Bind(TrackProperty, Resource("AcRunningFill")); VerticalAlignment = VerticalAlignment.Center;
        PropertyChanged += (_, change) => { if (change.Property == StateProperty) Refresh(); };
        Refresh();
    }
    private void Refresh()
    {
        Classes.Set("ac-running", State == "running");
        Bind(BrushProperty, Resource(State switch { "done" or "success" => "UiSuccessFill", "running" => "UiAccent", "failed" => "UiDanger", "paused" => "UiWarning", "info" => "UiAccent", "warning" => "UiWarning", "empty" => "AcTextTertiary", _ => "AcTextTertiary" }));
    }
    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height); var center = new Point(size / 2, size / 2); var radius = Math.Max(0, size / 2 - (State == "done" ? 0 : 1));
        if (State == "empty")
        {
            var pen = new Pen(Brush, 1.4); context.DrawRectangle(null, pen, new Rect(size * .14, size * .18, size * .72, size * .64), 3, 3);
            context.DrawLine(pen, new(size * .3, size * .18), new(size * .3, size * .82)); context.DrawLine(pen, new(size * .7, size * .18), new(size * .7, size * .82));
            for (var y = .32; y < .8; y += .18) { context.DrawLine(pen, new(size * .14, size * y), new(size * .3, size * y)); context.DrawLine(pen, new(size * .7, size * y), new(size * .86, size * y)); }
        }
        else if (State == "warning")
        {
            var shape = new StreamGeometry();
            using (var g = shape.Open()) { g.BeginFigure(new(size / 2, 1), false); g.LineTo(new(size - 1, size - 2)); g.LineTo(new(1, size - 2)); g.EndFigure(true); }
            var pen = new Pen(Brush, 1.4, lineJoin: PenLineJoin.Round); context.DrawGeometry(null, pen, shape);
            context.DrawLine(pen, new(size / 2, size * .4), new(size / 2, size * .62)); context.DrawEllipse(Brush, null, new(size / 2, size * .77), .7, .7);
        }
        else if (State is "done" or "success")
        {
            var outline = State == "success";
            context.DrawEllipse(outline ? null : Brush, outline ? new Pen(Brush, 1.4) : null, center, radius, radius);
            var pen = new Pen(outline ? Brush : Contrast, 1.4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawLine(pen, new(size * .26, size * .5), new(size * .43, size * .67));
            context.DrawLine(pen, new(size * .43, size * .67), new(size * .75, size * .32));
        }
        else if (State == "running")
        {
            context.DrawEllipse(null, new Pen(GetValue(TrackProperty), 2), center, radius, radius);
            var angle = Angle * Math.PI / 180 - Math.PI / 2;
            Point P(double a) => new(center.X + radius * Math.Cos(a), center.Y + radius * Math.Sin(a));
            var geometry = new StreamGeometry();
            using (var g = geometry.Open()) { g.BeginFigure(P(angle), false); g.ArcTo(P(angle + Math.PI / 2), new(radius, radius), 0, false, SweepDirection.Clockwise); g.EndFigure(false); }
            context.DrawGeometry(null, new Pen(Brush, 2, lineCap: PenLineCap.Round), geometry);
        }
        else
        {
            var pen = new Pen(Brush, 1.4); context.DrawEllipse(null, pen, center, radius, radius);
            if (State == "info") { context.DrawLine(pen, new(center.X, size * .45), new(center.X, size * .75)); context.DrawEllipse(Brush, null, new(center.X, size * .25), .7, .7); }
            if (State == "failed") { context.DrawLine(pen, new(center.X, size * .25), new(center.X, size * .55)); context.DrawEllipse(Brush, null, new(center.X, size * .73), .7, .7); }
            if (State == "paused") { context.DrawLine(pen, new(size * .4, size * .3), new(size * .4, size * .7)); context.DrawLine(pen, new(size * .6, size * .3), new(size * .6, size * .7)); }
        }
    }
}

public sealed class StageIndicator : Border
{
    private readonly TextBlock _name = new(), _detail = new() { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, LineHeight = 13 },
        _count = new(), _number = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _dot = new();
    private readonly StatusGlyph _done = new() { State = "done" };
    protected override Type StyleKeyOverride => typeof(Border);
    public bool ShowIdleBorder { get; set; } = true;
    public StageIndicator()
    {
        Classes.Add("ac-stage"); UseLayoutRounding = false;
        BindResource(_dot, WidthProperty, "AcStageDot"); BindResource(_dot, HeightProperty, "AcStageDot"); BindResource(_dot, CornerRadiusProperty, "AcStatusRadius");
        var badge = new Panel(); badge.Children.Add(_number); badge.Children.Add(_done); _dot.Child = badge;
        _name.FontWeight = FontWeight.SemiBold; _name.VerticalAlignment = VerticalAlignment.Center;
        BindResource(_name, TextBlock.FontSizeProperty, "AcBodyFont"); BindResource(_name, TextBlock.ForegroundProperty, "UiText");
        BindResource(_detail, TextBlock.FontSizeProperty, "AcSubStepFont"); BindResource(_detail, TextBlock.ForegroundProperty, "UiTextSecondary");
        BindResource(_count, TextBlock.FontFamilyProperty, "UiNumericFont"); _count.FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") };
        _count.FontSize = 11; BindResource(_count, TextBlock.ForegroundProperty, "UiTextSecondary"); _count.VerticalAlignment = VerticalAlignment.Center;
        _number.FontSize = 10.5; _number.FontWeight = FontWeight.SemiBold;
        var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("18,26"), ColumnSpacing = 9, RowSpacing = 1 };
        grid.Children.Add(_dot); Grid.SetColumn(_name, 1); grid.Children.Add(_name); Grid.SetColumn(_count, 2); grid.Children.Add(_count);
        Grid.SetRow(_detail, 1); Grid.SetColumn(_detail, 1); Grid.SetColumnSpan(_detail, 2); grid.Children.Add(_detail); Child = grid;
    }
    public void Update(int index, string title, string detail, string count, string state)
    {
        _number.Text = (index + 1).ToString(); _name.Text = title; _detail.Text = detail; _count.Text = count;
        _done.IsVisible = state == "done"; _number.IsVisible = !_done.IsVisible;
        BindResource(_dot, BackgroundProperty, state == "done" ? "UiSuccessFill" : state == "running" ? "UiAccent" : state == "paused" ? "UiWarning" : "UiTrack");
        BindResource(_number, TextBlock.ForegroundProperty, state is "done" or "running" or "paused" ? "UiAccentText" : "UiTextSecondary");
        Bind(BackgroundProperty, Resource(state is "running" or "paused" ? "UiAccentSoft" : "UiSurface"));
        Bind(BorderBrushProperty, Resource(state == "running" ? "UiAccent" : state == "paused" ? "UiWarning" : "AcStageLineBrush"));
        Bind(BorderThicknessProperty, Resource(state is "running" or "paused" ? "AcFocusThickness" : ShowIdleBorder ? "AcStageLine" : "AcNoBorder"));
        ToolTip.SetTip(this, detail);
    }
}

/// <summary>固定图标位和两行文字，可显示真实的单项错误说明。</summary>
public sealed class SubStepRow : Grid
{
    private readonly StatusGlyph _icon = new();
    private readonly TextBlock _title = new(), _detail = new() { TextTrimming = TextTrimming.CharacterEllipsis }, _meta = new() { VerticalAlignment = VerticalAlignment.Top };
    public SubStepRow()
    {
        ColumnDefinitions = new("Auto,*,Auto"); ColumnSpacing = 9; Children.Add(_icon);
        BindResource(_title, TextBlock.FontSizeProperty, "AcBodyFont"); BindResource(_title, TextBlock.ForegroundProperty, "UiText");
        BindResource(_detail, TextBlock.FontSizeProperty, "AcSubStepFont"); BindResource(_detail, TextBlock.ForegroundProperty, "UiTextSecondary");
        BindResource(_meta, TextBlock.FontSizeProperty, "AcSubStepFont"); BindResource(_meta, TextBlock.ForegroundProperty, "UiTextSecondary"); BindResource(_meta, TextBlock.FontFamilyProperty, "UiNumericFont"); Grid.SetColumn(_meta, 2); Children.Add(_meta);
        var text = new StackPanel(); text.Children.Add(_title); text.Children.Add(_detail); Grid.SetColumn(text, 1); Children.Add(text);
    }
    public void Update(string title, string detail, string state, string meta = "")
    {
        _title.Text = title; _detail.Text = detail; _meta.Text = meta; BindResource(_meta, TextBlock.ForegroundProperty, state == "failed" ? "UiDanger" : "UiTextSecondary"); BindResource(_detail, TextBlock.ForegroundProperty, state == "failed" ? "UiDanger" : "UiTextSecondary"); _detail.IsVisible = detail.Length > 0; _icon.State = state;
        ToolTip.SetTip(this, title + (detail.Length > 0 ? " · " + detail : ""));
    }
}
