using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaMedia.Core;

namespace AvaMedia.Desktop.MultiStage;

/// <summary>Structured, read-only task log: time · stage · message. Marker rows (cancel / interruption) use the selection fill.</summary>
internal sealed class MultiStageLogView : AvaMedia.Controls.Card
{
    private readonly ListBox _list = new() { Classes = { "ms-log" }, Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0, 6) };
    private readonly AvaMedia.Controls.EmptyState _empty = new() { Text = Localization.Text("暂无处理日志"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private IReadOnlyList<TaskLogEntry> _entries = [];

    public MultiStageLogView()
    {
        Padding = new(0); ClipToBounds = true;
        _list.ItemTemplate = new FuncDataTemplate<TaskLogEntry>((entry, _) => entry is null ? null : Row(entry));
        Avalonia.Automation.AutomationProperties.SetName(_list, Localization.Text("处理日志"));
        var root = new Panel(); root.Children.Add(_list); root.Children.Add(_empty);
        Child = root; _empty.IsVisible = true;
    }

    public int Count => _entries.Count;

    public void Update(IReadOnlyList<TaskLogEntry> entries)
    {
        if (entries.Count == _entries.Count && entries.SequenceEqual(_entries)) return;
        _entries = entries; _list.ItemsSource = entries.ToArray(); _empty.IsVisible = entries.Count == 0;
        var marker = entries.ToList().FindLastIndex(entry => entry.Level == TaskLogLevel.Marker);
        if (marker >= 0) _list.ScrollIntoView(marker);
    }

    private static Control Row(TaskLogEntry entry)
    {
        var grid = new Grid { ColumnDefinitions = new("64,92,*"), ColumnSpacing = 10, Margin = new(14, 5) };
        // Legacy log lines carry no timestamp (DateTime.MinValue); leave the time column empty for them.
        var time = Text(entry.TimeUtc == DateTime.MinValue ? "" : DateTime.SpecifyKind(entry.TimeUtc, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss"), "AcTextTertiary");
        var stage = Text(Localization.Text(entry.Title), entry.Level == TaskLogLevel.Error ? "UiDanger" : entry.Level == TaskLogLevel.Warning ? "UiWarning" : "UiTextSecondary");
        stage.TextTrimming = TextTrimming.CharacterEllipsis; ToolTip.SetTip(stage, stage.Text);
        var message = Text(entry.Message, entry.Level == TaskLogLevel.Error ? "UiDanger" : "UiText");
        message.TextWrapping = TextWrapping.Wrap; Localization.SetIsUserText(message, true);
        grid.Children.Add(time); Grid.SetColumn(stage, 1); grid.Children.Add(stage); Grid.SetColumn(message, 2); grid.Children.Add(message);
        var face = new Border { Child = grid };
        if (entry.Level == TaskLogLevel.Marker) face.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AcSelection"));
        return face;
    }

    private static TextBlock Text(string text, string brush)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Top, LineHeight = 17 };
        block.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension("AcCaptionFont"));
        block.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(brush));
        block.FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") };
        return block;
    }
}
