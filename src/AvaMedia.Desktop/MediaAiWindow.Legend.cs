using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed partial class MediaAiWindow
{
    private sealed record LegendValue(TextBlock Text, string Line, double Score, IReadOnlyList<MediaTagPoint> Points);
    private readonly Dictionary<string, LegendValue> _legendValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _legendTime = Ui.Text("", "caption");

    private void RenderTraceLegend(IReadOnlyList<ResultTag> tags, IReadOnlyList<TagChartSeries> series)
    {
        _traceLegend.Children.Clear(); _traceLegend.RowDefinitions.Clear(); _traceLegend.ColumnDefinitions.Clear();
        _legendValues.Clear(); _legendTime.Text = ""; _traceLegend.IsVisible = tags.Count > 0;
        if (tags.Count == 0) return;
        var models = tags.Select(tag => tag.Model).Distinct().ToArray();
        _traceLegend.ColumnDefinitions.Add(new(GridLength.Star));
        foreach (var model in models) _traceLegend.ColumnDefinitions.Add(new(GridLength.Auto));
        _traceLegend.RowDefinitions.Add(new(GridLength.Auto));
        _traceLegend.Children.Add(_legendTime);
        for (var index = 0; index < models.Length; index++)
        {
            var model = Ui.Text(ModelLabel(models[index]), "caption");
            model.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(ModelColor(models[index])));
            Grid.SetColumn(model, index + 1); _traceLegend.Children.Add(model);
        }
        var row = 1;
        foreach (var group in tags.GroupBy(tag => tag.Label.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            _traceLegend.RowDefinitions.Add(new(GridLength.Auto));
            var label = Ui.Text(group.Key); label.TextWrapping = TextWrapping.Wrap;
            label.VerticalAlignment = VerticalAlignment.Center; Localization.SetIsUserText(label, true);
            Grid.SetRow(label, row); _traceLegend.Children.Add(label);
            foreach (var tag in group)
            {
                var trace = series.First(item => item.Key == TagKey(tag));
                var line = new[] { "━", "┄", "┈" }[trace.Variant];
                var value = Ui.Text(line + $" {tag.Score:0.000}"); Localization.SetIsUserText(value, true);
                var button = new Button { Content = value, Padding = new(5, 2), HorizontalAlignment = HorizontalAlignment.Stretch };
                button.Bind(Button.BorderBrushProperty, new DynamicResourceExtension(ModelColor(tag.Model)));
                value.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(ModelColor(tag.Model)));
                AutomationProperties.SetName(button, ModelLabel(tag.Model) + " · " + tag.Label);
                button.Click += (_, _) => SelectTrace(TagKey(tag));
                Grid.SetRow(button, row); Grid.SetColumn(button, Array.IndexOf(models, tag.Model) + 1); _traceLegend.Children.Add(button);
                _legendValues[TagKey(tag)] = new(value, line, tag.Score, trace.Points);
            }
            row++;
        }
    }
    private void RefreshLegendSample(double? seconds)
    {
        _legendTime.Text = seconds is { } time ? MediaTime.Format(time) : "";
        Localization.SetIsUserText(_legendTime, true);
        foreach (var value in _legendValues.Values)
        {
            var score = seconds is { } sample ? value.Points.FirstOrDefault(point => point.Seconds == sample)?.Score : value.Score;
            value.Text.Text = value.Line + " " + (score?.ToString("0.000") ?? "—");
        }
    }
}
