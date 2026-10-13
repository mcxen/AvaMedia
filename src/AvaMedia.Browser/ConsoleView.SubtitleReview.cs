using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaMedia.FileUi;
using AvaMedia.MediaUi;

namespace AvaMedia.Browser;

public sealed partial class ConsoleView
{
    private SubtitleReviewPanel? _subtitleReview;
    private TabItem? _subtitleReviewTab;
    private async Task OpenSubtitleReview(string id)
    {
        var data = await RemoteApi.Call("review_subtitles", new() { ["taskId"] = id });
        if (_subtitleReviewTab is not null) _tabs.Items.Remove(_subtitleReviewTab);
        _subtitleReview?.Dispose();
        _subtitleReview = new(data, _output.Text ?? "", async panel =>
        {
            _tabs.SelectedIndex = 0;
            await PickServerFile("字幕输出目录", true, selected => { panel.SetFolder(selected); _tabs.SelectedItem = _subtitleReviewTab; });
        }, async args =>
        {
            if (_pending is not null) throw new InvalidOperationException("请先恢复或清除上次提交记录。");
            var draft = new JsonObject { ["method"] = "export_subtitles", ["arguments"] = args };
            BrowserHost.WritePending(draft.ToJsonString()); _pending = draft; UpdatePending(); await Submit();
        });
        _subtitleReviewTab = new TabItem { Header = "字幕校对 · " + PathInput.ShortName(data.Text("path")), Content = _subtitleReview };
        _tabs.Items.Add(_subtitleReviewTab); _tabs.SelectedItem = _subtitleReviewTab;
        UpdatePreviewActivity();
    }
}

internal sealed class SubtitleReviewPanel : UserControl, IDisposable
{
    private sealed record Cue(double Start, double End, string Text)
    { public string Summary => $"{Start:0.###} – {End:0.###}   {Text.ReplaceLineEndings(" ")}"; }
    private readonly JsonNode _source;
    private readonly ObservableCollection<Cue> _cues = [];
    private readonly ListBox _list = new() { MinHeight = 150, Height = 235 };
    private readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 55, MaxHeight = 110 };
    private readonly RangeBar _range = new() { Height = 32, Step = .001, TrackBrush = Brushes.LightGray, BorderBrush = Brushes.Gray,
        SelectionBrush = Brushes.SteelBlue, HandleBrush = Brushes.WhiteSmoke, GripBrush = Brushes.SteelBlue };
    private readonly TextBlock _timing = new(), _notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Image _image = new() { Height = 270, Stretch = Stretch.Uniform };
    private readonly ComboBox _format = new() { ItemsSource = new[] { "SRT", "ASS", "原格式视频", "MP4", "MKV" }, SelectedIndex = 0 };
    private readonly ComboBox _color = new() { ItemsSource = new[] { "白色", "黄色", "黑色", "青色" }, SelectedIndex = 0 };
    private readonly ComboBox _alignment = new() { ItemsSource = new[] { "左下", "底部居中", "右下", "左中", "画面中心", "右中", "左上", "顶部居中", "右上" }, SelectedIndex = 1 };
    private readonly ComboBox _font = new();
    private readonly VisualNumber _size = new() { Minimum = 0, Maximum = 200, Increment = 1, Value = 48 },
        _margin = new() { Minimum = 0, Maximum = 300, Increment = 1, Value = 36 };
    private readonly FilePathChoice _folder = new();
    private readonly CheckBox _partial = new() { Content = "确认只导出本次部分识别结果" }, _start = new() { Content = "立即执行", IsChecked = true };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly SemaphoreSlim _flight = new(1, 1);
    private CancellationTokenSource? _cancel;
    private Bitmap? _bitmap;
    private int _generation;
    private bool _sync, _active, _previewRequested, _disposed;
    private double? _positionX, _positionY;
    private string DraftKey => "subtitle." + _source.Text("taskId");
    private double Duration => _source["duration"]!.GetValue<double>();
    private static readonly string[] Colors = ["#FFFFFF", "#FFFF00", "#000000", "#00FFFF"];

    public SubtitleReviewPanel(JsonNode source, string folder, Func<SubtitleReviewPanel, Task> pickFolder, Func<JsonObject, Task> submit)
    {
        _source = source; _folder.Text = folder; _partial.IsVisible = source.Flag("partial");
        var saved = BrowserHost.ReadDraft(DraftKey); JsonNode? draft = null;
        if (saved.Length > 0) { try { draft = JsonNode.Parse(saved); } catch { } }
        if (draft.Text("sourceVersion") != source.Text("sourceVersion") || draft.Text("revision") != source.Text("revision")) draft = null;
        foreach (var cue in (draft?["cues"] ?? source["cues"]).Items())
            _cues.Add(new(cue["start"]!.GetValue<double>(), cue["end"]!.GetValue<double>(), cue.Text("text")));
        var style = draft?["style"] ?? source["style"]!;
        _size.Value = style["fontSize"]!.GetValue<int>(); _margin.Value = style["margin"]!.GetValue<int>();
        _alignment.SelectedIndex = style["alignment"]!.GetValue<int>() - 1;
        _color.ItemsSource = Colors.Contains(style.Text("color")) ? new[] { "白色", "黄色", "黑色", "青色" } : new[] { "白色", "黄色", "黑色", "青色", style.Text("color") };
        _color.SelectedIndex = Array.IndexOf(Colors, style.Text("color")) is var color && color >= 0 ? color : 4;
        _font.ItemsSource = new[] { "服务器默认", "PingFang SC", "Microsoft YaHei", "Arial", style.Text("font") }.Where(value => value.Length > 0).Distinct().ToArray();
        _font.SelectedItem = style.Text("font").Length > 0 ? style.Text("font") : "服务器默认";
        _positionX = style["positionX"]?.GetValue<double>(); _positionY = style["positionY"]?.GetValue<double>();
        _list.ItemsSource = _cues;
        _list.ItemTemplate = new FuncDataTemplate<Cue>((cue, _) => new TextBlock { Text = cue?.Summary, TextTrimming = TextTrimming.CharacterEllipsis });
        _range.Duration = Duration;
        var root = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new(12), RowSpacing = 8 };
        var body = new StackPanel { Spacing = 8 };
        root.Children.Add(new TextBlock { Text = source.Flag("partial") ? "部分识别结果 · 校对后确认再导出" : "字幕校对 · 文字可编辑，时间通过拖动调整" });
        body.Children.Add(_timing); body.Children.Add(_range); body.Children.Add(_text);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Button("添加字幕", () =>
        {
            var start = Math.Min((_list.SelectedItem as Cue)?.End ?? 0, Math.Max(0, Duration - .1));
            _cues.Add(new(start, Math.Min(Duration, start + 1), "新字幕")); _list.SelectedIndex = _cues.Count - 1; Save(); return Task.CompletedTask;
        }));
        actions.Children.Add(Button("删除此条", () =>
        { var index = _list.SelectedIndex; if (index >= 0) { _cues.RemoveAt(index); _list.SelectedIndex = Math.Min(index, _cues.Count - 1); Save(); } return Task.CompletedTask; }));
        actions.Children.Add(Button("按时间排序", () =>
        { var selected = _list.SelectedItem; var sorted = _cues.OrderBy(cue => cue.Start).ToArray(); _cues.Clear(); foreach (var cue in sorted) _cues.Add(cue); _list.SelectedItem = selected; Save(); return Task.CompletedTask; }));
        actions.Children.Add(Button("保存本机草稿", () => { Save(); _notice.Text = "草稿已保存在此浏览器，刷新后可继续校对。"; return Task.CompletedTask; }));
        actions.Children.Add(Button("播放此处", () =>
        {
            if (_list.SelectedItem is Cue cue) BrowserHost.Open("/web/media?path=" + Uri.EscapeDataString(source.Text("path")) + "#t=" + SourceSeconds(cue).ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Task.CompletedTask;
        }));
        body.Children.Add(actions);
        var styles = new StackPanel { Spacing = 6 };
        Field(styles, "字体", _font); Field(styles, "字号（0 自动）", _size); Field(styles, "颜色", _color); Field(styles, "位置", _alignment); Field(styles, "边距", _margin);
        body.Children.Add(new Expander { Header = "字幕样式", Content = styles });
        body.Children.Add(Button("读取字幕效果预览", () => { _previewRequested = true; return Preview(); })); body.Children.Add(_image);
        var content = new Grid { ColumnDefinitions = new("300,*"), ColumnSpacing = 12 };
        _list.Height = double.NaN; content.Children.Add(_list);
        var editor = new ScrollViewer { Content = body }; Grid.SetColumn(editor, 1); content.Children.Add(editor);
        content.PropertyChanged += (_, e) =>
        {
            if (e.Property != BoundsProperty) return;
            var narrow = content.Bounds.Width < 800;
            content.ColumnDefinitions = new(narrow ? "*" : "300,*"); content.RowDefinitions = new(narrow ? "170,*" : "*");
            Grid.SetColumn(editor, narrow ? 0 : 1); Grid.SetRow(editor, narrow ? 1 : 0);
        };
        Grid.SetRow(content, 1); root.Children.Add(content);
        var footer = new StackPanel { Spacing = 6 }; Field(footer, "导出内容", _format); footer.Children.Add(_folder);
        footer.Children.Add(Button("选择输出目录…", () => pickFolder(this))); footer.Children.Add(_partial); footer.Children.Add(_start);
        footer.Children.Add(Button("导出校对字幕", async () =>
        {
            if (_source.Flag("partial") && _partial.IsChecked != true) throw new InvalidOperationException("请确认仅导出部分识别结果。");
            Save(); await submit(new() { ["requestId"] = Guid.NewGuid().ToString(), ["taskId"] = source.Text("taskId"),
                ["sourceVersion"] = source.Text("sourceVersion"), ["revision"] = source.Text("revision"), ["cues"] = Cues(), ["style"] = Style(),
                ["format"] = _format.SelectedIndex == 2 ? "原格式" : (_format.SelectedItem?.ToString() ?? "SRT").ToLowerInvariant(), ["outputFolder"] = _folder.Text,
                ["allowPartial"] = _partial.IsChecked == true, ["startImmediately"] = _start.IsChecked == true });
        })); footer.Children.Add(_notice); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        _list.SelectionChanged += (_, _) => SelectCue();
        _text.TextChanged += (_, _) => UpdateCue(); _range.Changed += (_, _) => UpdateCue();
        _font.SelectionChanged += (_, _) => Changed(); _color.SelectionChanged += (_, _) => Changed();
        _alignment.SelectionChanged += (_, _) => { _positionX = _positionY = null; Changed(); };
        _size.ValueChanged += (_, _) => Changed(); _margin.ValueChanged += (_, _) => Changed();
        _timer.Tick += async (_, _) => { _timer.Stop(); if (_previewRequested && _active) await Preview(); };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        _list.SelectedIndex = _cues.Count > 0 ? 0 : -1;
        DetachedFromVisualTree += (_, _) => SetActive(false);
    }
    public void SetFolder(string value) => _folder.Text = value;
    public void SetActive(bool active)
    { _active = active && !_disposed; Cancel(); if (_active && _previewRequested) _timer.Start(); }
    private void SelectCue()
    {
        if (_sync) return;
        _sync = true;
        try
        {
            var cue = _list.SelectedItem as Cue; _text.Text = cue?.Text ?? ""; _text.IsEnabled = _range.IsEnabled = cue is not null;
            _range.Start = cue?.Start ?? 0; _range.End = cue?.End ?? Math.Min(1, Duration); _range.InvalidateVisual();
            _timing.Text = cue is null ? "暂无字幕，可添加或返回任务重新识别。" : $"{_list.SelectedIndex + 1} / {_cues.Count} · {cue.Start:0.###} – {cue.End:0.###} 秒";
        }
        finally { _sync = false; }
        Cancel(); if (_active && _previewRequested) _timer.Start();
    }
    private void UpdateCue()
    {
        if (_sync || _list.SelectedIndex < 0) return;
        var index = _list.SelectedIndex; _sync = true;
        try { _cues[index] = new(_range.Start, _range.End, _text.Text ?? ""); _list.SelectedIndex = index; }
        finally { _sync = false; }
        _timing.Text = $"{index + 1} / {_cues.Count} · {_range.Start:0.###} – {_range.End:0.###} 秒"; Changed();
    }
    private void Changed() { if (_sync) return; _saveTimer.Stop(); _saveTimer.Start(); Cancel(); if (_active && _previewRequested) _timer.Start(); }
    private void Save()
    {
        try { BrowserHost.WriteDraft(DraftKey, new JsonObject { ["sourceVersion"] = _source.Text("sourceVersion"), ["revision"] = _source.Text("revision"), ["cues"] = Cues(), ["style"] = Style() }.ToJsonString()); }
        catch (Exception error) { _notice.Text = "草稿未保存：" + error.Message; }
    }
    private JsonArray Cues() => new(_cues.Select(cue => (JsonNode)new JsonObject { ["start"] = cue.Start, ["end"] = cue.End, ["text"] = cue.Text }).ToArray());
    private JsonObject Style() => new() { ["fontSize"] = (int)(_size.Value ?? 48), ["color"] = _color.SelectedIndex is >= 0 and < 4 ? Colors[_color.SelectedIndex] : _color.SelectedItem?.ToString() ?? "#FFFFFF",
        ["alignment"] = _alignment.SelectedIndex + 1, ["margin"] = (int)(_margin.Value ?? 36),
        ["font"] = _font.SelectedItem?.ToString() == "服务器默认" ? "" : _font.SelectedItem?.ToString() ?? "", ["positionX"] = _positionX, ["positionY"] = _positionY };
    private double SourceSeconds(Cue cue) => _source["sourceStart"]!.GetValue<double>() + cue.Start * _source["speed"]!.GetValue<double>();
    private void Cancel() { ++_generation; _timer.Stop(); _cancel?.Cancel(); }
    private async Task Preview()
    {
        Cancel(); if (!_active || _list.SelectedItem is not Cue cue) return;
        if (!_source.Flag("hasVideo")) { _notice.Text = "音频源可播放此处试听，字幕样式请导出 ASS 查看。"; return; }
        var generation = _generation; using var cancellation = new CancellationTokenSource(); _cancel = cancellation;
        try
        {
            _notice.Text = "正在生成当前字幕效果…"; await _flight.WaitAsync(cancellation.Token);
            try
            {
                var result = await RemotePreview.Subtitle(new() { ["path"] = _source.Text("path"), ["sourceVersion"] = _source.Text("sourceVersion"),
                    ["seconds"] = SourceSeconds(cue), ["videoStreamIndex"] = _source["videoStreamIndex"]!.DeepClone(), ["text"] = cue.Text, ["style"] = Style() }, cancellation.Token);
                if (!_active || generation != _generation) return;
                using var stream = new MemoryStream(result.Image); var bitmap = new Bitmap(stream);
                _image.Source = bitmap; _bitmap?.Dispose(); _bitmap = bitmap; _notice.Text = "当前字幕效果预览";
            }
            finally { _flight.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (generation == _generation) _notice.Text = error.Message; }
        finally { if (ReferenceEquals(_cancel, cancellation)) _cancel = null; }
    }
    private Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text, Margin = new(0, 0, 6, 0) };
        button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } catch (Exception error) { _notice.Text = error.Message; } finally { button.IsEnabled = true; } }; return button;
    }
    private static void Field(Panel panel, string label, Control control)
    { panel.Children.Add(new TextBlock { Text = label }); panel.Children.Add(control); }
    public void Dispose() { Save(); _saveTimer.Stop(); _disposed = true; Cancel(); _image.Source = null; _bitmap?.Dispose(); _bitmap = null; }
}
