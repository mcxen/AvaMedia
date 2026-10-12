using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
    private JsonNode? _progress, _files, _summary, _histogram, _latestVisual;
    private Dictionary<string, int> _indexes = new(StringComparer.Ordinal);
    private bool _loading, _detached;
    private string? _renderKey;
    private string? _visualLookupKey, _lastActiveStage;
    private readonly List<IDisposable> _mobileChrome = new();
    private const int ResultPageSize = 20, RulePageSize = 60;
    private string? _resultRule, _destination;
    private int _pendingOffset;
    private JsonNode? _ruleFiles, _organization;
    private Control? _resultSheet;
    private JsonObject? _organizationSubmission;
    private bool _resultBusy;
    private string? _resultError;

    public ClassificationPage(string id, Func<bool> mobile, Action back, Func<string, string, Task> control, Func<string, Task> openOrganize)
    {
        _id = id; _mobile = mobile; _back = back; _control = control; _openOrganize = openOrganize;
        Content = _host;
        _poll.Tick += async (_, _) => await Refresh();
        AttachedToVisualTree += async (_, _) => { _detached = false; _poll.Start(); await Refresh(); };
        DetachedFromVisualTree += (_, _) => { _detached = true; _poll.Stop(); RestoreMobileChrome(); };
        SizeChanged += (_, _) => { if (_progress is not null && !_detached) Render(); };
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
                var done = (int)(Num(_progress["counts"], "completed") + Num(_progress["counts"], "pending") + Num(_progress["counts"], "failed"));
                var offset = Math.Max(0, Math.Min(total - 30, done - 15));
                _files = await Json(Base + "files?offset=" + offset + "&limit=30") ?? _files;
                await RefreshVisual();
            }
            else
            {
                _poll.Interval = TimeSpan.FromSeconds(6);
                _summary ??= await Json(Base + "summary");
                _histogram = await Json(Base + "histogram") ?? _histogram;
                _files = await Json(Base + $"files?offset={_pendingOffset}&limit={ResultPageSize}&state=pending") ?? _files;
                if (_pendingOffset > 0 && Num(_files, "total") <= _pendingOffset)
                {
                    _pendingOffset = Math.Max(0, ((int)Num(_files, "total") - 1) / ResultPageSize * ResultPageSize);
                    _files = await Json(Base + $"files?offset={_pendingOffset}&limit={ResultPageSize}&state=pending") ?? _files;
                }
            }
            Render();
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
        finally { _loading = false; }
    }

    private void Render()
    {
        UpdateMobileChrome();
        var key = _progress?.ToJsonString().GetHashCode() + ":" + _files?.ToJsonString().GetHashCode() + ":" + _latestVisual?.ToJsonString().GetHashCode() + ":" + _mobile()
            + (Active ? "" : $":{(int)Bounds.Width}:{_summary?.ToJsonString().GetHashCode()}:{_histogram?.ToJsonString().GetHashCode()}");
        if (key == _renderKey) return; _renderKey = key;
        _host.Content = Active ? (_mobile() ? Mobile() : Run()) : Result();
    }

    private void RestoreMobileChrome()
    {
        foreach (var value in _mobileChrome) value.Dispose();
        _mobileChrome.Clear();
    }

    private void UpdateMobileChrome()
    {
        if (!Active || !_mobile()) { RestoreMobileChrome(); return; }
        if (_mobileChrome.Count > 0) return;
        if (this.GetVisualAncestors().OfType<ConsoleView>().FirstOrDefault()?.Content is not Grid shell) return;

        // This detail page owns its mobile navigation and actions. Scope the shell overrides
        // to its lifetime; disposing them restores even visibility changes made while open.
        var chrome = shell.Children.Where(child => Grid.GetRow(child) != 1)
            .Concat(shell.Children.OfType<Grid>().Where(panel => Grid.GetRow(panel) == 1)
                .SelectMany(panel => panel.Children.OfType<Button>()));
        foreach (var control in chrome)
            if (control.SetValue(IsVisibleProperty, false, BindingPriority.Animation) is { } value)
                _mobileChrome.Add(value);
    }

    private static bool HasVisual(JsonNode? file) => file is not null &&
        (file["tags"].Items().Any() || file["frames"].Items().Any() || file["outfitColors"].Items().Any()
            || file["verdicts"].Items().Any() || file.Text("scene").Length > 0);

    private async Task RefreshVisual()
    {
        if (HasVisual(_progress?["current"]))
        {
            _latestVisual = _progress!["current"];
            _visualLookupKey = null;
            return;
        }

        var candidates = (_files?["items"].Items() ?? []).Where(HasVisual).ToList();
        var lookupKey = _progress?["counts"]?.ToJsonString() + ":" + Num(_progress?["regroup"], "ordinal");
        if (_visualLookupKey != lookupKey)
        {
            // A reopened paused task can have its last analysed file outside the visible page.
            // Fetch only the tail record of each analysed state; never download the full file list.
            var requests = new[] { (State: "done", Count: Num(_progress?["counts"], "completed")),
                (State: "pending", Count: Num(_progress?["counts"], "pending")) }
                .Where(item => item.Count > 0)
                .Select(item => Json(Base + $"files?state={item.State}&offset={(int)item.Count - 1}&limit=1"));
            try
            {
                var pages = await Task.WhenAll(requests);
                foreach (var page in pages) candidates.AddRange((page?["items"].Items() ?? []).Where(HasVisual));
                if (pages.All(page => page is not null)) _visualLookupKey = lookupKey;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                // The visible page and cached visual can still render while a tail request is unavailable.
            }
        }
        // The API orders records by input index. Keep the last known visual if a page has no results.
        if (_latestVisual is not null)
            candidates.Add(candidates.LastOrDefault(file => file.Text("path") == _latestVisual.Text("path")) ?? _latestVisual);
        _latestVisual = candidates.OrderBy(file => _indexes.GetValueOrDefault(file.Text("path"), -1)).LastOrDefault();
    }

    private JsonNode? FocusStage()
    {
        var stages = _progress?["stages"].Items().Where(stage => stage.Flag("enabled")).ToArray() ?? [];
        var active = stages.FirstOrDefault(stage => stage.Flag("active"));
        if (active is not null) _lastActiveStage = active.Text("stage");
        return active ?? stages.FirstOrDefault(stage => stage.Text("stage") == _lastActiveStage)
            ?? stages.FirstOrDefault(stage => Num(stage, "total") > Num(stage, "done"))
            ?? stages.LastOrDefault();
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
            var stage = FocusStage();
            var progressBox = new StackPanel { Spacing = 4, Width = 260, VerticalAlignment = VerticalAlignment.Center };
            progressBox.Children.Add(Bar(Num(_progress, "percent") / 100, Palette.Blue, 6));
            var count = stage.Text("stage") is "Group" or "4" ? L.F("第 {0} 次归组", Num(_progress["regroup"], "ordinal"))
                : $"{Num(stage, "done"):0} / {Num(stage, "total"):0}";
            progressBox.Children.Add(Text($"{L.T(stage.Text("name"))} · {count} · {L.T("已用")} {Clock(Num(_progress, "elapsedSeconds"))}", 11.5, brush: Palette.Secondary, raw: true));
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
        var focus = FocusStage();
        var grid = new Grid { ColumnDefinitions = new() };
        for (var i = 0; i < stages.Length; i++)
        {
            if (i > 0) { grid.ColumnDefinitions.Add(new(new GridLength(18))); var c = Ui.Icon("chevron-right", 12, Palette.Secondary); c.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(c, i * 2 - 1); grid.Children.Add(c); }
            grid.ColumnDefinitions.Add(new(1, GridUnitType.Star));
            var stage = stages[i]; var grouping = stage.Text("stage") is "Group" or "4";
            var active = ReferenceEquals(stage, focus);
            var done = Num(stage, "total") > 0 && Num(stage, "done") >= Num(stage, "total");
            var color = done ? Palette.Green : active || (!grouping && Num(stage, "done") > 0) ? Palette.Blue : Palette.Tertiary;
            var body = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), ColumnDefinitions = new("*,Auto"), MinHeight = 82 };
            var title = Ui.Row(6, new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = color }, Text(stage.Text("name"), 13, FontWeight.SemiBold));
            Grid.SetColumnSpan(title, 2); body.Children.Add(title);
            var model = Text(stage.Text("stage") is "Scan" or "0" ? Num(stage, "total").ToString("0") : stage.Text("model"), 11.5, brush: Palette.Secondary);
            model.Margin = new(14, 0, 0, 0);
            Grid.SetRow(model, 1); Grid.SetColumnSpan(model, 2); body.Children.Add(model);
            var covers = new Panel { Width = 52, Height = 38, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 4, 0, 8) };
            var samples = (_files?["items"].Items() ?? []).Where(file => stage.Text("stage") switch
            {
                "Tags" or "1" => file["tags"].Items().Any(),
                "Scene" or "2" => file.Text("scene").Length > 0,
                "Outfit" or "3" => file["outfitColors"].Items().Any(),
                "Group" or "4" => file.Text("groupId").Length > 0,
                _ => true
            }).TakeLast(3).ToArray();
            for (var j = 0; j < samples.Length; j++)
            {
                var file = samples[j];
                covers.Children.Add(new Border { Background = Brushes.White, CornerRadius = new(7), Padding = new(1.5),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new(j * 8, (2 - j) * 3, 0, 0),
                    Child = Thumb(file.Text("path"), 30, 30, 60, file.Flag("isVideo") ? 1 : null) });
            }
            Grid.SetRow(covers, 2); body.Children.Add(covers);
            var count = Text(grouping ? $"{Num(_progress?["regroup"], "ordinal"):0} {L.T("次")}" : $"{Num(stage, "done"):0} / {Num(stage, "total"):0}", 14, FontWeight.Bold, raw: true);
            count.VerticalAlignment = VerticalAlignment.Bottom; count.Margin = new(0, 0, 0, 8);
            Grid.SetRow(count, 2); Grid.SetColumn(count, 1); body.Children.Add(count);
            var fraction = grouping ? _progress!["groups"].Items().Sum(group => Num(group, "count")) / Math.Max(1, Num(_progress["counts"], "total")) : Num(stage, "fraction");
            var bar = Bar(fraction, color, 3); Grid.SetRow(bar, 3); Grid.SetColumnSpan(bar, 2); body.Children.Add(bar);
            var card = Card(body); card.BorderBrush = active ? Palette.Blue : Palette.Hairline; card.BorderThickness = new(active ? 1.5 : 1);
            Grid.SetColumn(card, i * 2); grid.Children.Add(card);
        }
        return grid;
    }

    private Control Look(bool compact)
    {
        var current = HasVisual(_progress!["current"]) ? _progress["current"] : _latestVisual ?? _progress["current"];
        if (current is null)
        {
            var empty = new StackPanel { Spacing = 10 };
            empty.Children.Add(Text("AI 正在看", 15, FontWeight.Bold));
            empty.Children.Add(Text("等待第一个文件完成分析", 12, brush: Palette.Secondary));
            var card = Card(empty); card.MinHeight = compact ? 100 : 296;
            return card;
        }
        var path = current.Text("path"); var video = current.Flag("isVideo");
        var frames = current["frames"].Items().ToArray();
        var selectedFrame = Math.Max(0, (frames.Length - 1) / 2);
        var preview = new Panel { MinHeight = compact ? 180 : 268, ClipToBounds = true };
        if (compact) preview.Height = 196;
        preview.Children.Add(Thumb(path, double.NaN, double.NaN, 640, video && frames.Length > 0 ? Num(frames[selectedFrame], "seconds") : null, radius: compact ? 0 : 10));
        var canvas = new Canvas(); preview.Children.Add(canvas);
        var spots = new[] { (.55, .18), (.08, .3), (.06, .52), (.5, .7) };
        var tags = current["tags"].Items().Take(4).ToArray();
        preview.SizeChanged += (_, e) =>
        {
            if (compact && e.WidthChanged) preview.Height = Math.Max(180, e.NewSize.Width * .54);
            canvas.Children.Clear();
            for (var i = 0; i < tags.Length; i++)
            {
                var chip = Chip(tags[i].Text("tag"), Num(tags[i], "score").ToString("0.00"), Brushes.White);
                chip.BoxShadow = BoxShadows.Parse("0 1 3 #14000000");
                chip.MaxWidth = Math.Max(0, e.NewSize.Width - 16);
                chip.Measure(new Size(chip.MaxWidth, double.PositiveInfinity));
                Canvas.SetLeft(chip, Math.Max(8, Math.Min(spots[i].Item1 * e.NewSize.Width, e.NewSize.Width - chip.DesiredSize.Width - 8)));
                Canvas.SetTop(chip, spots[i].Item2 * e.NewSize.Height); canvas.Children.Add(chip);
            }
            if (current.Text("scene") is { Length: > 0 } scene)
            {
                var chip = new Border { Background = DarkChip, CornerRadius = new(11), Padding = new(9, 3), Child = new TextBlock { FontSize = 11.5, Foreground = Brushes.White, Text = $"{L.T("场景")} · {L.T(scene)}  {Num(current, "sceneScore"):0.00}" } };
                Canvas.SetLeft(chip, 8); Canvas.SetBottom(chip, 8); canvas.Children.Add(chip);
            }
            if (video && !compact)
            {
                var duration = TimeSpan.FromSeconds(Num(current, "durationSeconds"));
                var chip = new Border { Background = Palette.Blue, CornerRadius = new(11), Padding = new(9, 3),
                    Child = Ui.Row(4, Ui.Icon("play", 11, Brushes.White), Text($"{L.T("视频")} {(int)duration.TotalMinutes:00}:{duration.Seconds:00}", 11.5, brush: Brushes.White, raw: true)) };
                Canvas.SetRight(chip, 8); Canvas.SetTop(chip, 8); canvas.Children.Add(chip);
            }
        };
        var info = new StackPanel { Spacing = 8 };
        var heading = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8, Margin = new(0, 0, 0, compact ? 0 : 4) };
        heading.Children.Add(Text("AI 正在看", 15, FontWeight.Bold));
        var detail = compact && frames.Length > 0 ? L.F("第 {0} / {1} 帧", selectedFrame + 1, frames.Length)
            : !ReferenceEquals(current, _progress["current"]) ? L.T("最近分析") : L.T(FocusStage().Text("name"));
        var name = Text(current.Text("name") + " · " + detail, 12, brush: Palette.Secondary, raw: true);
        ToolTip.SetTip(name, current.Text("name")); Grid.SetColumn(name, 1); heading.Children.Add(name); info.Children.Add(heading);
        var strip = new Grid { ColumnDefinitions = new() };
        for (var i = 0; i < frames.Length; i++)
        {
            strip.ColumnDefinitions.Add(new(1, GridUnitType.Star));
            var cell = new StackPanel { Spacing = 3, Margin = new(i == 0 ? 0 : 3, 0, 0, 0) };
            var frame = new Panel();
            frame.Children.Add(Thumb(path, double.NaN, compact ? 30 : 46, 120, video ? Num(frames[i], "seconds") : null, radius: compact ? 4 : 6));
            var timestamp = Num(frames[i], "seconds").ToString("0.0", CultureInfo.InvariantCulture) + "s";
            ToolTip.SetTip(frame, timestamp);
            if (!compact)
                frame.Children.Add(new Border { Background = DarkChip, CornerRadius = new(3), Padding = new(3, 0), Margin = new(2),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                    Child = Text(timestamp, 10, brush: Brushes.White, raw: true) });
            if (i == selectedFrame) frame.Children.Add(new Border { BorderBrush = Palette.Blue, BorderThickness = new(2), CornerRadius = new(7) });
            cell.Children.Add(frame);
            if (!compact) cell.Children.Add(new Border { Height = 5, CornerRadius = new(2), Background = frames[i].Text("categoryId").Length == 0
                ? Track : frames[i].Flag("consistent") ? Palette.Blue : Palette.Orange });
            Grid.SetColumn(cell, i); strip.Children.Add(cell);
        }
        if (frames.Length > 0)
        {
            if (!compact) info.Children.Add(Text(L.F("采样画面 · {0} 帧 · 当前第 {1} 帧", frames.Length, selectedFrame + 1), 11.5, brush: Palette.Secondary, raw: true));
            info.Children.Add(strip);
        }
        if (!compact)
        {
            if (video && frames.Any(frame => frame.Text("categoryId").Length > 0))
                info.Children.Add(Text(L.F("每帧归属一致率 {0:P0}", Num(current, "frameConsistency")) + (Num(current, "inconsistentFrame") > 0
                    ? " · " + L.F("第 {0} 帧服装不同", Num(current, "inconsistentFrame")) : ""), 11.5, brush: Palette.Secondary, raw: true));
            var colors = current["outfitColors"].Items().ToArray();
            if (colors.Length > 0)
            {
                var label = Text($"{L.T("服装主色")} · {L.T("人物占画面")} {Num(current, "personRatio"):P0}", 11.5, brush: Palette.Secondary, raw: true);
                label.Margin = new(0, 4, 0, 0); info.Children.Add(label);
                var bar = new Grid { Height = 14, ColumnDefinitions = new() };
                for (var i = 0; i < colors.Length; i++) { bar.ColumnDefinitions.Add(new(Math.Max(.001, Num(colors[i], "share")), GridUnitType.Star)); var b = new Border { Background = new SolidColorBrush(Color.Parse(colors[i].Text("hex"))) }; Grid.SetColumn(b, i); bar.Children.Add(b); }
                info.Children.Add(new Border { CornerRadius = new(4), ClipToBounds = true, BorderBrush = Palette.Hairline, BorderThickness = new(1), Child = bar });
            }
            info.Children.Add(Text("各规则判断", 11.5, brush: Palette.Secondary));
            var verdicts = new WrapPanel();
            foreach (var v in current["verdicts"].Items()) { var chip = Chip(v.Text("ruleName"), v.Text("value")); chip.Margin = new(0, 0, 6, 4); verdicts.Children.Add(chip); }
            info.Children.Add(verdicts);
        }
        var reason = new TextBlock { FontSize = 12, Foreground = Palette.Text, TextWrapping = TextWrapping.Wrap };
        reason.Inlines!.Add(new Avalonia.Controls.Documents.Run(L.T("归组理由")) { FontWeight = FontWeight.Bold });
        reason.Inlines.Add(new Avalonia.Controls.Documents.Run((compact ? " " : " · ") + L.T(current.Text("reason"))) { Foreground = compact ? Palette.Secondary : Palette.Text });
        info.Children.Add(compact ? reason : new Border { Background = new SolidColorBrush(Color.Parse("#F1F4FA")), CornerRadius = new(8), Padding = new(10, 10), Child = reason });
        if (compact)
        {
            var stack = new StackPanel(); stack.Children.Add(preview); stack.Children.Add(new Border { Padding = new(14), Child = info });
            return Ui.Card(stack);
        }
        var grid = new Grid { ColumnDefinitions = new("1.05*,2*"), ColumnSpacing = 14 };
        grid.Children.Add(preview); Grid.SetColumn(info, 1); grid.Children.Add(info);
        return Card(grid);
    }

    private Control Wall()
    {
        var wrap = new WrapPanel();
        foreach (var file in _files?["items"].Items() ?? [])
        {
            var state = State(file);
            var tile = new Panel { Width = 46, Height = 46, Margin = new(0, 0, 7, 7) };
            var thumb = Thumb(file.Text("path"), 46, 46, 92, file.Flag("isVideo") ? 1 : null); thumb.Opacity = state == "queued" ? .35 : 1; tile.Children.Add(thumb);
            if (state == "processing")
            {
                tile.Children.Add(new Border { BorderBrush = Palette.Blue, BorderThickness = new(2), CornerRadius = new(8), Margin = new(-2) });
                tile.Children.Add(new Border { Width = 20, Height = 20, CornerRadius = new(10), Background = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(0, 0, 4, 4),
                    Child = Ui.Icon("loader-circle", 18, Palette.Blue) });
            }
            (IBrush? fill, string glyph) badge = state switch { "done" => (Palette.Green, "check"), "pending" => (Palette.Orange, "?"), "failed" => (Palette.Red, "!"), _ => (null, "") };
            if (badge.fill is not null)
                tile.Children.Add(new Border { Width = 20, Height = 20, CornerRadius = new(10), Background = badge.fill,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(0, 0, 4, 4),
                    Child = badge.glyph == "check" ? Ui.Icon("check", 12, Brushes.White) : new TextBlock { Text = badge.glyph, FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
            ToolTip.SetTip(tile, file.Text("name")); wrap.Children.Add(tile);
        }
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Ui.Row(8, Text("文件流", 15, FontWeight.Bold), Text("最近 30 个", 11.5, brush: Palette.Secondary)));
        panel.Children.Add(wrap);
        var card = Card(panel); card.MinHeight = 204;
        return card;
    }

    private Control Groups()
    {
        var list = new StackPanel();
        list.Children.Add(Ui.Row(8, Text("分组正在形成", 15, FontWeight.Bold), Text(L.F("第 {0} 次归组", Num(_progress?["regroup"], "ordinal")), 11.5, brush: Palette.Secondary, raw: true)));
        var index = 0;
        foreach (var group in _progress!["groups"].Items().OrderByDescending(g => Num(g, "count")).Take(12))
        {
            index++;
            var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10, Margin = new(0, 8) };
            var cover = new Panel { Width = 64, Height = 64, VerticalAlignment = VerticalAlignment.Top };
            cover.Children.Add(Thumb(null, 64, 64, 128, mosaicQuery: "ruleId=" + Uri.EscapeDataString(group.Text("ruleId")) + "&categoryId=" + Uri.EscapeDataString(group.Text("categoryId")), radius: 9));
            if (Num(group, "added") > 0) cover.Children.Add(new Border { Background = Palette.Blue, CornerRadius = new(8), Padding = new(5, 1), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, -4, -6, 0),
                Child = new TextBlock { Text = "+" + Num(group, "added").ToString("0"), FontSize = 10.5, FontWeight = FontWeight.Bold, Foreground = Brushes.White } });
            row.Children.Add(cover);
            var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            var name = Text($"{index:00} {group.Text("name")}", 13, FontWeight.SemiBold, raw: true);
            ToolTip.SetTip(name, group.Text("name")); text.Children.Add(name);
            text.Children.Add(Text(group.Text("shootName"), 11.5, brush: Palette.Secondary, raw: true));
            var count = new TextBlock { FontSize = 11.5, Foreground = Palette.Secondary };
            count.Inlines!.Add(new Avalonia.Controls.Documents.Run(Num(group, "count").ToString("0")) { FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Palette.Text });
            count.Inlines.Add(new Avalonia.Controls.Documents.Run(" " + L.T("个文件"))); text.Children.Add(count);
            Grid.SetColumn(text, 1); row.Children.Add(text);
            if (index > 1) list.Children.Add(Ui.Hairline());
            list.Children.Add(row);
        }
        return Card(new ScrollViewer { Content = list });
    }

    private Control Run()
    {
        var grid = new Grid { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 12, Margin = new(18, 2, 18, 18) };
        grid.Children.Add(Header(true));
        var stages = Stages(); Grid.SetRow(stages, 1); grid.Children.Add(stages);
        var main = new Grid { ColumnDefinitions = new("3*,*"), ColumnSpacing = 12 };
        main.ColumnDefinitions[1].MinWidth = 280;
        var left = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 12 };
        left.Children.Add(Look(false));
        var wall = Wall(); Grid.SetRow(wall, 1); left.Children.Add(wall);
        main.Children.Add(new ScrollViewer { Content = left, VerticalContentAlignment = VerticalAlignment.Stretch });
        var groups = Groups(); Grid.SetColumn(groups, 1); main.Children.Add(groups);
        Grid.SetRow(main, 2); grid.Children.Add(main);
        return grid;
    }

    private Control Mobile()
    {
        var task = _progress!["task"]!;
        var stack = new StackPanel { Spacing = 10, Margin = new(16, 0, 16, 16) };
        var nav = new Grid { ColumnDefinitions = new("64,*,64"), MinHeight = 64 };
        var back = new Button { Content = Ui.Content(L.T("任务"), "chevron-left", Palette.Blue, 16), Classes = { "link" } }; back.Click += (_, _) => _back(); nav.Children.Add(back);
        var title = Text(task.Text("name"), 17, FontWeight.Bold, raw: true); title.TextAlignment = TextAlignment.Center;
        ToolTip.SetTip(title, task.Text("name")); Grid.SetColumn(title, 1); nav.Children.Add(title);
        stack.Children.Add(nav);
        var percent = new Grid { ColumnDefinitions = new("*,Auto") };
        percent.Children.Add(Text($"{Num(_progress, "percent"):0}%", 32, FontWeight.Bold, raw: true));
        var elapsed = Text($"{L.T("已用")} {Clock(Num(_progress, "elapsedSeconds"))}", 12, brush: Palette.Secondary, raw: true); Grid.SetColumn(elapsed, 1); percent.Children.Add(elapsed);
        var top = new StackPanel { Spacing = 8 }; top.Children.Add(percent);
        var counts = _progress["counts"];
        var grouped = _progress["groups"].Items().Sum(g => Num(g, "count"));
        top.Children.Add(Bar(grouped / Math.Max(1, Num(counts, "total")), Palette.Blue, 7, Math.Max(0, Num(counts, "completed") - grouped) / Math.Max(1, Num(counts, "total"))));
        top.Children.Add(Text($"{L.T("已归组")} {grouped:0} · {L.T("待确认")} {Num(counts, "pending"):0} · {_progress["rules"].Items().Count()} {L.T("组规则")}", 12, brush: Palette.Secondary, raw: true));
        stack.Children.Add(Card(top));
        stack.Children.Add(Look(true));
        var pipeline = new StackPanel(); pipeline.Children.Add(Text("流水线", 14, FontWeight.Bold));
        var focus = FocusStage();
        foreach (var stage in _progress["stages"].Items().Where(s => s.Flag("enabled")))
        {
            var row = new Grid { ColumnDefinitions = new("46,*,Auto,Auto"), ColumnSpacing = 10, MinHeight = 48 };
            row.Children.Add(MobileStageCovers(stage));
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(Text(stage.Text("name"), 14, FontWeight.SemiBold));
            names.Children.Add(Text(stage.Text("stage") is "Scan" or "0" ? Num(stage, "total").ToString("0") : stage.Text("model"), 11.5, brush: Palette.Secondary));
            Grid.SetColumn(names, 1); row.Children.Add(names);
            var value = Text(stage.Text("stage") is "Group" or "4" ? $"{Num(_progress?["regroup"], "ordinal"):0} {L.T("次")}" : Num(stage, "done").ToString("0"), 15, FontWeight.Bold, raw: true);
            Grid.SetColumn(value, 2); row.Children.Add(value);
            var done = Num(stage, "total") > 0 && Num(stage, "done") >= Num(stage, "total");
            var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center,
                Fill = done ? Palette.Green : ReferenceEquals(stage, focus) || Num(stage, "done") > 0 ? Palette.Blue : Palette.Tertiary };
            Grid.SetColumn(dot, 3); row.Children.Add(dot);
            if (pipeline.Children.Count > 1) pipeline.Children.Add(Ui.Hairline());
            pipeline.Children.Add(row);
        }
        stack.Children.Add(Card(pipeline));
        var paused = task.Text("state") == "Paused";
        var bottom = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 8, Margin = new(16, 10, 16, 16) };
        var pause = Pill(paused ? "继续" : "暂停", paused ? "play" : "pause", () => _control(_id, paused ? "resume" : "pause")); pause.Height = 48; pause.CornerRadius = new(24); pause.HorizontalAlignment = HorizontalAlignment.Stretch; pause.HorizontalContentAlignment = HorizontalAlignment.Center;
        var stop = Pill("停止", "square", () => _control(_id, "stop"), danger: true); stop.Height = 48; stop.CornerRadius = new(24); stop.HorizontalAlignment = HorizontalAlignment.Stretch; stop.HorizontalContentAlignment = HorizontalAlignment.Center; Grid.SetColumn(stop, 1);
        foreach (var button in new[] { pause, stop })
            if (button.Content is StackPanel content)
                foreach (var label in content.Children.OfType<TextBlock>()) label.FontSize = 16;
        bottom.Children.Add(pause); bottom.Children.Add(stop);
        var root = new Grid { RowDefinitions = new("*,Auto") };
        root.Children.Add(new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        root.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#FAFAFC")), BorderBrush = Palette.Hairline,
            BorderThickness = new(0, 1, 0, 0), Child = bottom, [Grid.RowProperty] = 1 });
        return root;
    }

    private Control MobileStageCovers(JsonNode stage)
    {
        var covers = new Panel { Width = 46, Height = 38, VerticalAlignment = VerticalAlignment.Center };
        var candidates = (_files?["items"].Items() ?? []).ToList();
        if (_latestVisual is not null) candidates.Add(_latestVisual);
        var samples = candidates.Where(file => stage.Text("stage") switch
        {
            "Tags" or "1" => file["tags"].Items().Any(),
            "Scene" or "2" => file.Text("scene").Length > 0,
            "Outfit" or "3" => file["outfitColors"].Items().Any(),
            "Group" or "4" => file.Text("groupId").Length > 0,
            _ => true
        }).DistinctBy(file => file.Text("path")).TakeLast(3).ToArray();
        for (var i = 0; i < samples.Length; i++)
        {
            var file = samples[i];
            var frame = file["frames"].Items().FirstOrDefault();
            covers.Children.Add(new Border { Background = Brushes.White, CornerRadius = new(9), Padding = new(1.5),
                BoxShadow = BoxShadows.Parse("0 1 3 #14000000"), HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, Margin = new(i * 8, (samples.Length - i - 1) * 3, 0, 0),
                Child = Thumb(file.Text("path"), 26, 26, 60, file.Flag("isVideo") ? frame is null ? 1 : Num(frame, "seconds") : null, radius: 7) });
        }
        return covers;
    }

    // ---------- result ----------
    private void RenderResult()
    {
        _renderKey = null;
        if (!_detached) Render();
    }

    private async Task ResultAction(Func<Task> action)
    {
        if (_resultBusy) return;
        _resultBusy = true; _resultError = null;
        try { await action(); }
        catch (Exception error) { _resultError = error.Message; _resultSheet = null; }
        finally { _resultBusy = false; RenderResult(); }
    }

    private Control Result()
    {
        var task = _progress!["task"]!; var counts = _progress["counts"];
        var rules = _progress["rules"].Items().ToArray();
        var primary = rules.FirstOrDefault(rule => rule.Flag("byOutfit")) ?? rules.FirstOrDefault();
        _resultRule ??= primary.Text("id");
        _destination ??= task.Text("output");
        var mobile = _mobile();
        var width = Bounds.Width > 0 ? Bounds.Width : 1280;
        var sidebarWidth = Math.Clamp((width - 48) * .27, 340, 440);
        var grid = new Grid { ColumnDefinitions = mobile ? new("*") : new($"*,{sidebarWidth.ToString(CultureInfo.InvariantCulture)}"),
            RowDefinitions = mobile ? new("Auto,Auto") : new("*"), ColumnSpacing = 12, RowSpacing = 12, Margin = new(mobile ? 16 : 18, 2, mobile ? 16 : 18, 18) };
        var left = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 20 };
        var head = new Grid { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("Auto,Auto"), RowSpacing = 12 };
        var back = new Button { Content = Ui.Icon("chevron-left", 16, Palette.Blue), Classes = { "link" }, Margin = new(0, 0, 6, 0) };
        ToolTip.SetTip(back, L.T("所有任务")); Avalonia.Automation.AutomationProperties.SetName(back, L.T("所有任务"));
        back.Click += (_, _) => _back(); head.Children.Add(back);
        var titles = new StackPanel { Spacing = 4 };
        titles.Children.Add(Text(task.Text("name") + " · " + L.T(task.Text("state") == "Completed" ? "已完成" : "已结束"), 17, FontWeight.Bold, raw: true));
        titles.Children.Add(Text(L.F("完成 {0} 个，待确认 {1} 个，失败 {2} 个 · 用时 {3}", Num(counts, "completed"), Num(counts, "pending"), Num(counts, "failed"), Clock(Num(_progress, "elapsedSeconds"))), 12, brush: Palette.Secondary, raw: true));
        Grid.SetColumn(titles, 1); head.Children.Add(titles);
        var segments = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var rule in rules)
        {
            var id = rule.Text("id");
            var button = new Button { Content = Text(rule.Text("name"), 13, id == _resultRule ? FontWeight.SemiBold : FontWeight.Normal), Classes = { "segmentLight" } };
            button.Classes.Set("active", id == _resultRule);
            button.Click += async (_, _) => await ResultAction(async () =>
            {
                if (id != primary.Text("id")) _ruleFiles = await Json(Base + $"files?offset=0&limit={RulePageSize}")
                    ?? throw new InvalidOperationException(L.T("无法读取分类结果。"));
                _resultRule = id;
            });
            segments.Children.Add(button);
        }
        var tabs = new Border { Background = Palette.Fill, CornerRadius = new(9), Padding = new(3),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Child = new ScrollViewer { Content = segments, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } };
        if (mobile || width < 1440) { Grid.SetRow(tabs, 1); Grid.SetColumnSpan(tabs, 3); }
        else Grid.SetColumn(tabs, 2);
        head.Children.Add(tabs); left.Children.Add(head);

        var beforeWidth = mobile ? 180 : Math.Clamp((width - sidebarWidth - 110) * .24, 160, 240);
        var body = new Grid { ColumnDefinitions = mobile ? new("*") : new($"{beforeWidth.ToString(CultureInfo.InvariantCulture)},Auto,*"),
            RowDefinitions = mobile ? new("Auto,Auto") : new("*"), ColumnSpacing = 18, RowSpacing = 20 };
        var before = new StackPanel { Spacing = 12, Width = beforeWidth, HorizontalAlignment = HorizontalAlignment.Left };
        before.Children.Add(Text("整理前", 15, FontWeight.Bold));
        var wrap = new WrapPanel();
        var random = new Random(3); var sourceSize = Math.Floor(beforeWidth / 5) - 4;
        foreach (var path in _summary?["sources"].Items().SelectMany(s => s["samplePaths"].Items().Select(p => p.ToString())).Take(30) ?? [])
        {
            var thumb = Thumb(path, sourceSize, sourceSize, (int)sourceSize * 2, VideoFormats(path) ? 1 : null, radius: 6);
            thumb.Margin = new(2); thumb.RenderTransform = new RotateTransform(random.Next(-8, 9)); wrap.Children.Add(thumb);
        }
        before.Children.Add(wrap);
        before.Children.Add(Text($"{Num(_summary, "images") + Num(_summary, "videos"):0} {L.T("个文件")} · {Ui.Size((long)Num(_summary, "bytes"))}", 12, brush: Palette.Secondary, raw: true));
        body.Children.Add(before);
        if (!mobile)
        {
            var arrow = Ui.Icon("arrow-right", 22, Palette.Blue); arrow.Margin = new(0, sourceSize * 4, 0, 0);
            arrow.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(arrow, 1); body.Children.Add(arrow);
        }
        var after = new StackPanel { Spacing = 16 };
        var primarySelected = _resultRule == primary.Text("id");
        var shoots = primarySelected ? _summary?["shoots"].Items().ToArray() ?? [] : RuleShoots();
        var groupCount = shoots.Sum(shoot => shoot["groups"].Items().Count());
        after.Children.Add(Ui.Row(8, Text("整理后", 15, FontWeight.Bold), Text(primarySelected && primary.Flag("byOutfit")
            ? L.F("{0} 个拍摄组 · {1} 个服装分组", shoots.Length, groupCount)
            : primarySelected ? L.F("{0} 个分组", groupCount) : L.F("本页 {0} 个文件 · {1} 个分组", _ruleFiles?["items"].Items().Count() ?? 0, groupCount), 12, brush: Palette.Secondary, raw: true)));
        // Keep two shoot columns when each has room for two large covers; stack them on narrower screens.
        var afterWidth = mobile ? width - 72 : width - sidebarWidth - beforeWidth - 134;
        var twoColumns = afterWidth >= 570;
        var columns = new Grid { ColumnDefinitions = twoColumns ? new("*,*") : new("*"), ColumnSpacing = 24 };
        var panels = new[] { new StackPanel { Spacing = 24 }, new StackPanel { Spacing = 24 } };
        columns.Children.Add(panels[0]);
        if (twoColumns) { Grid.SetColumn(panels[1], 1); columns.Children.Add(panels[1]); }
        var coverSize = Math.Clamp(((twoColumns ? (afterWidth - 24) / 2 : afterWidth) - 36) / 2, 112, 176);
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = 0;
        for (var s = 0; s < shoots.Length; s++)
        {
            var block = new StackPanel { Spacing = 8 };
            block.Children.Add(Ui.Row(7, Ui.Icon("folder-closed", 15, Palette.Blue), Text(shoots[s].Text("name"), 13, FontWeight.Bold, raw: true),
                Text(Num(shoots[s], "count").ToString("0"), 12, brush: Palette.Secondary, raw: true)));
            var groups = new WrapPanel { Margin = new(18, 0, 0, 0) };
            foreach (var group in shoots[s]["groups"].Items().OrderByDescending(g => Num(g, "count")))
            {
                index++; numbers[group.Text("ruleId") + "/" + group.Text("categoryId")] = index;
                var card = new StackPanel { Width = coverSize, Margin = new(0, 0, 12, 8), Spacing = 4 };
                card.Children.Add(ResultCover(group, coverSize));
                var name = new TextBlock { Text = $"{index:00} {group.Text("name")}", FontSize = 13, Foreground = Palette.Text,
                    TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, Margin = new(0, 2, 0, 0), Classes = { "raw" } };
                ToolTip.SetTip(name, group.Text("name")); card.Children.Add(name);
                card.Children.Add(Text(L.F("{0} 个文件", Num(group, "count")), 12, brush: Palette.Secondary, raw: true));
                groups.Children.Add(card);
            }
            block.Children.Add(groups); panels[twoColumns ? s % 2 : 0].Children.Add(block);
        }
        after.Children.Add(columns);
        if (groupCount == 0) after.Children.Add(Text("没有分类结果", 12, brush: Palette.Secondary));
        if (!primarySelected) after.Children.Add(ResultPager(_ruleFiles, RulePageSize, async offset =>
            _ruleFiles = await Json(Base + $"files?offset={offset}&limit={RulePageSize}") ?? throw new InvalidOperationException(L.T("无法读取分类结果。"))));
        if (mobile) { Grid.SetRow(after, 1); body.Children.Add(after); }
        else body.Children.Add(new ScrollViewer { Content = after, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, [Grid.ColumnProperty] = 2 });
        Grid.SetRow(body, 1); left.Children.Add(body); grid.Children.Add(Card(left, 18));

        var right = new Grid { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 12 };
        right.Children.Add(ResultHistogram());
        var organize = ResultOrganization(); Grid.SetRow(organize, 1); right.Children.Add(organize);
        var pending = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 8 };
        pending.Children.Add(Ui.Row(8, Text("待确认", 15, FontWeight.Bold), Text(L.F("{0} 个", Num(_files, "total")), 12, brush: Palette.Secondary, raw: true)));
        var pendingRows = new StackPanel();
        foreach (var file in _files?["items"].Items() ?? [])
        {
            pendingRows.Children.Add(Ui.Hairline());
            var row = new Grid { ColumnDefinitions = new("60,*"), ColumnSpacing = 10, Margin = new(0, 10) };
            var thumb = Thumb(file.Text("path"), 60, 60, 120, file.Flag("isVideo") ? 1 : null, radius: 9);
            thumb.VerticalAlignment = VerticalAlignment.Top; row.Children.Add(thumb);
            var text = new StackPanel { Spacing = 4 };
            var name = Text(file.Text("name"), 13, FontWeight.SemiBold, raw: true); ToolTip.SetTip(name, file.Text("name")); text.Children.Add(name);
            var reasonText = string.Join(" · ", file.Text("reason").Split(" · ").Select(L.T));
            var reason = Text(reasonText, 11.5, brush: Palette.Secondary, raw: true); ToolTip.SetTip(reason, reasonText); text.Children.Add(reason);
            var choices = new Grid { ColumnSpacing = 4 };
            foreach (var option in file["options"].Items().Take(2))
            {
                var shortName = option.Text("name").Split('·', StringSplitOptions.TrimEntries)[0];
                var label = numbers.TryGetValue(option.Text("ruleId") + "/" + option.Text("categoryId"), out var number) ? $"{number:00} {shortName}" : shortName;
                var choice = ResultChoice(label, () => ResultAction(() => Resolve(file.Text("path"), option.Text("ruleId"), option.Text("categoryId"))), raw: true);
                ToolTip.SetTip(choice, option.Text("name")); Grid.SetColumn(choice, choices.ColumnDefinitions.Count);
                choices.ColumnDefinitions.Add(new(1, GridUnitType.Star)); choices.Children.Add(choice);
            }
            var keep = new Border { Background = Palette.Fill, CornerRadius = new(12), Padding = new(7, 3), Child = Text("保持待确认", 11.5, brush: Palette.Secondary) };
            Grid.SetColumn(keep, choices.ColumnDefinitions.Count); choices.ColumnDefinitions.Add(new(GridLength.Auto)); choices.Children.Add(keep);
            text.Children.Add(choices); Grid.SetColumn(text, 1); row.Children.Add(text); pendingRows.Children.Add(row);
        }
        if (!(_files?["items"].Items().Any() ?? false)) pendingRows.Children.Add(Text("没有需要确认的文件", 12, brush: Palette.Secondary));
        pending.Children.Add(new ScrollViewer { Content = pendingRows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, [Grid.RowProperty] = 1 });
        var pager = ResultPager(_files, ResultPageSize, async offset => { _pendingOffset = offset; await Refresh(); });
        Grid.SetRow(pager, 2); pending.Children.Add(pager);
        var pendingCard = Card(pending); Grid.SetRow(pendingCard, 2); right.Children.Add(pendingCard);
        if (mobile) { pending.MaxHeight = 520; Grid.SetRow(right, 1); }
        else Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        Control content = mobile ? new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } : grid;
        if (_resultSheet is null) return content;
        var overlay = new Panel(); overlay.Children.Add(content);
        overlay.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#40000000")) });
        // The sheet owns its controls while periodic result updates replace the background.
        if (_resultSheet.Parent is Decorator parent) parent.Child = null;
        var sheet = Card(_resultSheet, 18); sheet.Width = Math.Min(720, Math.Max(280, width - 48));
        sheet.MaxHeight = Math.Max(240, Bounds.Height - 48); sheet.HorizontalAlignment = HorizontalAlignment.Center;
        sheet.VerticalAlignment = VerticalAlignment.Center; overlay.Children.Add(sheet); return overlay;
    }

    private JsonNode[] RuleShoots()
    {
        // The summary endpoint covers the primary rule. Other rules use one bounded file page.
        var groups = (_ruleFiles?["items"].Items() ?? []).Select(file => (File: file,
                Verdict: file["verdicts"].Items().FirstOrDefault(v => v.Text("ruleId") == _resultRule)))
            .Where(item => item.Verdict.Text("categoryId").Length > 0)
            .GroupBy(item => item.Verdict.Text("categoryId"));
        var results = new JsonArray();
        foreach (var group in groups)
            results.Add((JsonNode)new JsonObject { ["ruleId"] = _resultRule, ["categoryId"] = group.Key, ["name"] = group.First().Verdict.Text("value"),
                ["count"] = group.Count(), ["coverPaths"] = new JsonArray(group.Take(4).Select(item => (JsonNode?)JsonValue.Create(item.File.Text("path"))).ToArray()) });
        return results.Count == 0 ? [] : [new JsonObject { ["name"] = _progress?["rules"].Items().FirstOrDefault(rule => rule.Text("id") == _resultRule).Text("name"),
            ["count"] = results.Items().Sum(group => Num(group, "count")), ["groups"] = results }];
    }

    private Control ResultCover(JsonNode group, double size)
    {
        var paths = group["coverPaths"].Items().Select(path => path.ToString()).Take(4).ToArray();
        var mosaic = new Grid { Width = size, Height = size * 1.12, ColumnDefinitions = new("*,*"), RowDefinitions = new("*,*"), ColumnSpacing = 2, RowSpacing = 2 };
        for (var i = 0; i < paths.Length; i++)
        {
            // Three representatives fill the 2 × 2 grid with a tall lead image, as in the demo.
            var lead = (paths.Length is 1 or 3) && i == 0;
            var thumb = Thumb(paths[i], paths.Length == 1 ? size : (size - 2) / 2,
                lead || paths.Length == 2 ? size * 1.12 : (size * 1.12 - 2) / 2, (int)size, VideoFormats(paths[i]) ? 1 : null, radius: 0);
            Grid.SetColumn(thumb, paths.Length == 3 ? i == 0 ? 0 : 1 : i % 2);
            Grid.SetRow(thumb, paths.Length == 3 ? Math.Max(0, i - 1) : i / 2);
            if (lead || paths.Length == 2) Grid.SetRowSpan(thumb, 2);
            if (paths.Length == 1) Grid.SetColumnSpan(thumb, 2);
            mosaic.Children.Add(thumb);
        }
        return new Border { Background = Palette.Fill, CornerRadius = new(9), ClipToBounds = true, Child = mosaic };
    }

    private static Button ResultChoice(string label, Func<Task> action, bool raw = false)
    {
        var button = Pill(label, null, action);
        button.Content = Text(label, 11.5, raw: raw); button.Padding = new(7, 3); button.MinHeight = 0;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        return button;
    }

    private Control ResultPager(JsonNode? page, int size, Func<int, Task> load)
    {
        var offset = (int)Num(page, "offset"); var total = (int)Num(page, "total");
        var row = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8, IsVisible = total > size };
        var previous = ResultChoice("上一页", () => ResultAction(() => load(Math.Max(0, offset - size)))); previous.IsEnabled = offset > 0;
        row.Children.Add(previous);
        var label = Text(L.F("第 {0} / {1} 页", offset / size + 1, Math.Max(1, (total + size - 1) / size)), 11.5, brush: Palette.Secondary, raw: true);
        label.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(label, 1); row.Children.Add(label);
        var next = ResultChoice("下一页", () => ResultAction(() => load(offset + size))); next.IsEnabled = page?["nextOffset"] is not null;
        Grid.SetColumn(next, 2); row.Children.Add(next); return row;
    }

    private Control ResultHistogram()
    {
        var hist = new StackPanel { Spacing = 8 };
        hist.Children.Add(Ui.Row(8, Text("归组把握", 15, FontWeight.Bold), Text("相似度分布", 12, brush: Palette.Secondary)));
        var bars = new Grid { Height = 80, ColumnDefinitions = new() };
        var bins = _histogram?["bins"].Items().Select(b => b.GetValue<int>()).ToArray() ?? [];
        var max = Math.Max(1, bins.DefaultIfEmpty(0).Max()); var min = Num(_histogram, "min"); var hmax = Num(_histogram, "max"); var threshold = Num(_histogram, "threshold");
        for (var i = 0; i < bins.Length; i++)
        {
            bars.ColumnDefinitions.Add(new(1, GridUnitType.Star));
            var low = min + (hmax - min) * i / bins.Length;
            var bar = new Border { Background = low < threshold ? Palette.Orange : Palette.Blue, CornerRadius = new(3, 3, 0, 0), Margin = new(1.5, 0),
                VerticalAlignment = VerticalAlignment.Bottom, Height = Math.Max(1, 78.0 * bins[i] / max) };
            ToolTip.SetTip(bar, $"{low:0.00} · {bins[i]}"); Grid.SetColumn(bar, i); bars.Children.Add(bar);
        }
        hist.Children.Add(bars);
        var axis = new Grid { ColumnDefinitions = new("*,Auto,*") };
        axis.Children.Add(Text(min.ToString("0.00"), 11.5, brush: Palette.Secondary, raw: true));
        var middle = Text(L.F("阈值 {0:0.00}", threshold), 11.5, brush: Palette.Secondary, raw: true); Grid.SetColumn(middle, 1); axis.Children.Add(middle);
        var end = Text(hmax.ToString("0.00"), 11.5, brush: Palette.Secondary, raw: true); end.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(end, 2); axis.Children.Add(end);
        hist.Children.Add(axis);
        Control Legend(string label, IBrush color) => Ui.Row(5, new Border { Width = 7, Height = 7, CornerRadius = new(3.5), Background = color }, Text(label, 11.5, brush: Palette.Secondary));
        hist.Children.Add(Ui.Row(14, Legend("低于阈值", Palette.Orange), Legend("达到阈值", Palette.Blue)));
        return Card(hist);
    }

    private Control ResultOrganization()
    {
        var organize = new StackPanel { Spacing = 10 };
        organize.Children.Add(Ui.Row(8, Text("分类整理", 15, FontWeight.Bold), Text("远程仅复制", 12, brush: Palette.Secondary)));
        var destination = Text(_destination ?? "", 13, raw: true); ToolTip.SetTip(destination, _destination);
        organize.Children.Add(new Border { Background = Palette.Fill, CornerRadius = new(7), Padding = new(10, 9), Child = destination });
        var actions = new Grid { ColumnDefinitions = new("1.2*,*"), ColumnSpacing = 6 };
        var select = ResultChoice("选择分类目标文件夹…", () => ResultAction(() => BrowseDestination(_destination, 0)));
        var preview = ResultChoice("预览分类文件夹", () => ResultAction(PreviewOrganization));
        select.Padding = preview.Padding = new(8, 7); actions.Children.Add(select); Grid.SetColumn(preview, 1); actions.Children.Add(preview);
        organize.Children.Add(actions);
        organize.Children.Add(Text(L.F("将复制 {0} 个文件，保留源文件。", _organization is null
            ? Num(_progress?["counts"], "completed") : _organization["plan"].Items().Count()), 12, brush: Palette.Secondary, raw: true));
        var copy = Pill("按此预览复制文件", null, () => ResultAction(CopyOrganization), primary: true);
        copy.Classes.Add("primary"); copy.HorizontalAlignment = HorizontalAlignment.Left;
        copy.IsEnabled = !_resultBusy && (_organization is null || _organization["plan"].Items().Any()); organize.Children.Add(copy);
        if (_resultError is not null)
            organize.Children.Add(new TextBlock { Text = L.T(_resultError), Foreground = Palette.Red, FontSize = 12, TextWrapping = TextWrapping.Wrap, Classes = { "raw" } });
        return Card(organize);
    }

    private Grid ResultSheet(string title, Control content, Control? action = null)
    {
        var sheet = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 12 };
        var header = new Grid { ColumnDefinitions = new("*,Auto") }; header.Children.Add(Text(title, 15, FontWeight.Bold));
        var close = Pill("关闭", null, () => { _resultSheet = null; RenderResult(); return Task.CompletedTask; });
        Grid.SetColumn(close, 1); header.Children.Add(close); sheet.Children.Add(header);
        Grid.SetRow(content, 1); sheet.Children.Add(content);
        if (action is not null) { Grid.SetRow(action, 2); sheet.Children.Add(action); }
        return sheet;
    }

    private async Task BrowseDestination(string? folder, int offset)
    {
        JsonNode page;
        try { page = await RemoteApi.Call("browse_directory", new() { ["folder"] = folder, ["foldersOnly"] = true, ["offset"] = offset, ["limit"] = 40 }); }
        catch when (_resultSheet is null)
        {
            var locations = await RemoteApi.Call("file_locations");
            page = await RemoteApi.Call("browse_directory", new() { ["folder"] = locations.Text("defaultFolder"), ["foldersOnly"] = true, ["limit"] = 40 });
        }
        var path = page.Text("path");
        var rows = new StackPanel { Spacing = 8 };
        var address = Text(path, 12, raw: true); ToolTip.SetTip(address, path); rows.Children.Add(address);
        if (page.Text("parent").Length > 0)
            rows.Children.Add(Pill("上一级", "chevron-left", () => ResultAction(() => BrowseDestination(page.Text("parent"), 0))));
        foreach (var entry in page["entries"].Items())
        {
            var open = Pill(entry.Text("name"), "folder-closed", () => ResultAction(() => BrowseDestination(entry.Text("path"), 0)));
            open.Content = Ui.Row(8, Ui.Icon("folder-closed", 14, Palette.Blue), Text(entry.Text("name"), 13, raw: true));
            open.HorizontalContentAlignment = HorizontalAlignment.Left; rows.Children.Add(open);
        }
        rows.Children.Add(ResultPager(page, 40, next => BrowseDestination(path, next)));
        var select = Pill("选择此文件夹", null, () =>
        {
            _destination = path; _organization = null; _organizationSubmission = null; _resultSheet = null;
            RenderResult(); return Task.CompletedTask;
        });
        select.HorizontalAlignment = HorizontalAlignment.Right;
        _resultSheet = ResultSheet("分类目标目录", new ScrollViewer { Content = rows, MaxHeight = 400,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, select);
    }

    private async Task PreviewOrganization()
    {
        _organization = await RemoteApi.Call("preview_organization", new() { ["taskId"] = _id, ["outputFolder"] = _destination });
        _organizationSubmission = null;
        var plan = _organization["plan"].Items().ToArray();
        var rows = new StackPanel { Spacing = 8 };
        rows.Children.Add(Text(L.F("将复制 {0} 个文件，保留源文件。", plan.Length), 12, brush: Palette.Secondary, raw: true));
        var offset = 0;
        var files = new StackPanel { Spacing = 8 }; rows.Children.Add(files);
        var navigation = new Grid { ColumnDefinitions = new("Auto,*,Auto"), IsVisible = plan.Length > ResultPageSize };
        var previous = new Button { Content = L.T("上一页") };
        var next = new Button { Content = L.T("下一页") };
        var pageLabel = Text("", 12, brush: Palette.Secondary, raw: true); pageLabel.HorizontalAlignment = HorizontalAlignment.Center;
        previous.Click += (_, _) => { offset -= ResultPageSize; ShowPage(); };
        next.Click += (_, _) => { offset += ResultPageSize; ShowPage(); };
        navigation.Children.Add(previous); Grid.SetColumn(pageLabel, 1); navigation.Children.Add(pageLabel); Grid.SetColumn(next, 2); navigation.Children.Add(next);
        rows.Children.Add(navigation);
        void ShowPage()
        {
            files.Children.Clear();
            foreach (var file in plan.Skip(offset).Take(ResultPageSize))
            {
                var label = Text(file.Text("target"), 12, raw: true); ToolTip.SetTip(label, file.Text("target")); files.Children.Add(label);
            }
            previous.IsEnabled = offset > 0; next.IsEnabled = offset + ResultPageSize < plan.Length;
            pageLabel.Text = L.F("第 {0} / {1} 页", offset / ResultPageSize + 1, Math.Max(1, (plan.Length + ResultPageSize - 1) / ResultPageSize));
        }
        ShowPage();
        _resultSheet = ResultSheet("预览分类文件夹", new ScrollViewer { Content = rows, MaxHeight = 400,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
    }

    private async Task CopyOrganization()
    {
        if (_organization is null) { await PreviewOrganization(); return; }
        var saved = BrowserHost.ReadPending();
        if (saved.Length > 0 && _organizationSubmission is null)
        {
            throw new InvalidOperationException(L.T("请先恢复或清除上次提交记录。"));
        }
        _organizationSubmission ??= new JsonObject { ["requestId"] = Guid.NewGuid().ToString(), ["preview"] = _organization.DeepClone(), ["startImmediately"] = true };
        var draft = new JsonObject { ["method"] = "apply_organization", ["arguments"] = _organizationSubmission.DeepClone() };
        BrowserHost.WritePending(draft.ToJsonString());
        await RemoteApi.Call("apply_organization", _organizationSubmission);
        BrowserHost.WritePending(""); _organizationSubmission = null; _organization = null; _back();
    }

    private static bool VideoFormats(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" or ".m4v";

    private async Task Resolve(string path, string ruleId, string categoryId)
    {
        var body = new JsonObject { ["path"] = path, ["ruleId"] = ruleId, ["categoryId"] = categoryId };
        using var response = await Client.PostAsync(Base + "resolve", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        _organization = null; _organizationSubmission = null; _ruleFiles = null;
        _resultRule = null; _renderKey = null; _summary = null; await Refresh();
    }
}
