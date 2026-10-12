using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaMedia.FileUi;

namespace AvaMedia.Browser;

public sealed partial class ConsoleView
{
    private sealed record MergeInfo(double Duration, int Width, int Height, string Codec, Bitmap? Thumbnail);
    private readonly Dictionary<string, MergeInfo?> _mergeInfo = new(StringComparer.Ordinal);
    private readonly Grid _mergeView = new() { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 10 };
    private readonly StackPanel _mergeRows = new() { Spacing = 0 };
    private readonly Grid _mergeBar = new() { Height = 34 };
    private readonly StackPanel _mergeSide = new() { Spacing = 10 };
    private readonly Border _mergePreview = new() { Height = 150, CornerRadius = new(9), ClipToBounds = true, Background = Brushes.Black };
    private readonly TextBlock _mergeSummary = Ui.Text("", "secondary", raw: true);
    private bool _mergeBuilt;
    private string? _dragPath;
    private int _dragTarget = -1;
    private const double MergeRowHeight = 58;

    private void BuildMerge()
    {
        if (_mergeBuilt) return; _mergeBuilt = true;
        var header = new Grid { ColumnDefinitions = new("*,Auto") };
        header.Children.Add(Ui.Text("合并顺序", "section"));
        var hint = Ui.Text("拖动左侧手柄或使用箭头调整顺序", "caption"); Grid.SetColumn(hint, 1); header.Children.Add(hint);
        _mergeView.Children.Add(header);
        var list = new ScrollViewer { Content = _mergeRows }; Grid.SetRow(list, 1); _mergeView.Children.Add(list);
        var bar = new Border { CornerRadius = new(8), ClipToBounds = true, Child = _mergeBar }; Grid.SetRow(bar, 2); _mergeView.Children.Add(bar);
        _mergeSide.Children.Add(Ui.Text("输出预览", "section")); _mergeSide.Children.Add(_mergePreview); _mergeSide.Children.Add(_mergeSummary);
        var note = Ui.Text("分辨率或编码不一致时会自动重新编码到第一个视频的规格。", "caption"); note.TextWrapping = TextWrapping.Wrap;
        _mergeSide.Children.Add(note);
    }
    private void RenderMerge()
    {
        BuildMerge();
        if (_activeFeature != "join") return;
        _mergeRows.Children.Clear(); _mergeBar.Children.Clear(); _mergeBar.ColumnDefinitions.Clear();
        var items = IncludedInputs.Where(pair => !pair.Value.IsFolder).ToArray();
        if (items.Length == 0)
        {
            var empty = Ui.Text("请先在服务器文件中添加要合并的视频。", "secondary"); empty.Margin = new(4, 20); _mergeRows.Children.Add(empty);
            _mergePreview.Child = null; _mergeSummary.Text = ""; return;
        }
        var colors = new[] { "#007AFF", "#34C759", "#FF9500", "#AF52DE", "#FF2D55", "#5AC8FA" };
        double total = 0; var known = true;
        for (var index = 0; index < items.Length; index++)
        {
            var (path, item) = items[index]; var captured = index;
            var info = _mergeInfo.GetValueOrDefault(path);
            if (!_mergeInfo.ContainsKey(path)) _ = LoadMergeInfo(path);
            if (info is null) known = false; else total += info.Duration;
            var row = new Grid { ColumnDefinitions = new("Auto,Auto,Auto,*,Auto,Auto"), ColumnSpacing = 10, Height = MergeRowHeight, Background = Brushes.Transparent };
            var grip = new Border { Child = Ui.Icon("grip-vertical", 15, Palette.Secondary), Cursor = new Cursor(StandardCursorType.SizeNorthSouth), Background = Brushes.Transparent, Padding = new(2, 8), VerticalAlignment = VerticalAlignment.Center };
            grip.PointerPressed += (_, e) => { _dragPath = path; _dragTarget = captured; e.Pointer.Capture(grip); e.Handled = true; };
            grip.PointerMoved += (_, e) =>
            {
                if (_dragPath != path) return;
                var target = Math.Clamp((int)Math.Floor(e.GetPosition(_mergeRows).Y / (MergeRowHeight + 1)), 0, items.Length - 1);
                if (target != _dragTarget) { _dragTarget = target; HighlightMergeTarget(target); }
            };
            grip.PointerReleased += (_, e) =>
            {
                if (_dragPath != path) return; e.Pointer.Capture(null);
                var target = _dragTarget; _dragPath = null; _dragTarget = -1;
                if (target != captured) MoveInputTo(path, target); else RenderMerge();
            };
            row.Children.Add(grip);
            var number = Ui.Text((index + 1).ToString(), "strong", raw: true); number.VerticalAlignment = VerticalAlignment.Center; number.Width = 16;
            Grid.SetColumn(number, 1); row.Children.Add(number);
            var thumb = new Border { Width = 72, Height = 42, CornerRadius = new(6), ClipToBounds = true, Background = new SolidColorBrush(Color.Parse("#1C1C1E")), VerticalAlignment = VerticalAlignment.Center,
                Child = info?.Thumbnail is { } bitmap ? new Image { Source = bitmap, Stretch = Stretch.UniformToFill } : Ui.Icon("film", 15, Palette.Secondary) };
            Grid.SetColumn(thumb, 2); row.Children.Add(thumb);
            var name = Ui.Text(item.Name, "strong", raw: true, trim: true);
            var meta = Ui.Text(info is null ? L.T("正在读取…") : $"{ClipTimeline.Short(info.Duration)} · {info.Width}×{info.Height} · {info.Codec}", "caption", raw: true);
            var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { name, meta } };
            Grid.SetColumn(texts, 3); row.Children.Add(texts);
            if (index > 0 && info is not null && _mergeInfo.GetValueOrDefault(items[0].Key) is { } first && (first.Width != info.Width || first.Height != info.Height))
            {
                var warn = Ui.Pill(Ui.Text("需重新编码", "caption"), new SolidColorBrush(Color.Parse("#FFF4E5")), new(7, 2)); warn.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(warn, 4); row.Children.Add(warn);
            }
            var moves = Ui.Row(0, IconButton("chevron-up", "上移", () => { MoveInput(path, -1); return Task.CompletedTask; }),
                IconButton("chevron-down", "下移", () => { MoveInput(path, 1); return Task.CompletedTask; }),
                IconButton("x", "移除", () => { _inputs.Remove(path); RenderInputs(); return Task.CompletedTask; }));
            moves.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(moves, 5); row.Children.Add(moves);
            var holder = new Border { Child = row, CornerRadius = new(8), Padding = new(6, 0), Tag = index };
            _mergeRows.Children.Add(holder);
            if (index < items.Length - 1) _mergeRows.Children.Add(Ui.Hairline(40));
            // Proportional bar.
            _mergeBar.ColumnDefinitions.Add(new ColumnDefinition(Math.Max(.5, info?.Duration ?? 1), GridUnitType.Star));
            var block = new Border { Background = new SolidColorBrush(Color.Parse(colors[index % colors.Length])), Margin = new(index == 0 ? 0 : 1, 0, 0, 0),
                Child = new TextBlock { Text = info is null ? $"{index + 1}" : $"{index + 1} · {ClipTimeline.Short(info.Duration)}", Foreground = Brushes.White, FontSize = 11.5, Margin = new(8, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis } };
            block.Classes.Add("raw");
            Grid.SetColumn(block, index); _mergeBar.Children.Add(block);
        }
        _mergeSummary.Text = L.F("{0} 个片段 · 总时长 {1}", items.Length, known ? ClipTimeline.Short(total) : "…");
        var firstThumb = _mergeInfo.GetValueOrDefault(items[0].Key)?.Thumbnail;
        _mergePreview.Child = firstThumb is null ? null : new Image { Source = firstThumb, Stretch = Stretch.Uniform };
    }
    private void HighlightMergeTarget(int target)
    {
        foreach (var holder in _mergeRows.Children.OfType<Border>().Where(border => border.Tag is int))
            holder.Background = holder.Tag is int index && index == target ? Palette.BlueSoft : null;
    }
    private async Task LoadMergeInfo(string path)
    {
        _mergeInfo[path] = null;
        try
        {
            var probe = await RemoteApi.Call("probe", new() { ["path"] = path });
            var info = probe["info"] ?? probe;
            var duration = info["duration"]?.GetValue<double>() ?? 0;
            Bitmap? bitmap = null;
            try
            {
                var (image, _) = await RemotePreview.Read(path, Math.Min(1, duration / 2), 640, 0, 0, CancellationToken.None);
                using var stream = new MemoryStream(image); bitmap = new Bitmap(stream);
            }
            catch (Exception) { }
            _mergeInfo[path] = new(duration, info["width"]?.GetValue<int>() ?? 0, info["height"]?.GetValue<int>() ?? 0, info.Text("videoCodec"), bitmap);
            if (_activeFeature == "join" && _dragPath is null) RenderMerge();
        }
        catch (Exception) { _mergeInfo.Remove(path); }
    }
}
