using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AvaMedia.Browser;

public sealed partial class ConsoleView
{
    /// <summary>One watermark region; times are absolute source seconds (End 0 = until the end).</summary>
    private sealed class DelogoDraft
    {
        public double X, Y, Width, Height, Start, End;
        public Rect Rect => new(X, Y, Width, Height);
    }
    private readonly List<DelogoDraft> _delogoRegions = [];
    private int _delogoSelected;
    private readonly StackPanel _delogoSide = new() { Spacing = 10 };
    private readonly StackPanel _delogoRows = new() { Spacing = 2 };
    private readonly VisualNumber _delogoStart = new() { Minimum = 0, Maximum = 1000000, Increment = .1m, Formatter = value => ClipTimeline.Long((double)value) };
    private readonly VisualNumber _delogoEnd = new() { Minimum = 0, Maximum = 1000000, Increment = .1m, Formatter = value => value == 0 ? L.T("至结尾") : ClipTimeline.Long((double)value) };
    private bool _delogoBuilt, _syncDelogo;

    private void BuildDelogoSide()
    {
        if (_delogoBuilt) return; _delogoBuilt = true;
        var header = new Grid { ColumnDefinitions = new("*,Auto") };
        header.Children.Add(Ui.Text("水印区域", "section"));
        var tools = Ui.Row(2, IconButton("plus", "添加区域", AddDelogoRegion), IconButton("trash-2", "删除区域", RemoveDelogoRegion));
        Grid.SetColumn(tools, 1); header.Children.Add(tools);
        _delogoSide.Children.Add(header);
        _delogoSide.Children.Add(new Border { Background = Palette.Fill, CornerRadius = new(10), Padding = new(4), Child = _delogoRows });
        _delogoSide.Children.Add(Ui.Text("出现时间", "section"));
        _delogoSide.Children.Add(Ui.Line("开始", _delogoStart));
        _delogoSide.Children.Add(Ui.Line("结束", _delogoEnd));
        _delogoSide.Children.Add(Actions(
            Action("开始设为当前", () => { _delogoStart.Value = _seconds.Value; return Task.CompletedTask; }, "arrow-down-to-line"),
            Action("结束设为当前", () => { _delogoEnd.Value = _seconds.Value; return Task.CompletedTask; }, "arrow-down-to-line"),
            Action("全程", () => { _delogoStart.Value = 0; _delogoEnd.Value = 0; return Task.CompletedTask; })));
        var hint = Ui.Text("结束为 0 表示持续到视频结尾。时间以原视频为准。", "caption"); hint.TextWrapping = TextWrapping.Wrap;
        _delogoSide.Children.Add(hint);
        _delogoStart.ValueChanged += (_, _) => { if (!_syncDelogo) { SyncDelogo(); RenderDelogoRegions(); UpdateDelogoTimeline(); } };
        _delogoEnd.ValueChanged += (_, _) => { if (!_syncDelogo) { SyncDelogo(); RenderDelogoRegions(); UpdateDelogoTimeline(); } };
    }
    private DelogoDraft ActiveDelogo()
    {
        if (_delogoRegions.Count == 0) _delogoRegions.Add(new());
        _delogoSelected = Math.Clamp(_delogoSelected, 0, _delogoRegions.Count - 1);
        return _delogoRegions[_delogoSelected];
    }
    /// <summary>Copies the form fields (the active region) and the time range into the draft list.</summary>
    private void SyncDelogo()
    {
        if (_activeFeature != "delogo" || !_optionControls.ContainsKey("delogoWidth")) return;
        var region = ActiveDelogo();
        region.X = GetNumber("delogoX"); region.Y = GetNumber("delogoY"); region.Width = GetNumber("delogoWidth"); region.Height = GetNumber("delogoHeight");
        region.Start = (double)(_delogoStart.Value ?? 0); region.End = (double)(_delogoEnd.Value ?? 0);
    }
    private void SelectDelogo(int index)
    {
        SyncDelogo(); _delogoSelected = index; var region = ActiveDelogo();
        _syncDelogo = true;
        try
        {
            SetNumber("delogoX", region.X); SetNumber("delogoY", region.Y); SetNumber("delogoWidth", region.Width); SetNumber("delogoHeight", region.Height);
            _delogoStart.Value = (decimal)region.Start; _delogoEnd.Value = (decimal)region.End;
        }
        finally { _syncDelogo = false; }
        RenderDelogoRegions(); UpdateRegion();
    }
    private Task AddDelogoRegion()
    {
        SyncDelogo();
        if (_delogoRegions.Count >= 16) throw new InvalidOperationException("水印区域最多 16 个。");
        var width = _cropOverlay.SourceWidth; var height = _cropOverlay.SourceHeight;
        var offset = _delogoRegions.Count * 24 % Math.Max(2, height / 3);
        _delogoRegions.Add(new() { X = (width / 20 + offset) / 2 * 2, Y = (height / 20 + offset) / 2 * 2, Width = width / 6 / 2 * 2, Height = height / 10 / 2 * 2 });
        SelectDelogo(_delogoRegions.Count - 1); return Task.CompletedTask;
    }
    private Task RemoveDelogoRegion()
    {
        SyncDelogo();
        if (_delogoRegions.Count <= 1) { SetRegion(new Rect()); return Task.CompletedTask; }
        _delogoRegions.RemoveAt(_delogoSelected); _delogoSelected = Math.Max(0, _delogoSelected - 1);
        // Reload the fields from the newly selected region without saving the removed one back.
        var keep = _delogoSelected; _delogoSelected = keep; var region = ActiveDelogo();
        _syncDelogo = true;
        try
        {
            SetNumber("delogoX", region.X); SetNumber("delogoY", region.Y); SetNumber("delogoWidth", region.Width); SetNumber("delogoHeight", region.Height);
            _delogoStart.Value = (decimal)region.Start; _delogoEnd.Value = (decimal)region.End;
        }
        finally { _syncDelogo = false; }
        RenderDelogoRegions(); UpdateRegion(); return Task.CompletedTask;
    }
    private void RenderDelogoRegions()
    {
        BuildDelogoSide();
        if (_activeFeature != "delogo") return;
        if (!_syncDelogo) SyncDelogo();
        _delogoRows.Children.Clear();
        var duration = _previewInfo?["duration"]?.GetValue<double>() ?? 0;
        for (var index = 0; index < Math.Max(1, _delogoRegions.Count); index++)
        {
            var region = _delogoRegions.Count > index ? _delogoRegions[index] : new DelogoDraft(); var captured = index;
            var row = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto"), ColumnSpacing = 8 };
            row.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new(3), Background = index == _delogoSelected ? Palette.Blue : Palette.Orange, VerticalAlignment = VerticalAlignment.Center });
            var name = Ui.Text(L.F("区域 {0}", index + 1), "strong"); Grid.SetColumn(name, 1); row.Children.Add(name);
            var size = Ui.Text(region.Width > 0 ? $"{region.Width:0}×{region.Height:0} @ {region.X:0},{region.Y:0}" : L.T("未框选"), "caption", raw: true); Grid.SetColumn(size, 2); row.Children.Add(size);
            var time = Ui.Text(region.Start <= 0 && region.End <= 0 ? L.T("全程") : $"{ClipTimeline.Short(region.Start)}–{(region.End > 0 ? ClipTimeline.Short(region.End) : ClipTimeline.Short(duration))}", "caption", raw: true);
            Grid.SetColumn(time, 3); row.Children.Add(time);
            var button = new Button { Content = row, Classes = { "row" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(8, 6) };
            if (index == _delogoSelected) button.Classes.Add("selected");
            button.Click += (_, _) => { if (captured != _delogoSelected) SelectDelogo(captured); };
            _delogoRows.Children.Add(button);
        }
        _delogoStart.Maximum = _delogoEnd.Maximum = (decimal)Math.Max(.01, duration > 0 ? duration : 1000000);
    }
    private void UpdateDelogoOverlay()
    {
        if (!_syncDelogo) SyncDelogo();
        _cropOverlay.Label = _delogoRegions.Count > 1 ? L.F("区域 {0}", _delogoSelected + 1) : null;
        _cropOverlay.Others = _delogoRegions.Select((region, index) => (region, index)).Where(pair => pair.index != _delogoSelected && pair.region.Width > 0)
            .Select(pair => (pair.region.Rect, L.F("区域 {0}", pair.index + 1))).ToArray();
        if (_delogoBuilt) RenderDelogoRegions();
    }
    private void UpdateDelogoTimeline()
    {
        var duration = _previewInfo?["duration"]?.GetValue<double>() ?? 0;
        if (duration <= 0) { _timeline.SetSegments([], 0); return; }
        _timeline.RemoveMode = false;
        _timeline.SetSegments(_delogoRegions.Count == 0 ? [new(0, duration)]
            : _delogoRegions.Select(region => new ClipTimeline.Segment(region.Start, region.End > 0 ? region.End : duration)).ToArray(), _delogoSelected);
    }
    /// <summary>Region 0 stays in the legacy fields when it has no time range; all others go to delogoRegions.</summary>
    private void ApplyDelogoRegions(JsonObject options)
    {
        SyncDelogo();
        var regions = _delogoRegions.Count == 0
            ? [new DelogoDraft { X = GetNumber("delogoX"), Y = GetNumber("delogoY"), Width = GetNumber("delogoWidth"), Height = GetNumber("delogoHeight") }]
            : _delogoRegions.ToList();
        if (regions.Any(region => region.Width <= 0 || region.Height <= 0)) throw new ArgumentException("请先在预览中框选区域，或拖动区域滑块。");
        var offset = ReadOption(options, "start")?.GetValue<double>() ?? 0;
        JsonObject Relative(DelogoDraft region) => new()
        {
            ["x"] = (int)region.X, ["y"] = (int)region.Y, ["width"] = (int)region.Width, ["height"] = (int)region.Height,
            ["start"] = Math.Max(0, region.Start - offset), ["end"] = region.End > 0 ? Math.Max(.001, region.End - offset) : 0
        };
        var first = regions[0]; var legacy = first.Start <= 0 && first.End <= 0;
        options["delogoX"] = legacy ? (int)first.X : 0; options["delogoY"] = legacy ? (int)first.Y : 0;
        options["delogoWidth"] = legacy ? (int)first.Width : 0; options["delogoHeight"] = legacy ? (int)first.Height : 0;
        var extra = regions.Skip(legacy ? 1 : 0).Select(Relative).ToArray();
        if (extra.Length > 0) options["delogoRegions"] = new JsonArray(extra); else options.Remove("delogoRegions");
    }
}
