using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaMedia.Controls;
using AvaMedia.Core;
using AvaMedia.Desktop.MultiStage;

namespace AvaMedia.Desktop.PersonClipAdjust;

internal sealed partial class PersonClipAdjustWindow
{
    private sealed record Row(PersonClipEditSegment Segment, int Number, double Threshold, bool HasScores);

    private readonly ListBox _list = new() { Classes = { "ms-segments" } };
    private readonly ObservableCollection<Row> _rows = [];
    private readonly SegmentedControl _filter = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _listTitle = new() { Classes = { "ms-heading" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _keptTotal = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _keptOf = new() { Classes = { "ms-caption" }, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(6, 0, 0, 2) };
    private readonly ProgressBar _keptBar = new() { Classes = { "ac" }, Minimum = 0, Maximum = 1, Margin = new(0, 8, 0, 8) };
    private readonly TextBlock _keptLeft = new() { Classes = { "ms-small" } };
    private readonly TextBlock _keptRight = new() { Classes = { "ms-small" }, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Dictionary<long, Bitmap> _rowThumbs = [];
    private readonly SemaphoreSlim _thumbGate = new(2, 2);
    private bool _selectingRow;

    partial void BuildListContent() => _listSlot.Children.Add(BuildList());

    private Control BuildList()
    {
        var card = new Border { Classes = { "ms-card" }, ClipToBounds = true };
        var grid = new Grid { RowDefinitions = new("Auto,*,Auto") };
        var head = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new(12, 10, 12, 8) };
        _listTitle.Text = Localization.Text("片段");
        head.Children.Add(_listTitle);
        _filter.HorizontalAlignment = HorizontalAlignment.Right; _filter.Height = 26;
        _filter.Resources["AcSegmentPadding"] = new Thickness(1);
        _filter.Resources["AcSegmentItemPadding"] = new Thickness(5, 0);
        _filter.ItemTemplate = new FuncDataTemplate<string>((value, _) => MsUi.Caption(value ?? ""));
        Avalonia.Automation.AutomationProperties.SetName(_filter, Localization.Text("片段筛选"));
        _filter.SelectionChanged += (_, _) => RefreshList();
        Grid.SetColumn(_filter, 1); head.Children.Add(_filter);
        grid.Children.Add(head);

        MsUi.ListViewport(_list);
        _list.ItemsSource = _rows;
        _list.ItemTemplate = new FuncDataTemplate<Row>((row, _) => row is null ? new Panel() : BuildRow(row));
        Avalonia.Automation.AutomationProperties.SetName(_list, Localization.Text("片段列表"));
        _list.SelectionChanged += (_, _) => { if (!_selectingRow && _list.SelectedItem is Row row) Select(row.Segment.Id); };
        Grid.SetRow(_list, 1); grid.Children.Add(_list);

        var foot = new StackPanel { Margin = new(12, 9, 12, 11) };
        var line = new Border { Classes = { "ms-row-line" }, BorderThickness = new(0, .5, 0, 0), Margin = new(-12, -9, -12, 9) };
        foot.Children.Add(line);
        _keptTotal.FontSize = 17;
        _keptTotal.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiText"));
        _keptTotal.FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") };
        foot.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _keptTotal, _keptOf } });
        _keptBar.Bind(HeightProperty, new DynamicResourceExtension("AcProgressHeight"));
        foot.Children.Add(_keptBar);
        var labels = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        labels.Children.Add(_keptLeft); Grid.SetColumn(_keptRight, 1); labels.Children.Add(_keptRight);
        foot.Children.Add(labels);
        Grid.SetRow(foot, 2); grid.Children.Add(foot);
        card.Child = grid;
        return card;
    }

    private IEnumerable<PersonClipEditSegment> Filtered() => _filter.SelectedIndex switch
    {
        1 => _session.Segments.Where(s => s.Keep),
        2 => _session.Segments.Where(s => !s.Keep),
        _ => _session.Segments
    };

    partial void RefreshList()
    {
        var all = _session.Segments; var kept = all.Count(s => s.Keep); var removed = all.Count - kept;
        var labels = new[] { Localization.Format($"全部 {all.Count}"), Localization.Format($"保留 {kept}"), Localization.Format($"已移除 {removed}") };
        var filter = Math.Max(0, _filter.SelectedIndex);
        if (_filter.ItemsSource is not string[] current || !current.SequenceEqual(labels))
        { _filter.ItemsSource = labels; _filter.SelectedIndex = filter; }

        var numbers = all.Select((s, i) => (s.Id, i + 1)).ToDictionary(p => p.Id, p => p.Item2);
        var rows = Filtered().Select(s => new Row(s, numbers[s.Id], _session.Threshold, _session.HasScores)).ToList();
        _selectingRow = true;
        for (var i = 0; i < rows.Count; i++)
        {
            if (i < _rows.Count) { if (!Equals(_rows[i], rows[i])) _rows[i] = rows[i]; }
            else _rows.Add(rows[i]);
        }
        while (_rows.Count > rows.Count) _rows.RemoveAt(_rows.Count - 1);
        _selectingRow = false;
        RefreshListSelection();

        var duration = Math.Max(1e-9, _session.Duration);
        _keptTotal.Text = AdjustTime.Clock(_session.KeptSeconds, 1);
        _keptOf.Text = "/ " + AdjustTime.Clock(_session.Duration, 1) + " " + Localization.Text("保留") + " · "
            + (_session.KeptSeconds / duration).ToString("0%", CultureInfo.InvariantCulture);
        _keptBar.Value = _session.KeptSeconds / duration;
        _keptLeft.Text = Localization.Format($"保留 {kept} 段") + " · " + Localization.Text("移除") + " " + AdjustTime.Clock(_session.Duration - _session.KeptSeconds, 1);
        _keptRight.Text = Localization.Text("检测结果") + " " + AdjustTime.Clock(_session.DetectedSeconds, 1);
    }

    partial void RefreshListSelection()
    {
        _selectingRow = true;
        var row = _rows.FirstOrDefault(r => r.Segment.Id == _selected);
        _list.SelectedItem = row;
        if (row is not null) _list.ScrollIntoView(row);
        _selectingRow = false;
    }

    private Control BuildRow(Row row)
    {
        var segment = row.Segment;
        var grid = new Grid { ColumnDefinitions = new("26,18,50,*,Auto"), ColumnSpacing = 8, Margin = new(12, 6, 12, 6) };
        MsUi.Brush(grid, Grid.BackgroundProperty, "UiTransparent");
        var toggle = new ToggleSwitch { Classes = { "ac", "small" }, IsChecked = segment.Keep, OnContent = null, OffContent = null, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(toggle, Localization.Format($"保留片段 {row.Number}"));
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (toggle.IsChecked == segment.Keep) return;
            Do(() => _session.SetKeep(segment.Id, toggle.IsChecked == true));
            if (_session.Segments.FirstOrDefault(s => s.Id == segment.Id) is { } now && now.Keep != toggle.IsChecked) toggle.IsChecked = now.Keep;
        };
        grid.Children.Add(toggle);
        var number = new TextBlock { Classes = { "ms-small" }, Text = row.Number.ToString(CultureInfo.InvariantCulture), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        MsUi.Brush(number, TextBlock.ForegroundProperty, "AcTextTertiary");
        Grid.SetColumn(number, 1); grid.Children.Add(number);
        var thumb = new Border { Width = 50, Height = 28.125, CornerRadius = new(4), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
        thumb.Bind(Border.BackgroundProperty, new DynamicResourceExtension("UiFill"));
        var image = new Image { Stretch = Stretch.UniformToFill };
        thumb.Child = image;
        image.AttachedToVisualTree += (_, _) => RequestRowThumb((segment.Start + segment.End) / 2, bitmap => image.Source = bitmap);
        Grid.SetColumn(thumb, 2); grid.Children.Add(thumb);

        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var range = new TextBlock { FontSize = 12, FontWeight = FontWeight.Medium, Text = AdjustTime.Clock(segment.Start, 1) + " – " + AdjustTime.Clock(segment.End, 1), TextTrimming = TextTrimming.CharacterEllipsis };
        range.FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") };
        if (!segment.Keep) { range.TextDecorations = TextDecorations.Strikethrough; MsUi.Brush(range, TextBlock.ForegroundProperty, "AcTextTertiary"); }
        text.Children.Add(range);
        var sub = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (!segment.Keep) sub.Children.Add(Tag("已移除", "UiFill", "UiTextSecondary"));
        if (segment.Origin == PersonClipSegmentOrigin.Manual) sub.Children.Add(Tag("手动", "AcWarningFill", "AcWarningText"));
        else if (segment.Adjusted) sub.Children.Add(Tag("已调整", "AcWarningFill", "AcWarningText"));
        if (segment.LowConfidence) sub.Children.Add(Tag("低置信度", "UiFill", "UiTextSecondary"));
        if (segment.Confidence is { } confidence)
        {
            var bar = new ProgressBar { Classes = { "ac" }, Minimum = 0, Maximum = 1, Value = confidence, Width = 30, VerticalAlignment = VerticalAlignment.Center };
            bar.Bind(HeightProperty, new DynamicResourceExtension("AcProgressHeight"));
            sub.Children.Add(bar);
            sub.Children.Add(new TextBlock { Classes = { "ms-small" }, Text = confidence.ToString("0.00", CultureInfo.InvariantCulture), VerticalAlignment = VerticalAlignment.Center });
        }
        if (sub.Children.Count > 0) text.Children.Add(sub);
        Grid.SetColumn(text, 3); grid.Children.Add(text);
        var duration = new TextBlock { Classes = { "ms-small" }, Text = Localization.Format($"{MsUi.Number(Math.Round(segment.Duration, 1))} 秒"), VerticalAlignment = VerticalAlignment.Center };
        if (!segment.Keep) { duration.TextDecorations = TextDecorations.Strikethrough; MsUi.Brush(duration, TextBlock.ForegroundProperty, "AcTextTertiary"); }
        Grid.SetColumn(duration, 4); grid.Children.Add(duration);
        if (!segment.Keep) { thumb.Opacity = .4; number.Opacity = .6; }
        var tip = Localization.Format($"片段 {row.Number}") + " · " + EditorTime.FormatQuick(segment.Start) + " – " + EditorTime.FormatQuick(segment.End);
        ToolTip.SetTip(grid, tip);
        grid.ContextRequested += (_, e) => { Select(segment.Id); OpenSegmentMenu(grid, segment.Id); e.Handled = true; };
        grid.DoubleTapped += (_, _) => { Select(segment.Id); PlaySelected(); };
        var line = new Border { Child = grid, BorderThickness = new(0, 0, 0, .5) };
        MsUi.Brush(line, Border.BorderBrushProperty, "AcHairline");
        return line;
    }

    partial void DisposeThumbnails()
    {
        foreach (var bitmap in _rowThumbs.Values) bitmap.Dispose();
        _rowThumbs.Clear(); _pendingThumbs.Clear();
    }

    private static Border Tag(string text, string background, string foreground)
    {
        var label = new TextBlock { Text = Localization.Text(text), FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        MsUi.Brush(label, TextBlock.ForegroundProperty, foreground);
        var tag = new Border { Padding = new(5, 1), CornerRadius = new(4), Child = label, VerticalAlignment = VerticalAlignment.Center };
        MsUi.Brush(tag, Border.BackgroundProperty, background);
        return tag;
    }

    private void RequestRowThumb(double seconds, Action<Bitmap> show)
    {
        var key = AdjustTime.Key(seconds);
        if (_rowThumbs.TryGetValue(key, out var cached)) { show(cached); return; }
        if (_info?.HasVideo != true && _info is not null) return;
        _pendingThumbs.Add((key, seconds, show));
        if (_info is not null) _ = LoadRowThumbsAsync();
    }

    private readonly List<(long Key, double Seconds, Action<Bitmap> Show)> _pendingThumbs = [];
    private bool _loadingThumbs;
    private async Task LoadRowThumbsAsync()
    {
        if (_loadingThumbs || _closed) return;
        _loadingThumbs = true;
        try
        {
            while (_pendingThumbs.Count > 0 && !_closed)
            {
                var (key, seconds, show) = _pendingThumbs[0]; _pendingThumbs.RemoveAt(0);
                if (_rowThumbs.TryGetValue(key, out var cached)) { show(cached); continue; }
                var bitmap = await DecodeAsync(seconds, 96, 54);
                if (bitmap is null) continue;
                if (_closed) { bitmap.Dispose(); continue; }
                _rowThumbs[key] = bitmap; show(bitmap);
                foreach (var waiting in _pendingThumbs.Where(p => p.Key == key).ToArray()) { waiting.Show(bitmap); _pendingThumbs.Remove(waiting); }
            }
        }
        finally { _loadingThumbs = false; }
    }

    /// <summary>Decode one frame off the UI thread; null when cancelled or unreadable.</summary>
    private async Task<Bitmap?> DecodeAsync(double seconds, int width, int height, CancellationToken token = default)
    {
        if (_info?.HasVideo != true) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, token);
        try
        {
            await _thumbGate.WaitAsync(linked.Token);
            try
            {
                var info = _info; var time = Math.Clamp(seconds, 0, Math.Max(0, info.Duration - .001));
                return await Task.Run(async () =>
                {
                    var data = await _engine.Thumbnail(_session.SourcePath, time, width, height, linked.Token, pad: false, videoStreamIndex: info.VideoStreamIndex).ConfigureAwait(false);
                    using var memory = new MemoryStream(data); return new Bitmap(memory);
                }, linked.Token);
            }
            finally { _thumbGate.Release(); }
        }
        catch (OperationCanceledException) { return null; }
        catch (ObjectDisposedException) { return null; }
        catch (Exception error) { AppDiagnostics.Record("Person clip adjust thumbnail", error); return null; }
    }
}
