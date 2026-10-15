using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using static AvaMedia.Controls.ComponentResources;

namespace AvaMedia.Controls;

/// <summary>One row of a vertical stage list. State: pending, active, paused, done, failed, skipped, cancelled.</summary>
public sealed record StageListItem(string Title, string State, string Detail = "", string Time = "")
{
    /// <summary>0..1 determinate progress; null hides the bar unless <see cref="IsIndeterminate"/>.</summary>
    public double? Progress { get; init; }
    public bool IsIndeterminate { get; init; }
}

/// <summary>Stage state symbol (Lucide-style, 18 DIP): filled check, activity ring, numbered outline, alert, minus, ban, pause.</summary>
public sealed class StageGlyph : Control
{
    public static readonly StyledProperty<string> StateProperty = AvaloniaProperty.Register<StageGlyph, string>(nameof(State), "pending");
    public static readonly StyledProperty<int> NumberProperty = AvaloniaProperty.Register<StageGlyph, int>(nameof(Number), 1);
    public static readonly StyledProperty<double> AngleProperty = AvaloniaProperty.Register<StageGlyph, double>(nameof(Angle));
    private static readonly StyledProperty<IBrush?> SuccessProperty = AvaloniaProperty.Register<StageGlyph, IBrush?>("Success");
    private static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<StageGlyph, IBrush?>("Accent");
    private static readonly StyledProperty<IBrush?> AccentSoftProperty = AvaloniaProperty.Register<StageGlyph, IBrush?>("AccentSoft");
    private static readonly StyledProperty<IBrush?> DangerProperty = AvaloniaProperty.Register<StageGlyph, IBrush?>("Danger");
    private static readonly StyledProperty<IBrush?> WarningProperty = AvaloniaProperty.Register<StageGlyph, IBrush?>("Warning");
    private static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<StageGlyph, IBrush?>("Fill");
    private static readonly StyledProperty<IBrush?> TertiaryProperty = AvaloniaProperty.Register<StageGlyph, IBrush?>("Tertiary");
    private static readonly StyledProperty<IBrush?> SecondaryProperty = AvaloniaProperty.Register<StageGlyph, IBrush?>("Secondary");
    private static readonly StyledProperty<IBrush?> ContrastProperty = AvaloniaProperty.Register<StageGlyph, IBrush?>("Contrast");
    public string State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public int Number { get => GetValue(NumberProperty); set => SetValue(NumberProperty, value); }
    public double Angle { get => GetValue(AngleProperty); set => SetValue(AngleProperty, value); }

    static StageGlyph() => AffectsRender<StageGlyph>(StateProperty, NumberProperty, AngleProperty, SuccessProperty, AccentProperty, AccentSoftProperty,
        DangerProperty, WarningProperty, FillProperty, TertiaryProperty, SecondaryProperty, ContrastProperty);

    public StageGlyph()
    {
        BindResource(this, WidthProperty, "AcMsGlyphSize"); BindResource(this, HeightProperty, "AcMsGlyphSize");
        BindResource(this, SuccessProperty, "UiSuccessFill"); BindResource(this, AccentProperty, "UiAccent");
        BindResource(this, AccentSoftProperty, "AcRunningFill"); BindResource(this, DangerProperty, "UiDanger");
        BindResource(this, WarningProperty, "UiWarning"); BindResource(this, FillProperty, "UiFill");
        BindResource(this, TertiaryProperty, "AcTextTertiary"); BindResource(this, SecondaryProperty, "UiTextSecondary");
        BindResource(this, ContrastProperty, "UiAccentText");
        VerticalAlignment = VerticalAlignment.Top;
        Classes.Add("ac-stage-glyph");
        PropertyChanged += (_, change) => { if (change.Property == StateProperty) Classes.Set("ac-active", State == "active"); };
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var center = new Point(size / 2, size / 2); var radius = size / 2;
        var s = size / 18;
        switch (State)
        {
            case "done":
            {
                context.DrawEllipse(GetValue(SuccessProperty), null, center, radius, radius);
                var pen = Stroke(ContrastProperty, 2.2 * s);
                var check = Geometry(g => { g.BeginFigure(P(5.6, 9.3), false); g.LineTo(P(7.9, 11.6)); g.LineTo(P(12.4, 6.8)); g.EndFigure(false); });
                context.DrawGeometry(null, pen, check);
                break;
            }
            case "failed":
            {
                context.DrawEllipse(GetValue(DangerProperty), null, center, radius, radius);
                var pen = Stroke(ContrastProperty, 2.2 * s);
                context.DrawLine(pen, P(9, 5.4), P(9, 9.8));
                context.DrawEllipse(GetValue(ContrastProperty), null, P(9, 12.6), 1.2 * s, 1.2 * s);
                break;
            }
            case "paused":
            {
                context.DrawEllipse(GetValue(WarningProperty), null, center, radius, radius);
                var pen = Stroke(ContrastProperty, 2 * s);
                context.DrawLine(pen, P(7.3, 6.2), P(7.3, 11.8)); context.DrawLine(pen, P(10.7, 6.2), P(10.7, 11.8));
                break;
            }
            case "skipped":
            {
                context.DrawEllipse(GetValue(FillProperty), null, center, radius, radius);
                context.DrawLine(Stroke(SecondaryProperty, 2 * s), P(6, 9), P(12, 9));
                break;
            }
            case "cancelled":
            {
                context.DrawEllipse(GetValue(FillProperty), null, center, radius, radius);
                var pen = Stroke(SecondaryProperty, 1.6 * s);
                context.DrawEllipse(null, pen, center, 4.2 * s, 4.2 * s);
                context.DrawLine(pen, P(6.1, 6.1), P(11.9, 11.9));
                break;
            }
            case "active":
            {
                var ring = radius - s;
                context.DrawEllipse(null, new Pen(GetValue(AccentSoftProperty), 2 * s), center, ring, ring);
                var start = (Angle - 135) * Math.PI / 180;
                Point A(double a) => new(center.X + ring * Math.Cos(a), center.Y + ring * Math.Sin(a));
                var arc = Geometry(g => { g.BeginFigure(A(start), false); g.ArcTo(A(start + Math.PI), new(ring, ring), 0, false, SweepDirection.Clockwise); g.EndFigure(false); });
                context.DrawGeometry(null, new Pen(GetValue(AccentProperty), 2 * s, lineCap: PenLineCap.Round), arc);
                context.DrawEllipse(GetValue(AccentProperty), null, center, 3 * s, 3 * s);
                break;
            }
            default:
            {
                var ring = radius - .75 * s;
                context.DrawEllipse(null, new Pen(GetValue(TertiaryProperty), 1.5 * s), center, ring, ring);
                var text = new FormattedText(Number.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, FontWeight.SemiBold), 11 * s, GetValue(SecondaryProperty));
                context.DrawText(text, new(center.X - text.Width / 2, center.Y - text.Height / 2));
                break;
            }
        }
        Point P(double x, double y) => new(x * s, y * s);
    }

    private Pen Stroke(StyledProperty<IBrush?> brush, double width) => new(GetValue(brush), width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private static StreamGeometry Geometry(Action<StreamGeometryContext> draw)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open()) draw(g);
        return geometry;
    }
}

/// <summary>Vertical stage list (multi-stage shell B): header with count, connected rows and a pinned footer slot.</summary>
public sealed class StageList : Border
{
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold }, _count = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel _rows = new();
    private readonly ContentControl _footer = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Border _footerFace = new();
    private readonly List<Row> _cache = [];
    protected override Type StyleKeyOverride => typeof(Border);

    public StageList()
    {
        Classes.Add("ac-stage-list");
        BindResource(this, BackgroundProperty, "AcCardFace"); BindResource(this, CornerRadiusProperty, "AcMsCardRadius");
        BindResource(this, BoxShadowProperty, "AcCardShadow"); BindResource(this, PaddingProperty, "AcMsStageListPadding");
        BindResource(_title, TextBlock.FontSizeProperty, "AcCaptionFont"); BindResource(_title, TextBlock.ForegroundProperty, "UiText");
        BindResource(_count, TextBlock.FontSizeProperty, "AcSubStepFont"); BindResource(_count, TextBlock.ForegroundProperty, "UiTextSecondary");
        _count.FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") };
        var header = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(8, 4, 8, 8) };
        header.Children.Add(_title); Grid.SetColumn(_count, 1); header.Children.Add(_count);
        _title.Text = "处理阶段";
        BindResource(_footerFace, BorderBrushProperty, "AcHairline");
        _footerFace.BorderThickness = new(0, .5, 0, 0); _footerFace.Padding = new(8, 10, 8, 4);
        _footerFace.Child = _footer;
        var layout = new Grid { RowDefinitions = new("Auto,*,Auto") };
        layout.Children.Add(header);
        _rows.Margin = new(0, 0, 0, 12);
        var scroll = new ScrollViewer
        {
            Content = _rows, AllowAutoHide = false, Margin = new(0, 0, 0, 8),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        scroll.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<Avalonia.Controls.Primitives.ScrollBar>("PART_VerticalScrollBar") is { } bar)
                ScrollVisibility.SetPinned(bar, true);
        };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        Grid.SetRow(_footerFace, 2); layout.Children.Add(_footerFace);
        Child = layout;
        _footerFace.IsVisible = false;
    }

    /// <summary>Header text; hosts localize before assigning.</summary>
    public string Title { get => _title.Text ?? ""; set => _title.Text = value; }
    public Control? Footer
    {
        get => _footer.Content as Control;
        set { _footer.Content = value; _footerFace.IsVisible = value is not null; }
    }

    public void Update(IReadOnlyList<StageListItem> items, string count)
    {
        _count.Text = count;
        while (_cache.Count < items.Count) { var row = new Row(); _cache.Add(row); _rows.Children.Add(row); }
        while (_cache.Count > items.Count) { _rows.Children.Remove(_cache[^1]); _cache.RemoveAt(_cache.Count - 1); }
        for (var i = 0; i < items.Count; i++) _cache[i].Update(items[i], i + 1, i == items.Count - 1);
    }

    private sealed class Row : Panel
    {
        private readonly Border _face = new();
        private readonly Rectangle _line = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch, IsHitTestVisible = false };
        private readonly StageGlyph _glyph = new();
        private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = 18, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _time = new() { LineHeight = 18, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = 15, Margin = new(0, 1, 0, 0) };
        private readonly ProgressBar _bar = new() { Classes = { "ac" }, Minimum = 0, Maximum = 1, Margin = new(0, 7, 0, 0) };
        private string _state = "";

        public Row()
        {
            BindResource(_face, Border.CornerRadiusProperty, "AcMsStageRowRadius"); BindResource(_face, Border.PaddingProperty, "AcMsStageRowPadding");
            BindResource(_title, TextBlock.FontSizeProperty, "AcBodyFont");
            BindResource(_time, TextBlock.FontSizeProperty, "AcSubStepFont"); BindResource(_time, TextBlock.ForegroundProperty, "UiTextSecondary");
            _time.FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") };
            BindResource(_detail, TextBlock.FontSizeProperty, "AcSubStepFont");
            _detail.FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") };
            BindResource(_bar, HeightProperty, "AcProgressHeight");
            BindResource(_line, WidthProperty, "AcMsStageLineWidth");
            // Connector from below this glyph into the next row (glyph centre = row padding 8 + half glyph 9).
            _line.Margin = new(16.25, 30, 0, -4);
            var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("Auto,Auto,Auto"), ColumnSpacing = 9 };
            grid.Children.Add(_glyph);
            Grid.SetColumn(_title, 1); grid.Children.Add(_title);
            Grid.SetColumn(_time, 2); grid.Children.Add(_time);
            Grid.SetRow(_detail, 1); Grid.SetColumn(_detail, 1); Grid.SetColumnSpan(_detail, 2); grid.Children.Add(_detail);
            Grid.SetRow(_bar, 2); Grid.SetColumn(_bar, 1); Grid.SetColumnSpan(_bar, 2); grid.Children.Add(_bar);
            _face.Child = grid;
            Children.Add(_line); Children.Add(_face);
        }

        public void Update(StageListItem item, int number, bool last)
        {
            _glyph.State = item.State; _glyph.Number = number;
            _title.Text = item.Title; _time.Text = item.Time; _time.IsVisible = item.Time.Length > 0;
            _detail.Text = item.Detail; _detail.IsVisible = item.State != "pending" && item.Detail.Length > 0; ToolTip.SetTip(_detail, item.Detail.Length > 0 ? item.Detail : null);
            ToolTip.SetTip(this, item.Detail.Length > 0 ? item.Title + "\n" + item.Detail : item.Title);
            _bar.IsVisible = item.Progress is not null || item.IsIndeterminate;
            _bar.IsIndeterminate = item.IsIndeterminate && item.Progress is null;
            _bar.Value = Math.Clamp(item.Progress ?? 0, 0, 1);
            _bar.Classes.Set("paused", item.State == "paused");
            _line.IsVisible = !last;
            if (_state == item.State) return;
            _state = item.State;
            BindResource(_face, Border.BackgroundProperty, item.State switch
            { "active" => "UiAccentSoft", "paused" => "AcWarningFill", "failed" => "AcDangerSoft", _ => "UiTransparent" });
            BindResource(_line, Shape.FillProperty, item.State == "done" ? "UiSuccessFill" : "UiTrack");
            BindResource(_title, TextBlock.ForegroundProperty, item.State is "pending" or "skipped" or "cancelled" ? "UiTextSecondary" : "UiText");
            BindResource(_detail, TextBlock.ForegroundProperty, item.State switch
            { "pending" => "AcTextTertiary", "failed" => "UiDanger", _ => "UiTextSecondary" });
        }
    }
}
