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
    public static readonly StyledProperty<IBrush?> BrushProperty = AvaloniaProperty.Register<StatusGlyph, IBrush?>(nameof(Brush));
    public static readonly StyledProperty<IBrush?> ContrastProperty = AvaloniaProperty.Register<StatusGlyph, IBrush?>(nameof(Contrast));
    public string State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public IBrush? Brush => GetValue(BrushProperty);
    public IBrush? Contrast => GetValue(ContrastProperty);
    static StatusGlyph() => AffectsRender<StatusGlyph>(StateProperty, BrushProperty, ContrastProperty);
    public StatusGlyph()
    {
        Bind(WidthProperty, Resource("AcStepIcon")); Bind(HeightProperty, Resource("AcStepIcon"));
        Bind(ContrastProperty, Resource("UiAccentText")); VerticalAlignment = VerticalAlignment.Center;
        PropertyChanged += (_, change) => { if (change.Property == StateProperty) Refresh(); };
        Refresh();
    }
    private void Refresh() => Bind(BrushProperty, Resource(State switch { "done" => "UiSuccessFill", "running" => "UiAccent", "failed" => "UiDanger", "paused" => "UiWarning", _ => "UiTrack" }));
    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height); var center = new Point(size / 2, size / 2); var radius = Math.Max(0, size / 2 - 1);
        if (State == "done")
        {
            context.DrawEllipse(Brush, null, center, radius, radius);
            var pen = new Pen(Contrast, 1.4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawLine(pen, new(size * .26, size * .5), new(size * .43, size * .67));
            context.DrawLine(pen, new(size * .43, size * .67), new(size * .75, size * .32));
        }
        else if (State == "running")
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open()) { g.BeginFigure(new(center.X, 1), false); g.ArcTo(new(1, center.Y), new(radius, radius), 0, true, SweepDirection.Clockwise); g.EndFigure(false); }
            context.DrawGeometry(null, new Pen(Brush, 1.5, lineCap: PenLineCap.Round), geometry);
        }
        else
        {
            var pen = new Pen(Brush, 1.4); context.DrawEllipse(null, pen, center, radius, radius);
            if (State == "failed") { context.DrawLine(pen, new(center.X, size * .25), new(center.X, size * .55)); context.DrawEllipse(Brush, null, new(center.X, size * .73), .7, .7); }
            if (State == "paused") { context.DrawLine(pen, new(size * .4, size * .3), new(size * .4, size * .7)); context.DrawLine(pen, new(size * .6, size * .3), new(size * .6, size * .7)); }
        }
    }
}

public sealed class StageIndicator : Border
{
    private readonly TextBlock _name = new(), _detail = new() { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2 },
        _count = new(), _number = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _dot = new();
    private readonly StatusGlyph _done = new() { State = "done" };
    protected override Type StyleKeyOverride => typeof(Border);
    public StageIndicator()
    {
        Classes.Add("ac-stage");
        BindResource(_dot, WidthProperty, "AcStageDot"); BindResource(_dot, HeightProperty, "AcStageDot"); BindResource(_dot, CornerRadiusProperty, "AcStatusRadius");
        var badge = new Panel(); badge.Children.Add(_number); badge.Children.Add(_done); _dot.Child = badge;
        _name.FontWeight = FontWeight.SemiBold; _name.VerticalAlignment = VerticalAlignment.Center;
        BindResource(_name, TextBlock.FontSizeProperty, "AcButtonFont"); BindResource(_name, TextBlock.ForegroundProperty, "UiText");
        BindResource(_detail, TextBlock.FontSizeProperty, "AcCaptionFont"); BindResource(_detail, TextBlock.ForegroundProperty, "UiTextSecondary");
        BindResource(_count, TextBlock.FontFamilyProperty, "UiNumericFont"); _count.FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") };
        _count.FontSize = 10.5; BindResource(_count, TextBlock.ForegroundProperty, "UiTextSecondary"); _count.VerticalAlignment = VerticalAlignment.Center;
        _number.FontSize = 12; _number.FontWeight = FontWeight.Bold;
        var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("18,*"), ColumnSpacing = 7, RowSpacing = 4 };
        grid.Children.Add(_dot); Grid.SetColumn(_name, 1); grid.Children.Add(_name); Grid.SetColumn(_count, 2); grid.Children.Add(_count);
        Grid.SetRow(_detail, 1); Grid.SetColumnSpan(_detail, 3); grid.Children.Add(_detail); Child = grid;
    }
    public void Update(int index, string title, string detail, string count, string state)
    {
        _number.Text = (index + 1).ToString(); _name.Text = title; _detail.Text = detail; _count.Text = count;
        _done.IsVisible = state == "done"; _number.IsVisible = !_done.IsVisible;
        BindResource(_dot, BackgroundProperty, state == "done" ? "UiSuccessFill" : state == "running" ? "UiAccent" : state == "paused" ? "UiWarning" : "UiTrack");
        BindResource(_number, TextBlock.ForegroundProperty, state is "done" or "running" or "paused" ? "UiAccentText" : "UiTextSecondary");
        Bind(BackgroundProperty, Resource(state is "running" or "paused" ? "AcRunningFill" : "UiSurface"));
        Bind(BorderBrushProperty, Resource(state == "running" ? "UiAccent" : state == "paused" ? "UiWarning" : "UiTransparent"));
        Bind(BorderThicknessProperty, Resource(state is "running" or "paused" ? "AcFocusThickness" : "AcNoBorder"));
        ToolTip.SetTip(this, detail);
    }
}

/// <summary>固定图标位和两行文字，可显示真实的单项错误说明。</summary>
public sealed class SubStepRow : Grid
{
    private readonly StatusGlyph _icon = new();
    private readonly TextBlock _title = new(), _detail = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    public SubStepRow()
    {
        ColumnDefinitions = new("Auto,*"); ColumnSpacing = 6; Children.Add(_icon);
        BindResource(_title, TextBlock.FontSizeProperty, "AcButtonFont"); BindResource(_title, TextBlock.ForegroundProperty, "UiText");
        BindResource(_detail, TextBlock.FontSizeProperty, "AcCaptionFont"); BindResource(_detail, TextBlock.ForegroundProperty, "UiTextSecondary");
        var text = new StackPanel(); text.Children.Add(_title); text.Children.Add(_detail); Grid.SetColumn(text, 1); Children.Add(text);
    }
    public void Update(string title, string detail, string state)
    {
        _title.Text = title; _detail.Text = detail; _detail.IsVisible = detail.Length > 0; _icon.State = state;
        ToolTip.SetTip(this, title + (detail.Length > 0 ? " · " + detail : ""));
    }
}
