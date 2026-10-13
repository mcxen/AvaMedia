using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace AvaMedia.Desktop.Controls;

public sealed class SizeComparisonView : UserControl
{
    private readonly Grid _source = SizeBar("UiTextSecondary");
    private readonly Grid _target = SizeBar("UiAccent");
    private readonly TextBlock _sourceLabel = Ui.Text("", "caption");
    private readonly TextBlock _targetLabel = Ui.Text("", "caption");

    public SizeComparisonView()
    {
        var root = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        root.Bind(StackPanel.SpacingProperty, new DynamicResourceExtension("AcRowGap"));
        root.Children.Add(_sourceLabel); root.Children.Add(BarSurface(_source));
        root.Children.Add(_targetLabel); root.Children.Add(BarSurface(_target)); Content = root;
        IsVisible = false;
    }

    private static Grid SizeBar(string brush)
    {
        // These bars compare byte counts; they do not represent task progress.
        var bar = new Grid { ColumnDefinitions = new("*,0*"), ClipToBounds = true };
        bar.Bind(HeightProperty, new DynamicResourceExtension("AcColorBarHeight"));
        var fill = new Border(); fill.Bind(Border.BackgroundProperty, new DynamicResourceExtension(brush));
        bar.Children.Add(fill); return bar;
    }

    private static Border BarSurface(Grid bar)
    {
        var surface = new Border { Child = bar, ClipToBounds = true };
        surface.Bind(Border.BackgroundProperty, new DynamicResourceExtension("UiTrack"));
        surface.Bind(Border.CornerRadiusProperty, new DynamicResourceExtension("AcColorBarRadius"));
        surface.Bind(IsVisibleProperty, bar.GetObservable(IsVisibleProperty));
        return surface;
    }

    private static void SetRatio(Grid bar, double ratio)
    {
        bar.ColumnDefinitions[0].Width = new GridLength(ratio, GridUnitType.Star);
        bar.ColumnDefinitions[1].Width = new GridLength(1 - ratio, GridUnitType.Star);
    }

    public void SetSizes(long source, long? target)
    {
        IsVisible = source > 0;
        var maximum = Math.Max(1, Math.Max(source, target ?? source));
        SetRatio(_source, Math.Clamp(source / (double)maximum, 0, 1));
        SetRatio(_target, Math.Clamp((target ?? 0) / (double)maximum, 0, 1));
        _target.IsVisible = target is not null;
        Localization.SetText(_sourceLabel, $"原体积 {source / 1000000d:0.##} MB");
        _targetLabel.Text = target is { } bytes
            ? Localization.Format($"预计 {bytes / 1000000d:0.##} MB · 节省 {(source - bytes) * 100d / Math.Max(1, source):0.#}%")
            : Localization.Text("输出体积由内容决定");
        AutomationProperties.SetName(_source, _sourceLabel.Text);
        AutomationProperties.SetName(_target, _targetLabel.Text);
    }
}
