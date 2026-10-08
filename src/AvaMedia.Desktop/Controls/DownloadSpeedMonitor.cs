using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

public sealed class DownloadSpeedMonitor : Border
{
    private readonly TextBlock _current = Value(15);
    private readonly TextBlock _peak = Value(12);
    private readonly TextBlock _average = Value(12);
    private readonly DownloadSpeedGraph _graph = new() { Height = 44 };

    public DownloadSpeedMonitor()
    {
        Width = 320; Padding = new Thickness(10, 6); BorderThickness = new Thickness(1);
        Bind(BackgroundProperty, new DynamicResourceExtension("UiSurface"));
        Bind(BorderBrushProperty, new DynamicResourceExtension("UiBorder"));
        ToolTip.SetTip(this, "曲线显示最近 60 秒的下载总速度；峰值与平均值按本轮下载统计，平均值包含停顿，不包含媒体整理。");
        AutomationProperties.SetName(this, "下载网速监控");
        var layout = new Grid { RowDefinitions = new("Auto,Auto"), RowSpacing = 5 };
        var header = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 16 };
        AddMetric(header, 0, "下载", _current);
        AddMetric(header, 1, "峰值", _peak);
        AddMetric(header, 2, "平均", _average);
        layout.Children.Add(header); Grid.SetRow(_graph, 1); layout.Children.Add(_graph); Child = layout;
        Update(default, new double[60]);
    }

    private static TextBlock Value(double size)
    {
        var value = new TextBlock { FontSize = size, FontWeight = FontWeight.SemiBold };
        value.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiText"));
        Localization.SetIsUserText(value, true);
        return value;
    }

    private static void AddMetric(Grid header, int column, string label, TextBlock value)
    {
        var caption = new TextBlock { Text = label, FontSize = 10 };
        caption.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiTextSecondary"));
        var metric = new StackPanel { Spacing = 1 };
        metric.Children.Add(caption); metric.Children.Add(value);
        Grid.SetColumn(metric, column); header.Children.Add(metric);
    }

    public void Update(DownloadSpeedTotals totals, IReadOnlyList<double> history)
    {
        _current.Text = FormatSpeed(totals.Current);
        _peak.Text = FormatSpeed(totals.Peak);
        _average.Text = FormatSpeed(totals.Average);
        _graph.Update(history, totals.Average);
    }

    private static string FormatSpeed(double speed)
    {
        string[] units = ["B/s", "KiB/s", "MiB/s", "GiB/s"];
        var unit = 0;
        while (speed >= 1024 && unit < units.Length - 1) { speed /= 1024; unit++; }
        return speed.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}

public sealed class DownloadSpeedGraph : Control
{
    public static readonly StyledProperty<IBrush?> LineBrushProperty = AvaloniaProperty.Register<DownloadSpeedGraph, IBrush?>(nameof(LineBrush));
    public static readonly StyledProperty<IBrush?> AverageBrushProperty = AvaloniaProperty.Register<DownloadSpeedGraph, IBrush?>(nameof(AverageBrush));
    public static readonly StyledProperty<IBrush?> GridBrushProperty = AvaloniaProperty.Register<DownloadSpeedGraph, IBrush?>(nameof(GridBrush));
    public IBrush? LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public IBrush? AverageBrush { get => GetValue(AverageBrushProperty); set => SetValue(AverageBrushProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    private readonly double[] _history = new double[60];
    private double _average;

    static DownloadSpeedGraph() => AffectsRender<DownloadSpeedGraph>(LineBrushProperty, AverageBrushProperty, GridBrushProperty);
    public DownloadSpeedGraph()
    {
        Bind(LineBrushProperty, new DynamicResourceExtension("UiSuccess"));
        Bind(AverageBrushProperty, new DynamicResourceExtension("UiAccent"));
        Bind(GridBrushProperty, new DynamicResourceExtension("UiDivider"));
    }

    public void Update(IReadOnlyList<double> history, double average)
    {
        for (var i = 0; i < _history.Length; i++) _history[i] = i < history.Count ? history[i] : 0;
        _average = average; InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 2 || Bounds.Height <= 2) return;
        var plot = new Rect(1, 1, Bounds.Width - 2, Bounds.Height - 2);
        var ceiling = Math.Max(1024, Math.Max(_history.Max(), _average) * 1.15);
        double Y(double value) => plot.Bottom - Math.Clamp(value / ceiling, 0, 1) * plot.Height;
        double X(int index) => plot.X + index * plot.Width / (_history.Length - 1);
        using var clip = context.PushClip(plot);
        var grid = new Pen(GridBrush, 1);
        for (var i = 1; i <= 3; i++)
        {
            var y = plot.Y + plot.Height * i / 3;
            context.DrawLine(grid, new(plot.X, y), new(plot.Right, y));
        }
        for (var i = 1; i < 6; i++)
        {
            var x = plot.X + plot.Width * i / 6;
            context.DrawLine(grid, new(x, plot.Y), new(x, plot.Bottom));
        }
        var mountain = new StreamGeometry();
        using (var path = mountain.Open())
        {
            path.BeginFigure(new(plot.X, plot.Bottom), true);
            for (var i = 0; i < _history.Length; i++) path.LineTo(new(X(i), Y(_history[i])));
            path.LineTo(new(plot.Right, plot.Bottom)); path.EndFigure(true);
        }
        using (context.PushOpacity(.18)) context.DrawGeometry(LineBrush, null, mountain);
        if (_average > 0)
            context.DrawLine(new Pen(AverageBrush, 1, DashStyle.Dash), new(plot.X, Y(_average)), new(plot.Right, Y(_average)));
        var line = new Pen(LineBrush, 1.5);
        for (var i = 1; i < _history.Length; i++)
            context.DrawLine(line, new(X(i - 1), Y(_history[i - 1])), new(X(i), Y(_history[i])));
    }
}
