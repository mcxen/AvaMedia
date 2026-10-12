using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace AvaMedia.Browser;

/// <summary>
/// Web run / result view of one AI 媒体整理 task. Polls a compact progress endpoint, shows only the latest 30 files,
/// and loads small server-side JPEG thumbnails lazily (one request per visible cover, cached per page).
/// </summary>
internal sealed class ClassificationPage : UserControl
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly IBrush BlueSoft = new SolidColorBrush(Color.Parse("#8CC0FF")), Track = new SolidColorBrush(Color.Parse("#E8E8ED")),
        DarkChip = new SolidColorBrush(Color.Parse("#CC1D1D1F"));
    private readonly string _id;
    private readonly Func<bool> _mobile;
    private readonly Action _back;
    private readonly Func<string, string, Task> _control;
    private readonly Func<string, Task> _openOrganize;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly Dictionary<string, Task<Bitmap?>> _images = new();
    private readonly SemaphoreSlim _gate = new(4, 4);
    private readonly ContentControl _host = new();
    private JsonNode? _progress, _files, _summary, _histogram;
    private Dictionary<string, int> _indexes = new(StringComparer.Ordinal);
    private bool _loading, _detached;
    private string? _renderKey;

    public ClassificationPage(string id, Func<bool> mobile, Action back, Func<string, string, Task> control, Func<string, Task> openOrganize)
    {
        _id = id; _mobile = mobile; _back = back; _control = control; _openOrganize = openOrganize;
        Content = _host;
        _poll.Tick += async (_, _) => await Refresh();
        AttachedToVisualTree += async (_, _) => { _detached = false; _poll.Start(); await Refresh(); };
        DetachedFromVisualTree += (_, _) => { _detached = true; _poll.Stop(); };
    }

    private string Base => BrowserHost.Origin() + "/web/tasks/" + _id + "/classification/";
    private static async Task<JsonNode?> Json(string url)
    {
        using var response = await Client.GetAsync(url);
        if (!response.IsSuccessStatusCode) return null;
        return JsonNode.Parse(await response.Content.ReadAsStringAsync());
    }

    private bool Active => _progress?["task"].Text("state") is "Waiting" or "Running" or "Paused" or "Stopping";

    public async Task Refresh()
    {
        if (_loading || _detached) return; _loading = true;
        try
        {
            _progress = await Json(Base + "progress") ?? _progress;
            if (_progress is null) return;
            if (_indexes.Count == 0)
                _indexes = _progress["task"]?["inputs"].Items().Select((node, index) => (node.ToString(), index))
                    .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.First().index, StringComparer.Ordinal) ?? new();
            if (Active)
            {
                // Latest 30 files only; the full list stays paginated on the server.
                var total = _progress["counts"]?["total"]?.GetValue<int>() ?? 0;
                var done = (_progress["counts"]?["completed"]?.GetValue<int>() ?? 0) + (_progress["counts"]?["failed"]?.GetValue<int>() ?? 0);
                var offset = Math.Max(0, Math.Min(total - 30, done - 15));
                _files = await Json(Base + "files?offset=" + offset + "&limit=30") ?? _files;
            }
            else
            {
                _poll.Interval = TimeSpan.FromSeconds(6);
                _summary ??= await Json(Base + "summary");
                _histogram = await Json(Base + "histogram") ?? _histogram;
                _files = await Json(Base + "files?offset=0&limit=200&state=pending") ?? _files;
            }
            Render();
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
        finally { _loading = false; }
    }

    private void Render()
    {
        var key = _progress?.ToJsonString().GetHashCode() + ":" + _files?.ToJsonString().GetHashCode() + ":" + _mobile();
        if (key == _renderKey) return; _renderKey = key;
        _host.Content = Active ? (_mobile() ? Mobile() : Run()) : Result();
    }

    // ---------- helpers ----------
    private static TextBlock Text(string text, double size = 13, FontWeight weight = FontWeight.Normal, IBrush? brush = null, bool raw = false)
    {
        var block = Ui.Text(raw ? text : L.T(text), raw: raw, trim: true); block.FontSize = size; block.FontWeight = weight;
        block.Foreground = brush ?? Palette.Text; return block;
    }
    private static Border Card(Control child, double padding = 14) => Ui.Card(child, new Thickness(padding));
    private static Control Bar(double a, IBrush fill, double height = 4, double b = 0)
    {
        a = Math.Clamp(a, 0, 1); b = Math.Clamp(b, 0, 1 - a);
        var grid = new Grid { Height = height, ColumnDefinitions = new() };
        grid.ColumnDefinitions.Add(new(a, GridUnitType.Star)); grid.ColumnDefinitions.Add(new(b, GridUnitType.Star)); grid.ColumnDefinitions.Add(new(1 - a - b, GridUnitType.Star));
        var back = new Border { Background = Track, CornerRadius = new(height / 2) }; Grid.SetColumnSpan(back, 3); grid.Children.Add(back);
        grid.Children.Add(new Border { Background = fill, CornerRadius = new(height / 2) });
        var second = new Border { Background = BlueSoft, CornerRadius = new(height / 2) }; Grid.SetColumn(second, 1); grid.Children.Add(second);
        return grid;
    }
    private static Border Chip(string label, string? value, IBrush? background = null)
    {
        var text = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines!.Add(new Avalonia.Controls.Documents.Run(L.T(label)) { Foreground = Palette.Text });
        if (value is not null) text.Inlines.Add(new Avalonia.Controls.Documents.Run("  " + L.T(value)) { Foreground = Palette.Blue, FontWeight = FontWeight.SemiBold });
        return new Border { Background = background ?? Palette.Fill, CornerRadius = new(11), Padding = new(10, 4), Child = text };
    }
    private static Button Pill(string text, string? icon, Func<Task> click, bool primary = false, bool danger = false)
    {
        var fg = primary ? Brushes.White : danger ? Palette.Red : Palette.Text;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (icon is not null) content.Children.Add(Ui.Icon(icon, 14, fg));
        var label = Ui.Text(L.T(text)); label.Foreground = fg; label.FontSize = 12.5; content.Children.Add(label);
        var button = new Button { Content = content, Background = primary ? Palette.Blue : Palette.Fill, CornerRadius = new(15), Padding = new(14, 6), BorderThickness = new(0) };
        button.Click += async (_, _) => { try { await click(); } catch (Exception) { } };
        return button;
    }
    private static string Clock(double seconds) { var t = TimeSpan.FromSeconds(Math.Max(0, seconds)); return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"; }
    private static double Num(JsonNode? node, string key) => node?[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
    private static string State(JsonNode? file)
    {
        var node = file?["state"];
        if (node is JsonValue v && v.TryGetValue<int>(out var n)) return new[] { "queued", "processing", "done", "pending", "failed" }[Math.Clamp(n, 0, 4)];
        return (node?.ToString() ?? "queued").ToLowerInvariant();
    }

    /// <summary>Lazy server thumbnail: requested once the control is attached (realized), bounded to 4 parallel fetches.</summary>
    private Control Thumb(string? path, double width, double height, int decode = 120, double? seconds = null, string? mosaicQuery = null, double radius = 6)
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        var border = new Border { Width = width, Height = height, CornerRadius = new(radius), ClipToBounds = true, Background = Palette.Fill, Child = image };
        string? url = mosaicQuery is not null ? Base + "mosaic?" + mosaicQuery + "&width=" + decode + "&height=" + (int)(decode * height / width)
            : path is not null && _indexes.TryGetValue(path, out var index) ? Base + "thumb?index=" + index + "&width=" + decode
                + (seconds is { } s ? "&seconds=" + s.ToString("0.###", CultureInfo.InvariantCulture) : "") : null;
        if (url is null) return border;
        border.AttachedToVisualTree += async (_, _) =>
        {
            if (!_images.TryGetValue(url, out var task)) _images[url] = task = Load(url, decode);
            if (await task is { } bitmap) image.Source = bitmap;
        };
        return border;
    }
    private async Task<Bitmap?> Load(string url, int width)
    {
        await _gate.WaitAsync();
        try
        {
            using var response = await Client.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch (Exception) { return null; }
        finally { _gate.Release(); }
    }

    // ---------- run ----------
    private Control Header(bool showControls)
    {
        var task = _progress!["task"]!;
        var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto,Auto"), ColumnSpacing = 16 };
        var back = new Button { Content = Ui.Content(L.T("所有任务"), "chevron-left", Palette.Blue, 14), Classes = { "link" } };
        back.Click += (_, _) => _back(); grid.Children.Add(back);
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(Text(task.Text("name") + " · " + L.T("AI 媒体整理"), 17, FontWeight.Bold, raw: true));
        var rules = _progress["rules"].Items().Select(r => L.T(r.Text("name"))).ToArray();
        titles.Children.Add(Text($"{rules.Length} {L.T("组规则")} · {task["inputs"].Items().Count()} {L.T("个文件")} · {L.T("网页")}", 11.5, brush: Palette.Secondary, raw: true));
        Grid.SetColumn(titles, 1); grid.Children.Add(titles);
        if (showControls)
        {
            var percent = Text($"{Num(_progress, "percent"):0}%", 26, FontWeight.Bold, raw: true); Grid.SetColumn(percent, 2); grid.Children.Add(percent);
            var tags = _progress["stages"].Items().FirstOrDefault(s => s.Text("stage") is "Tags" or "1");
            var progressBox = new StackPanel { Spacing = 4, Width = 210, VerticalAlignment = VerticalAlignment.Center };
            progressBox.Children.Add(Bar(Num(_progress, "percent") / 100, Palette.Blue, 6));
            progressBox.Children.Add(Text($"{L.T(tags.Text("name"))} · {Num(tags, "done"):0} / {Num(tags, "total"):0} · {L.T("已用")} {Clock(Num(_progress, "elapsedSeconds"))}", 11, brush: Palette.Secondary, raw: true));
            Grid.SetColumn(progressBox, 3); grid.Children.Add(progressBox);
            var paused = task.Text("state") == "Paused";
            var actions = Ui.Row(8, Pill(paused ? "继续" : "暂停", paused ? "play" : "pause", () => _control(_id, paused ? "resume" : "pause")),
                Pill("停止", "square", () => _control(_id, "stop"), danger: true));
            Grid.SetColumn(actions, 4); grid.Children.Add(actions);
        }
        return Card(grid, 14);
    }

    private Control Stages()
    {
        var stages = _progress!["stages"].Items().Where(s => s.Flag("enabled")).ToArray();
        var grid = new Grid { ColumnDefinitions = new() };
        for (var i = 0; i < stages.Length; i++)
        {
            if (i > 0) { grid.ColumnDefinitions.Add(new(new GridLength(18))); var c = Ui.Icon("chevron-right", 12, Palette.Secondary); c.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(c, i * 2 - 1); grid.Children.Add(c); }
            grid.ColumnDefinitions.Add(new(1, GridUnitType.Star));
            var stage = stages[i]; var last = i == stages.Length - 1;
            var done = Num(stage, "total") > 0 && Num(stage, "done") >= Num(stage, "total");
            var color = done ? Palette.Green : stage.Flag("active") || Num(stage, "done") > 0 ? Palette.Blue : Palette.Secondary;
            var body = new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto"), ColumnDefinitions = new("*,Auto") };
            body.Children.Add(Ui.Row(6, new Avalonia.Controls.Shapes.Ellipse { Width = 7, Height = 7, Fill = color }, Text(stage.Text("name"), 12.5, FontWeight.SemiBold)));
            var model = Text(stage.Text("stage") is "Scan" or "0" ? Num(stage, "total").ToString("0") : stage.Text("model"), 11, brush: Palette.Secondary); model.Margin = new(13, 0, 0, 0);
            Grid.SetRow(model, 1); body.Children.Add(model);
            var count = Text(last ? $"{Num(_progress?["regroup"], "ordinal"):0} {L.T("次")}" : $"{Num(stage, "done"):0} / {Num(stage, "total"):0}", 14, FontWeight.Bold, raw: true);
            count.Margin = new(0, 14, 0, 6); Grid.SetRow(count, 2); Grid.SetColumn(count, 1); body.Children.Add(count);
            var bar = Bar(last ? 0.3 : Num(stage, "fraction"), done ? Palette.Green : Palette.Blue); Grid.SetRow(bar, 3); Grid.SetColumnSpan(bar, 2); body.Children.Add(bar);
            var card = Card(body, 12); if (stage.Flag("active")) { card.BorderBrush = Palette.Blue; card.BorderThickness = new(1.5); }
            Grid.SetColumn(card, i * 2); grid.Children.Add(card);
        }
        return grid;
    }

    private Control Look(bool compact)
    {
        var current = _progress!["current"];
        if (current is null) return Card(Text("等待第一个文件完成分析", 12, brush: Palette.Secondary));
        var path = current.Text("path"); var video = current.Flag("isVideo");
        var frames = current["frames"].Items().ToArray();
        var preview = new Panel { Height = compact ? 230 : 220 };
        preview.Children.Add(Thumb(path, double.NaN, double.NaN, 480, video && frames.Length > 0 ? Num(frames[frames.Length / 2], "seconds") : null, radius: 10));
        var canvas = new Canvas(); preview.Children.Add(canvas);
        var spots = new[] { (.55, .18), (.08, .3), (.06, .52), (.5, .7) };
        var tags = current["tags"].Items().Take(4).ToArray();
        preview.SizeChanged += (_, e) =>
        {
            canvas.Children.Clear();
            for (var i = 0; i < tags.Length; i++)
            {
                var chip = Chip(tags[i].Text("tag"), Num(tags[i], "score").ToString("0.00"), Brushes.White);
                Canvas.SetLeft(chip, spots[i].Item1 * e.NewSize.Width); Canvas.SetTop(chip, spots[i].Item2 * e.NewSize.Height); canvas.Children.Add(chip);
            }
            if (current.Text("scene") is { Length: > 0 } scene)
            {
                var chip = new Border { Background = DarkChip, CornerRadius = new(11), Padding = new(9, 3), Child = new TextBlock { FontSize = 11.5, Foreground = Brushes.White, Text = $"{L.T("场景")} · {L.T(scene)}  {Num(current, "sceneScore"):0.00}" } };
                Canvas.SetLeft(chip, 8); Canvas.SetBottom(chip, 8); canvas.Children.Add(chip);
            }
            if (video)
            {
                var chip = new Border { Background = Palette.Blue, CornerRadius = new(11), Padding = new(9, 3), Child = new TextBlock { FontSize = 11.5, Foreground = Brushes.White, Text = $"{L.T("视频")} {TimeSpan.FromSeconds(Num(current, "durationSeconds")):mm\\:ss}" } };
                Canvas.SetRight(chip, 8); Canvas.SetTop(chip, 8); canvas.Children.Add(chip);
            }
        };
        var info = new StackPanel { Spacing = 8 };
        info.Children.Add(Ui.Row(8, Text("AI 正在看", 13, FontWeight.Bold), Text(current.Text("name") + (compact ? $" · {frames.Length} {L.T("帧")}" : ""), 12, brush: Palette.Secondary, raw: true)));
        var strip = new Grid { ColumnDefinitions = new() };
        var shown = frames.Length > 0 ? frames : [new JsonObject { ["seconds"] = 0, ["consistent"] = true }];
        for (var i = 0; i < shown.Length; i++)
        {
            strip.ColumnDefinitions.Add(new(1, GridUnitType.Star));
            var cell = new StackPanel { Spacing = 3, Margin = new(i == 0 ? 0 : 3, 0, 0, 0) };
            cell.Children.Add(Thumb(path, double.NaN, 38, 96, video ? Num(shown[i], "seconds") : null));
            if (!compact) cell.Children.Add(new Border { Height = 4, CornerRadius = new(2), Background = shown[i].Flag("consistent") ? Palette.Blue : Palette.Orange });
            Grid.SetColumn(cell, i); strip.Children.Add(cell);
        }
        if (!compact) info.Children.Add(Text($"{L.T("采样画面")} · {shown.Length} {L.T("帧")}", 11.5, brush: Palette.Secondary, raw: true));
        info.Children.Add(strip);
        if (!compact)
        {
            if (video) info.Children.Add(Text($"{L.T("每帧归属一致率")} {Num(current, "frameConsistency"):P0}" + (Num(current, "inconsistentFrame") > 0 ? $" · {L.T("第")} {Num(current, "inconsistentFrame"):0} {L.T("帧服装不同")}" : ""), 11.5, brush: Palette.Secondary, raw: true));
            var colors = current["outfitColors"].Items().ToArray();
            info.Children.Add(Text($"{L.T("服装主色")} · {L.T("人物占画面")} {Num(current, "personRatio"):P0}", 11.5, brush: Palette.Secondary, raw: true));
            var bar = new Grid { Height = 12, ColumnDefinitions = new() };
            for (var i = 0; i < colors.Length; i++) { bar.ColumnDefinitions.Add(new(Math.Max(.001, Num(colors[i], "share")), GridUnitType.Star)); var b = new Border { Background = new SolidColorBrush(Color.Parse(colors[i].Text("hex"))) }; Grid.SetColumn(b, i); bar.Children.Add(b); }
            info.Children.Add(new Border { CornerRadius = new(4), ClipToBounds = true, Child = bar });
            info.Children.Add(Text("各规则判断", 11.5, brush: Palette.Secondary));
            var verdicts = new WrapPanel();
            foreach (var v in current["verdicts"].Items()) { var chip = Chip(v.Text("ruleName"), v.Text("value")); chip.Margin = new(0, 0, 6, 4); verdicts.Children.Add(chip); }
            info.Children.Add(verdicts);
        }
        var reason = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        reason.Inlines!.Add(new Avalonia.Controls.Documents.Run(L.T("归组理由")) { FontWeight = FontWeight.Bold });
        reason.Inlines.Add(new Avalonia.Controls.Documents.Run(" " + current.Text("reason")) { Foreground = compact ? Palette.Secondary : Palette.Text });
        info.Children.Add(compact ? reason : new Border { Background = Palette.BlueSoft, CornerRadius = new(8), Padding = new(10, 7), Child = reason });
        if (compact)
        {
            var stack = new StackPanel(); stack.Children.Add(preview); stack.Children.Add(new Border { Padding = new(14), Child = info });
            return Ui.Card(stack);
        }
        var grid = new Grid { ColumnDefinitions = new("240,*"), ColumnSpacing = 14 };
        grid.Children.Add(preview); Grid.SetColumn(info, 1); grid.Children.Add(info);
        return Card(grid);
    }

    private Control Wall()
    {
        var wrap = new WrapPanel();
        foreach (var file in _files?["items"].Items() ?? [])
        {
            var state = State(file);
            var tile = new Panel { Width = 40, Height = 40, Margin = new(0, 0, 6, 6) };
            var thumb = Thumb(file.Text("path"), 40, 40, 80, file.Flag("isVideo") ? 1 : null); thumb.Opacity = state == "queued" ? .35 : 1; tile.Children.Add(thumb);
            if (state == "processing") tile.Children.Add(new Border { BorderBrush = Palette.Blue, BorderThickness = new(2), CornerRadius = new(8), Margin = new(-2) });
            (IBrush? fill, string glyph) badge = state switch { "done" => (Palette.Green, "check"), "pending" => (Palette.Orange, "?"), "failed" => (Palette.Red, "!"), _ => (null, "") };
            if (badge.fill is not null)
                tile.Children.Add(new Border { Width = 15, Height = 15, CornerRadius = new(8), Background = badge.fill, BorderBrush = Brushes.White, BorderThickness = new(1.5),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(0, 0, 0, 2),
                    Child = badge.glyph == "check" ? Ui.Icon("check", 9, Brushes.White) : new TextBlock { Text = badge.glyph, FontSize = 9, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center } });
            ToolTip.SetTip(tile, file.Text("name")); wrap.Children.Add(tile);
        }
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Ui.Row(8, Text("文件流", 13, FontWeight.Bold), Text("最近 30 个", 11.5, brush: Palette.Secondary)));
        panel.Children.Add(wrap);
        return Card(panel);
    }

    private Control Groups()
    {
        var list = new StackPanel();
        list.Children.Add(Ui.Row(8, Text("分组正在形成", 13, FontWeight.Bold), Text($"{L.T("第")} {Num(_progress?["regroup"], "ordinal"):0} {L.T("次归组")}", 11.5, brush: Palette.Secondary, raw: true)));
        var index = 0;
        foreach (var group in _progress!["groups"].Items().OrderByDescending(g => Num(g, "count")).Take(12))
        {
            index++;
            var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10, Margin = new(0, 8) };
            var cover = new Panel();
            cover.Children.Add(Thumb(null, 52, 60, 104, mosaicQuery: "ruleId=" + Uri.EscapeDataString(group.Text("ruleId")) + "&categoryId=" + Uri.EscapeDataString(group.Text("categoryId"))));
            if (Num(group, "added") > 0) cover.Children.Add(new Border { Background = Palette.Blue, CornerRadius = new(8), Padding = new(5, 1), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, -4, -6, 0),
                Child = new TextBlock { Text = "+" + Num(group, "added").ToString("0"), FontSize = 10.5, FontWeight = FontWeight.Bold, Foreground = Brushes.White } });
            row.Children.Add(cover);
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(Text($"{index:00} {group.Text("name")}", 12.5, FontWeight.SemiBold, raw: true));
            text.Children.Add(Text(group.Text("shootName"), 11, brush: Palette.Secondary, raw: true));
            var count = new TextBlock { FontSize = 11, Foreground = Palette.Secondary };
            count.Inlines!.Add(new Avalonia.Controls.Documents.Run(Num(group, "count").ToString("0")) { FontSize = 17, FontWeight = FontWeight.Bold, Foreground = Palette.Text });
            count.Inlines.Add(new Avalonia.Controls.Documents.Run(" " + L.T("个文件"))); text.Children.Add(count);
            Grid.SetColumn(text, 1); row.Children.Add(text);
            list.Children.Add(row); list.Children.Add(Ui.Hairline());
        }
        return Card(new ScrollViewer { Content = list });
    }

    private Control Run()
    {
        var grid = new Grid { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 12, Margin = new(18, 2, 18, 18) };
        grid.Children.Add(Header(true));
        var stages = Stages(); Grid.SetRow(stages, 1); grid.Children.Add(stages);
        var main = new Grid { ColumnDefinitions = new("*,240"), ColumnSpacing = 12 };
        var left = new StackPanel { Spacing = 12 }; left.Children.Add(Look(false)); left.Children.Add(Wall());
        main.Children.Add(new ScrollViewer { Content = left });
        var groups = Groups(); Grid.SetColumn(groups, 1); main.Children.Add(groups);
        Grid.SetRow(main, 2); grid.Children.Add(main);
        return grid;
    }

    private Control Mobile()
    {
        var task = _progress!["task"]!;
        var stack = new StackPanel { Spacing = 12, Margin = new(16, 0, 16, 90) };
        var nav = new Grid { ColumnDefinitions = new("Auto,*,Auto") };
        var back = new Button { Content = Ui.Content(L.T("任务"), "chevron-left", Palette.Blue, 16), Classes = { "link" } }; back.Click += (_, _) => _back(); nav.Children.Add(back);
        var title = Text(task.Text("name"), 17, FontWeight.Bold, raw: true); title.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(title, 1); nav.Children.Add(title);
        stack.Children.Add(nav);
        var percent = new Grid { ColumnDefinitions = new("*,Auto") };
        percent.Children.Add(Text($"{Num(_progress, "percent"):0}%", 34, FontWeight.Bold, raw: true));
        var elapsed = Text($"{L.T("已用")} {Clock(Num(_progress, "elapsedSeconds"))}", 12, brush: Palette.Secondary, raw: true); Grid.SetColumn(elapsed, 1); percent.Children.Add(elapsed);
        var top = new StackPanel { Spacing = 8 }; top.Children.Add(percent);
        var counts = _progress["counts"];
        var grouped = _progress["groups"].Items().Sum(g => Num(g, "count"));
        top.Children.Add(Bar(grouped / Math.Max(1, Num(counts, "total")), Palette.Blue, 6, Math.Max(0, Num(counts, "completed") - grouped) / Math.Max(1, Num(counts, "total"))));
        top.Children.Add(Text($"{L.T("已归组")} {grouped:0} · {L.T("待确认")} {Num(counts, "pending"):0} · {_progress["rules"].Items().Count()} {L.T("组规则")}", 12, brush: Palette.Secondary, raw: true));
        stack.Children.Add(Card(top));
        stack.Children.Add(Look(true));
        var pipeline = new StackPanel(); pipeline.Children.Add(Text("流水线", 14, FontWeight.Bold));
        foreach (var stage in _progress["stages"].Items().Where(s => s.Flag("enabled")))
        {
            var row = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 10, MinHeight = 50 };
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(Text(stage.Text("name"), 14, FontWeight.SemiBold)); names.Children.Add(Text(stage.Text("model"), 11.5, brush: Palette.Secondary));
            row.Children.Add(names);
            var value = Text(stage.Text("stage") is "Group" or "4" ? $"{Num(_progress?["regroup"], "ordinal"):0} {L.T("次")}" : Num(stage, "done").ToString("0"), 15, FontWeight.Bold, raw: true);
            Grid.SetColumn(value, 1); row.Children.Add(value);
            var done = Num(stage, "total") > 0 && Num(stage, "done") >= Num(stage, "total");
            var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 7, Height = 7, Fill = done ? Palette.Green : Palette.Blue }; Grid.SetColumn(dot, 2); row.Children.Add(dot);
            pipeline.Children.Add(Ui.Hairline()); pipeline.Children.Add(row);
        }
        stack.Children.Add(Card(pipeline));
        var paused = task.Text("state") == "Paused";
        var bottom = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 12, Margin = new(16, 10, 16, 16), VerticalAlignment = VerticalAlignment.Bottom };
        var pause = Pill(paused ? "继续" : "暂停", paused ? "play" : "pause", () => _control(_id, paused ? "resume" : "pause")); pause.Height = 48; pause.CornerRadius = new(24); pause.HorizontalAlignment = HorizontalAlignment.Stretch; pause.HorizontalContentAlignment = HorizontalAlignment.Center;
        var stop = Pill("停止", "square", () => _control(_id, "stop"), danger: true); stop.Height = 48; stop.CornerRadius = new(24); stop.HorizontalAlignment = HorizontalAlignment.Stretch; stop.HorizontalContentAlignment = HorizontalAlignment.Center; Grid.SetColumn(stop, 1);
        bottom.Children.Add(pause); bottom.Children.Add(stop);
        var root = new Grid();
        root.Children.Add(new ScrollViewer { Content = stack });
        root.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#F2F2F7")), BorderBrush = Palette.Hairline, BorderThickness = new(0, 1, 0, 0), VerticalAlignment = VerticalAlignment.Bottom, Child = bottom });
        return root;
    }

    // ---------- result ----------
    private Control Result()
    {
        var task = _progress!["task"]!; var counts = _progress["counts"];
        var grid = new Grid { ColumnDefinitions = new("*,264"), ColumnSpacing = 12, Margin = new(18, 2, 18, 18) };
        var left = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 14 };
        var head = new Grid { ColumnDefinitions = new("Auto,*,Auto") };
        var back = new Button { Content = Ui.Icon("chevron-left", 16, Palette.Blue), Classes = { "link" }, Margin = new(0, 0, 6, 0) }; back.Click += (_, _) => _back(); head.Children.Add(back);
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(Text(task.Text("name") + " · " + L.T(task.Text("state") == "Completed" ? "已完成" : "已结束"), 17, FontWeight.Bold, raw: true));
        titles.Children.Add(Text($"{L.T("完成")} {Num(counts, "completed"):0} {L.T("个")}，{L.T("待确认")} {Num(counts, "pending"):0} {L.T("个")}，{L.T("失败")} {Num(counts, "failed"):0} {L.T("个")} · {L.T("用时")} {Clock(Num(_progress, "elapsedSeconds"))}", 11.5, brush: Palette.Secondary, raw: true));
        Grid.SetColumn(titles, 1); head.Children.Add(titles);
        left.Children.Add(head);
        var body = new Grid { ColumnDefinitions = new("170,Auto,*"), ColumnSpacing = 14 };
        var before = new StackPanel { Spacing = 8 }; before.Children.Add(Text("整理前", 13, FontWeight.Bold));
        var wrap = new WrapPanel();
        var random = new Random(3);
        foreach (var path in _summary?["sources"].Items().SelectMany(s => s["samplePaths"].Items().Select(p => p.ToString())).Take(30) ?? [])
        { var t = Thumb(path, 26, 26, 52, VideoFormats(path) ? 1 : null, radius: 4); t.Margin = new(2); t.RenderTransform = new RotateTransform(random.Next(-8, 9)); wrap.Children.Add(t); }
        before.Children.Add(wrap);
        before.Children.Add(Text($"{Num(_summary, "images") + Num(_summary, "videos"):0} {L.T("个文件")} · {Ui.Size((long)Num(_summary, "bytes"))}", 11.5, brush: Palette.Secondary, raw: true));
        body.Children.Add(before);
        var arrow = Ui.Icon("arrow-right", 18, Palette.Blue); arrow.Margin = new(0, 110, 0, 0); arrow.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(arrow, 1); body.Children.Add(arrow);
        var after = new StackPanel { Spacing = 12 };
        var shoots = _summary?["shoots"].Items().ToArray() ?? [];
        after.Children.Add(Ui.Row(8, Text("整理后", 13, FontWeight.Bold), Text($"{shoots.Length} {L.T("个拍摄组")} · {shoots.Sum(s => s["groups"].Items().Count())} {L.T("个服装分组")}", 11.5, brush: Palette.Secondary, raw: true)));
        var cols = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 14 };
        var colPanels = new[] { new StackPanel { Spacing = 14 }, new StackPanel { Spacing = 14 } };
        cols.Children.Add(colPanels[0]); Grid.SetColumn(colPanels[1], 1); cols.Children.Add(colPanels[1]);
        var index = 0;
        for (var s = 0; s < shoots.Length; s++)
        {
            var block = new StackPanel { Spacing = 6 };
            block.Children.Add(Ui.Row(6, Ui.Icon("folder-closed", 13, Palette.Blue), Text(shoots[s].Text("name"), 12.5, FontWeight.Bold, raw: true), Text(Num(shoots[s], "count").ToString("0"), 12, brush: Palette.Secondary, raw: true)));
            var groups = new WrapPanel { Margin = new(14, 0, 0, 0) };
            foreach (var group in shoots[s]["groups"].Items().OrderByDescending(g => Num(g, "count")))
            {
                index++;
                var card = new StackPanel { Width = 92, Margin = new(0, 0, 10, 6), Spacing = 2 };
                card.Children.Add(Thumb(null, 88, 100, 176, mosaicQuery: "ruleId=" + Uri.EscapeDataString(group.Text("ruleId")) + "&categoryId=" + Uri.EscapeDataString(group.Text("categoryId"))));
                card.Children.Add(new TextBlock { Text = $"{index:00} {group.Text("name")}", FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxLines = 2, Margin = new(0, 4, 0, 0), Classes = { "raw" } });
                card.Children.Add(Text($"{Num(group, "count"):0} {L.T("个文件")}", 11, brush: Palette.Secondary, raw: true));
                groups.Children.Add(card);
            }
            block.Children.Add(groups); colPanels[s % 2].Children.Add(block);
        }
        after.Children.Add(cols);
        Grid.SetColumn(after, 2); body.Children.Add(new ScrollViewer { Content = after, [Grid.ColumnProperty] = 2 });
        Grid.SetRow(body, 1); left.Children.Add(body);
        grid.Children.Add(Card(left, 16));

        var right = new StackPanel { Spacing = 12 };
        var hist = new StackPanel { Spacing = 6 };
        hist.Children.Add(Ui.Row(8, Text("归组把握", 13, FontWeight.Bold), Text("相似度分布", 11.5, brush: Palette.Secondary)));
        var bars = new Grid { Height = 64, ColumnDefinitions = new() };
        var bins = _histogram?["bins"].Items().Select(b => b.GetValue<int>()).ToArray() ?? [];
        var max = Math.Max(1, bins.DefaultIfEmpty(0).Max()); var min = Num(_histogram, "min"); var hmax = Num(_histogram, "max"); var thr = Num(_histogram, "threshold");
        for (var i = 0; i < bins.Length; i++)
        {
            bars.ColumnDefinitions.Add(new(1, GridUnitType.Star));
            var low = min + (hmax - min) * i / bins.Length;
            var bar = new Border { Background = low < thr ? Palette.Orange : Palette.Blue, CornerRadius = new(2, 2, 0, 0), Margin = new(1.5, 0), VerticalAlignment = VerticalAlignment.Bottom, Height = Math.Max(2, 62.0 * bins[i] / max) };
            Grid.SetColumn(bar, i); bars.Children.Add(bar);
        }
        hist.Children.Add(bars);
        right.Children.Add(Card(hist));
        var organize = new StackPanel { Spacing = 8 };
        organize.Children.Add(Ui.Row(8, Text("分类整理", 13, FontWeight.Bold), Text("远程仅复制", 11.5, brush: Palette.Secondary)));
        organize.Children.Add(new Border { Background = Palette.Fill, CornerRadius = new(7), Padding = new(10, 6), Child = Text(task.Text("output"), 12, raw: true) });
        organize.Children.Add(Text($"{L.T("将复制")} {Num(counts, "completed"):0} {L.T("个文件")}，{L.T("保留源文件。")}", 11.5, brush: Palette.Secondary, raw: true));
        organize.Children.Add(Pill("按此预览复制文件", null, () => _openOrganize(_id), primary: true));
        right.Children.Add(Card(organize));
        var pending = new StackPanel();
        var items = (_files?["items"].Items() ?? []).ToArray();
        pending.Children.Add(Ui.Row(8, Text("待确认", 13, FontWeight.Bold), Text($"{items.Length} {L.T("个")}", 11.5, brush: Palette.Secondary, raw: true)));
        foreach (var file in items.Take(20))
        {
            var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10, Margin = new(0, 8) };
            row.Children.Add(Thumb(file.Text("path"), 46, 46, 92, file.Flag("isVideo") ? 1 : null));
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(Text(file.Text("name"), 12.5, FontWeight.SemiBold, raw: true));
            text.Children.Add(new TextBlock { Text = file.Text("reason"), FontSize = 11, Foreground = Palette.Secondary, TextWrapping = TextWrapping.Wrap, MaxLines = 2, Classes = { "raw" } });
            var choices = new WrapPanel();
            foreach (var option in file["options"].Items().Take(2))
            {
                var choice = Pill(option.Text("name"), null, async () => { await Resolve(file.Text("path"), option.Text("ruleId"), option.Text("categoryId")); });
                choice.Padding = new(8, 2); choice.Margin = new(0, 0, 4, 4); choices.Children.Add(choice);
            }
            text.Children.Add(choices);
            Grid.SetColumn(text, 1); row.Children.Add(text);
            pending.Children.Add(row); pending.Children.Add(Ui.Hairline());
        }
        if (items.Length == 0) pending.Children.Add(Text("没有需要确认的文件", 12, brush: Palette.Secondary));
        right.Children.Add(Card(pending));
        var rightScroll = new ScrollViewer { Content = right }; Grid.SetColumn(rightScroll, 1); grid.Children.Add(rightScroll);
        return grid;
    }

    private static bool VideoFormats(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" or ".m4v";

    private async Task Resolve(string path, string ruleId, string categoryId)
    {
        var body = new JsonObject { ["path"] = path, ["ruleId"] = ruleId, ["categoryId"] = categoryId };
        using var response = await Client.PostAsync(Base + "resolve", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        _renderKey = null; _summary = null; await Refresh();
    }
}
