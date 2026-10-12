using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    private readonly TabControl _workspaceTabs = new();
    private readonly ComboBox _previewInput = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly VideoRegionPreview _preview = new() { MinHeight = 160, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CropOverlay _cropOverlay = new()
    {
        ShadeBrush = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
        SelectionBrush = new SolidColorBrush(Color.FromArgb(20, 0, 122, 255)),
        BorderBrush = Palette.Blue, HandleBrush = Brushes.White, LabelBrush = Palette.Blue
    };
    private static readonly string[] CropRatios = ["自由比例", "16:9", "9:16", "4:3", "3:4", "1:1"];
    private readonly ComboBox _cropRatio = new() { ItemsSource = CropRatios, SelectedIndex = 0 };
    private readonly WrapStack _cropChips = new() { Spacing = 6 };
    private StackPanel _cropActions = new();
    private readonly VideoRegionPreview _orientationPreview = new() { Width = 180, Height = 110, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new(10) };
    private readonly VisualNumber _seconds = new() { Minimum = 0, Maximum = 1000000, Increment = .1m, Value = 0 };
    private readonly DispatcherTimer _seekTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool _settingPosition;
    private bool _updatingPreviewInputs;
    private readonly TextBlock _mediaInfo = Ui.Text("添加视频后，选择文件并读取预览。裁剪和水印区域在原始画面上框选。", "caption");
    private JsonNode? _previewInfo;
    private int _previewGeneration;
    private Bitmap? _previewBitmap;
    private readonly SemaphoreSlim _previewFlight = new(1, 1);
    private CancellationTokenSource? _previewCancellation;
    private readonly TextBlock _previewStatus = Ui.Text("", "caption");
    private bool _pageVisible = true, _editorAttached, _previewRequested, _previewStale = true;
    private double _displayedSeconds;
    private bool PreviewActive => _editorAttached && _pageVisible && _tabs.SelectedIndex == 0 && _workspaceTabs.SelectedIndex == 1;

    // Editor chrome.
    private readonly Grid _editorRoot = new() { RowDefinitions = new("Auto,*"), RowSpacing = 10, Margin = new(14, 4, 14, 14) };
    private readonly Grid _editorBody = new() { ColumnSpacing = 12, RowSpacing = 12 };
    private readonly WrapStack _fileChips = new() { Spacing = 6 };
    private readonly ContentControl _toolBadge = new();
    private readonly Grid _editorStage = new() { RowDefinitions = new("*,Auto,Auto"), RowSpacing = 10 };
    private readonly Grid _canvas = new() { MinHeight = 200 };
    private readonly Border _canvasFrame = new() { Background = Brushes.Black, CornerRadius = new(10), ClipToBounds = true };
    private readonly TextBlock _canvasLabel = Ui.Text("", raw: true);
    private readonly TextBlock _timeLabel = Ui.Text("00:00.000 / 00:00.000", "mono", raw: true);
    private readonly Button _playButton = new() { Classes = { "icon" }, Width = 34, Height = 34 };
    private readonly ClipTimeline _timeline = new();
    private readonly StackPanel _stageFooter = new() { Spacing = 8 };
    private readonly StackPanel _sidePanel = new() { Spacing = 10 };
    private readonly StackPanel _sideTool = new() { Spacing = 10 };
    private readonly ScrollViewer _sideScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private Border _stageCard = new(), _sideCard = new();
    private readonly ScrollViewer _editorScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private bool HasEditor => _activeFeature is "clip" or "crop" or "delogo" or "join" or "rotate" or "contact-sheet" or "person-clip";

    private Control BuildEditor()
    {
        _previewInput.ItemTemplate = new FuncDataTemplate<FileBrowserItem>((item, _) => Ui.Text(item?.Name ?? "", raw: true));
        _previewInput.SelectionChanged += (_, _) =>
        {
            if (_updatingPreviewInputs) return;
            ClearPreview(); if (_activeFeature.Length > 0) BuildToolOptions();
            RenderFileChips();
            if (_workspaceTabs.SelectedIndex == 1 && _activeFeature != "join") _ = Guard(RefreshPreview);
        };
        // Top bar: back to files, file chips, tool badge.
        var back = new Button { Content = Ui.Content("服务器文件", "chevron-left", Palette.Blue), Classes = { "link" } };
        back.Click += (_, _) => _workspaceTabs.SelectedIndex = 0;
        var top = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 12 };
        top.Children.Add(back); Grid.SetColumn(_fileChips, 1); top.Children.Add(_fileChips);
        _fileChips.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_toolBadge, 2); top.Children.Add(_toolBadge);
        _editorRoot.Children.Add(top);

        // Stage: canvas + transport + tool footer.
        _canvas.Children.Add(_preview); _canvas.Children.Add(_cropOverlay); _canvas.Children.Add(_orientationPreview);
        _canvasLabel.Foreground = Brushes.White; _canvasLabel.FontSize = 11.5;
        _canvas.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), CornerRadius = new(6), Padding = new(8, 3),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(10), Child = _canvasLabel, IsHitTestVisible = false });
        _canvasFrame.Child = _canvas;
        _editorStage.Children.Add(_canvasFrame);
        var transport = BuildTransport(); Grid.SetRow(transport, 1); _editorStage.Children.Add(transport);
        Grid.SetRow(_stageFooter, 2); _editorStage.Children.Add(_stageFooter);
        _timeline.Seek += seconds => { _seconds.Value = (decimal)seconds; };
        _timeline.SegmentSelected += index => { if (_activeFeature == "delogo") { if (index != _delogoSelected) SelectDelogo(index); return; } if (_activeFeature == "clip" && CurrentClip is { } draft && index != draft.Selected) SelectSegment(draft, index); };
        _timeline.HandleMoved += (index, isStart, seconds, finished) =>
        {
            if (_activeFeature == "delogo")
            {
                if (index != _delogoSelected) SelectDelogo(index);
                (isStart ? _delogoStart : _delogoEnd).Value = (decimal)seconds; if (finished) _seconds.Value = (decimal)seconds; return;
            }
            if (_activeFeature == "clip" && CurrentClip is { } draft && index != draft.Selected) SelectSegment(draft, index);
            SetNumber(isStart ? TimelineStartKey : TimelineEndKey, seconds);
            if (finished) { _seconds.Value = (decimal)seconds; ScheduleBoundaries(); }
        };
        _stageCard = Ui.Card(new Border { Padding = new(12), Child = _editorStage });
        _sidePanel.Children.Add(_sideTool); _sidePanel.Children.Add(_formHostEditor);
        _sideScroll.Content = new Border { Padding = new(14), Child = _sidePanel };
        _sideCard = Ui.Card(_sideScroll);
        _editorBody.Children.Add(_stageCard); _editorBody.Children.Add(_sideCard);
        Grid.SetRow(_editorBody, 1); _editorRoot.Children.Add(_editorBody);

        _cropRatio.SelectionChanged += (_, _) => { UpdateCropRatio(); RenderCropChips(); };
        _cropActions = Actions(Action("重置为全画面", () =>
        { SetRegion(new Rect(0, 0, _cropOverlay.SourceWidth, _cropOverlay.SourceHeight)); return Task.CompletedTask; }, "maximize"),
            Action("清除选区", () => { SetRegion(new Rect()); return Task.CompletedTask; }, "x"));
        BuildClipEditor();
        _seconds.ValueChanged += (_, _) =>
        {
            _timeline.Position = (double)(_seconds.Value ?? 0); UpdateTimeLabel();
            if (_settingPosition || !_previewRequested || _activeFeature == "contact-sheet") return;
            CancelPreview();
            _previewStatus.Text = L.F("定位中 · 当前画面 {0} 秒", _displayedSeconds.ToString("0.###"));
            if (PreviewActive) _seekTimer.Start();
        };
        _seekTimer.Tick += async (_, _) => { _seekTimer.Stop(); await Guard(RefreshPreview); };
        _playTimer.Tick += (_, _) =>
        {
            var duration = _previewInfo?["duration"]?.GetValue<double>() ?? 0;
            if (_previewStale && _previewRequested) return; // Wait until the previous frame has arrived.
            var next = (double)(_seconds.Value ?? 0) + .5;
            if (duration <= 0 || next >= duration) { StopPlayback(); return; }
            _seconds.Value = (decimal)next;
        };
        _previewStatus.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) _canvasLabel.Text = _previewStatus.Text; };
        _canvasLabel.Classes.Add("raw");
        _workspaceTabs.SelectionChanged += (_, e) => { if (e.Source == _workspaceTabs) { UpdatePreviewActivity(); if (_workspaceTabs.SelectedIndex != 1) StopPlayback(); } };
        _tabs.SelectionChanged += (_, e) => { if (e.Source == _tabs) UpdatePreviewActivity(); };
        AttachedToVisualTree += (_, _) =>
        {
            _editorAttached = true;
            BrowserHost.ObserveVisibility(visible => { _pageVisible = visible; UpdatePreviewActivity(); });
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _editorAttached = false; BrowserHost.StopObservingVisibility(); CancelPreview(); StopPlayback();
        };
        _cropOverlay.Changed += SetRegion;
        LayoutEditor();
        return _editorScroll;
    }
    private Control BuildTransport()
    {
        Button Step(string icon, string tip, Func<Task> run)
        {
            var button = new Button { Content = Ui.Icon(icon, 15, Palette.Text), Classes = { "plain", "icon" }, Width = 30, Height = 30 };
            ToolTip.SetTip(button, L.T(tip)); Avalonia.Automation.AutomationProperties.SetName(button, tip);
            button.Click += async (_, _) => await Guard(run); return button;
        }
        _playButton.Classes.Add("primary"); _playButton.Content = Ui.Icon("play", 15, Brushes.White);
        ToolTip.SetTip(_playButton, L.T("逐帧播放预览")); Avalonia.Automation.AutomationProperties.SetName(_playButton, "播放");
        _playButton.Click += (_, _) => { if (_playTimer.IsEnabled) StopPlayback(); else StartPlayback(); };
        var controls = Ui.Row(2,
            Step("chevrons-left", "跳到片段起点", () => { _seconds.Value = (decimal)GetNumber(TimelineStartKey); return Task.CompletedTask; }),
            Step("chevron-left", "上一帧", () => StepFrame(-1)), _playButton,
            Step("chevron-right", "下一帧", () => StepFrame(1)),
            Step("chevrons-right", "跳到片段终点", () =>
            {
                var end = GetNumber(TimelineEndKey); if (end <= 0) end = _previewInfo?["duration"]?.GetValue<double>() ?? 0;
                _seconds.Value = (decimal)end; return Task.CompletedTask;
            }));
        _timeLabel.FontSize = 12.5; _timeLabel.VerticalAlignment = VerticalAlignment.Center;
        var refresh = Action("读取预览帧", RefreshPreview, "refresh-cw");
        var play = Action("浏览器播放", () => { BrowserHost.Open("/web/media?path=" + Uri.EscapeDataString(PreviewPath())); return Task.CompletedTask; }, "monitor");
        var info = Action("媒体信息", async () =>
        {
            var path = PreviewPath(); var probe = await RemoteApi.Call("probe", new() { ["path"] = path });
            if (path == SelectedPreviewPath) ShowMediaInfo(probe);
        }, "info");
        var right = Ui.Row(6, refresh, play, info); right.HorizontalAlignment = HorizontalAlignment.Right;
        var grid = new Grid { ColumnDefinitions = new("Auto,Auto,*"), ColumnSpacing = 12 };
        grid.Children.Add(controls); Grid.SetColumn(_timeLabel, 1); grid.Children.Add(_timeLabel);
        Grid.SetColumn(right, 2); grid.Children.Add(right);
        var panel = new StackPanel { Spacing = 4 }; panel.Children.Add(grid); panel.Children.Add(_mediaInfo);
        _mediaInfo.TextTrimming = TextTrimming.CharacterEllipsis;
        return panel;
    }
    private void StartPlayback()
    {
        if (_previewInfo is null) { _ = Guard(RefreshPreview); }
        _playTimer.Start(); _playButton.Content = Ui.Icon("pause", 15, Brushes.White);
    }
    private void StopPlayback() { _playTimer.Stop(); _playButton.Content = Ui.Icon("play", 15, Brushes.White); }
    private async Task StepFrame(int frames)
    {
        var path = PreviewPath();
        var next = await RemotePreview.Nudge(path, (double)(_seconds.Value ?? 0), frames, _optionDraft["videoStreamIndex"]?.GetValue<int>() ?? 0, CancellationToken.None);
        _seconds.Value = (decimal)next;
    }
    private void UpdateTimeLabel()
    {
        var duration = _previewInfo?["duration"]?.GetValue<double>() ?? 0;
        _timeLabel.Text = ClipTimeline.Long((double)(_seconds.Value ?? 0)) + " / " + ClipTimeline.Long(duration);
    }
    private void LayoutEditor()
    {
        _editorBody.ColumnDefinitions.Clear(); _editorBody.RowDefinitions.Clear();
        _editorRoot.Children.Remove(_editorBody); _editorScroll.Content = null;
        if (_mobile)
        {
            _editorBody.RowDefinitions = new("Auto,Auto"); Grid.SetColumn(_sideCard, 0); Grid.SetRow(_sideCard, 1);
            _sideScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled; _canvasFrame.Height = 210;
            _editorRoot.RowDefinitions = new("Auto,Auto"); _editorRoot.Margin = new(12, 4, 12, 90);
        }
        else
        {
            _editorBody.ColumnDefinitions = new("*,320"); Grid.SetColumn(_sideCard, 1); Grid.SetRow(_sideCard, 0);
            _sideScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; _canvasFrame.Height = double.NaN;
            _editorRoot.RowDefinitions = new("Auto,*"); _editorRoot.Margin = new(14, 4, 14, 14);
        }
        Grid.SetRow(_editorBody, 1); _editorRoot.Children.Add(_editorBody);
        _editorScroll.VerticalScrollBarVisibility = _mobile ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _editorScroll.Content = _editorRoot;
    }
    private void RenderFileChips()
    {
        _fileChips.Children.Clear();
        var videos = IncludedInputs.Where(pair => !pair.Value.IsFolder).ToArray();
        if (_activeFeature == "join")
        {
            _fileChips.Children.Add(Ui.Pill(Ui.Content(L.F("{0} 个文件", videos.Length), "layers", Palette.Blue), Palette.BlueSoft, new(10, 4)));
            return;
        }
        var selected = SelectedPreviewPath;
        foreach (var (path, item) in videos.Take(_mobile ? 2 : 4))
        {
            var chip = new Button { Content = Ui.Content(item.Name, Ui.FileIcon(item.Name, false)), Classes = { "chip" }, MaxWidth = 220 };
            if (path == selected) chip.Classes.Add("selected");
            chip.Click += (_, _) => { if (path != SelectedPreviewPath) _previewInput.SelectedItem = item; };
            _fileChips.Children.Add(chip);
        }
        if (videos.Length > (_mobile ? 2 : 4)) _fileChips.Children.Add(Ui.Text("+" + (videos.Length - (_mobile ? 2 : 4)), "caption", raw: true));
    }
    private void UpdateEditorChrome()
    {
        var feature = _features.FirstOrDefault(item => item.Text("id") == _activeFeature);
        _toolBadge.Content = _activeFeature.Length == 0 ? null
            : Ui.Pill(Ui.Content(feature.Text("name"), ToolIcon(_activeFeature), Palette.Blue), Palette.BlueSoft, new(10, 4));
        var merge = _activeFeature == "join";
        _editorStage.Children.Remove(_mergeView);
        foreach (var child in _editorStage.Children) child.IsVisible = !merge;
        if (merge) { Grid.SetRowSpan(_mergeView, 3); _editorStage.Children.Add(_mergeView); RenderMerge(); }
        _stageFooter.Children.Clear(); _sideTool.Children.Clear();
        _cropOverlay.Label = _activeFeature == "crop" ? null : null; _cropOverlay.ShowGrid = _activeFeature == "crop";
        _cropOverlay.Shade = _activeFeature != "delogo";
        switch (_activeFeature)
        {
            case "clip":
                _stageFooter.Children.Add(_timeline); _stageFooter.Children.Add(_clipActions);
                _sideTool.Children.Add(_clipSide); break;
            case "crop":
                _stageFooter.Children.Add(FooterHint(_cropActions, "拖动边框或角点调整；按住画面移动选区。"));
                RenderCropChips(); _sideTool.Children.Add(Ui.Text("比例", "section")); _sideTool.Children.Add(_cropChips);
                _sideTool.Children.Add(_cropSize); break;
            case "delogo":
                _stageFooter.Children.Add(_timeline);
                _stageFooter.Children.Add(FooterHint(_cropActions, "在画面上框选水印；可添加多个区域并设置出现时间。"));
                _sideTool.Children.Add(_delogoSide); RenderDelogoRegions(); break;
            case "join": _sideTool.Children.Add(_mergeSide); break;
            default:
                if (_optionControls.ContainsKey(TimelineStartKey)) _stageFooter.Children.Add(_timeline);
                break;
        }
        _timeline.IsVisible = _activeFeature != "join";
        _timeline.LabelFormat = _activeFeature == "delogo" ? "区域 {0}" : "片段 {0}";
        RenderFileChips(); UpdateClipEditor();
    }
    private static Control FooterHint(Control actions, string hint)
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
        if (actions.Parent is Panel previous) previous.Children.Remove(actions); // Reused across tool switches.
        grid.Children.Add(actions); var text = Ui.Text(hint, "caption", trim: true); text.HorizontalAlignment = HorizontalAlignment.Right; text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1); grid.Children.Add(text); return grid;
    }
    private static string ToolIcon(string id) => id switch
    {
        "clip" or "person-clip" => "scissors", "crop" => "crop", "delogo" => "eraser", "join" => "layers",
        "rotate" => "rotate-cw", "contact-sheet" => "image-file", _ => "film"
    };
    private void UpdateEditorSummary() => UpdateClipSummary();
    private readonly TextBlock _cropSize = Ui.Text("", "secondary", raw: true);
    private void RenderCropChips()
    {
        _cropChips.Children.Clear();
        for (var index = 0; index < CropRatios.Length; index++)
        {
            var chip = new Button { Content = CropRatios[index], Classes = { "chip" } }; var captured = index;
            if (_cropRatio.SelectedIndex == index) chip.Classes.Add("selected");
            chip.Click += (_, _) => _cropRatio.SelectedIndex = captured;
            _cropChips.Children.Add(chip);
        }
        var width = GetNumber("cropWidth"); var height = GetNumber("cropHeight");
        _cropSize.Text = width > 0 && height > 0 ? L.F("输出尺寸 {0} × {1}", width, height) : L.T("尚未框选区域");
    }
    private string PreviewPath() => _previewInput.SelectedItem is FileBrowserItem item && !item.IsFolder
        ? _source.DisplayAddress(item) : throw new InvalidOperationException("请先添加并选择一个视频文件。文件夹请先展开选择视频。");
    private async Task RefreshPreview()
    {
        if (!PreviewActive) return;
        var path = PreviewPath();
        CancelPreview(); _previewRequested = true;
        var generation = _previewGeneration;
        var seconds = (double)(_seconds.Value ?? 0);
        using var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        var ct = cancellation.Token;
        _previewStatus.Text = "正在读取当前帧…";
        try
        {
            // A superseding seek cancels this operation before taking the one client slot.
            await _previewFlight.WaitAsync(ct);
            try
            {
                SaveOptions();
                var result = _activeFeature == "contact-sheet"
                    ? await RemotePreview.ContactSheet(path, _optionDraft["contactSheet"]!.DeepClone(), ct)
                    : await RemotePreview.Read(path, seconds, _preview.Bounds.Width > 640 ? 960 : 640,
                        _optionDraft["videoStreamIndex"]?.GetValue<int>() ?? 0, _optionDraft["audioStreamIndex"]?.GetValue<int>() ?? 0, ct);
                ct.ThrowIfCancellationRequested();
                // Let input/layout run before decoding the bounded thumbnail on the WASM UI thread.
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                ct.ThrowIfCancellationRequested();
                if (generation != _previewGeneration || !PreviewActive) return;
                using var stream = new MemoryStream(result.Image, writable: false);
                var bitmap = new Bitmap(stream);
                _preview.Image = _orientationPreview.Image = bitmap;
                _previewBitmap?.Dispose(); _previewBitmap = bitmap;
                _previewInfo = result.Metadata["info"];
                _settingPosition = true;
                try
                {
                    ShowMediaInfo(_previewInfo);
                    _displayedSeconds = result.Metadata["seconds"]!.GetValue<double>();
                    _seconds.Value = (decimal)_displayedSeconds;
                }
                finally { _settingPosition = false; }
                _previewStale = false;
                _previewStatus.Text = _activeFeature == "contact-sheet" ? "第一张拼图预览" : $"当前画面 {_displayedSeconds:0.###} 秒";
                UpdateRegion();
            }
            finally { _previewFlight.Release(); }
        }
        catch (OperationCanceledException)
        {
            if (!ct.IsCancellationRequested && generation == _previewGeneration) _previewStatus.Text = "预览超时，点击读取预览帧重试。";
        }
        catch (Exception error)
        {
            if (generation == _previewGeneration) _previewStatus.Text = "预览失败：" + error.Message;
        }
        finally { if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null; }
    }
    private void ScheduleParameterPreview()
    {
        if (_settingPosition || !_previewRequested) return;
        CancelPreview();
        if (PreviewActive) _seekTimer.Start();
    }
    private void CancelPreview()
    {
        ++_previewGeneration; _seekTimer.Stop(); _previewCancellation?.Cancel();
        _previewStale = true; _cropOverlay.Enabled = false;
    }
    private void UpdatePreviewActivity()
    {
        if (!PreviewActive)
        {
            CancelPreview();
            if (_previewRequested) _previewStatus.Text = "预览已暂停";
        }
        else if (_previewRequested && _previewStale) { _seekTimer.Stop(); _seekTimer.Start(); }
    }
    private void ShowMediaInfo(JsonNode? info)
    {
        var duration = info?["duration"]?.GetValue<double>() ?? 0;
        _mediaInfo.Text = $"{info?["width"]} × {info?["height"]} · {ClipTimeline.Long(duration)} · {info.Text("videoCodec")} · {info.Text("audioCodec")}";
        UpdateTimeLabel(); _timeline.Duration = duration;
        if (SelectedPreviewPath is { } stripPath && duration > 0) _ = LoadStrip(stripPath);
        _seconds.Maximum = (decimal)Math.Max(.01, duration);
        foreach (var key in new[] { "start", "end", "contactSheet.startSeconds", "contactSheet.endSeconds" })
            if (_optionControls.GetValueOrDefault(key) is VisualNumber timeline) timeline.Maximum = (decimal)Math.Max(.01, duration);
        foreach (var prefix in new[] { "crop", "delogo" })
            foreach (var suffix in new[] { "X", "Y", "Width", "Height" })
                if (_optionControls.GetValueOrDefault(prefix + suffix) is VisualNumber dimension)
                    dimension.Maximum = Math.Max(2, (info?[suffix is "X" or "Width" ? "width" : "height"]?.GetValue<int>() ?? 2) / 2 * 2);
    }
    private void ClearPreview()
    {
        CancelPreview(); _previewRequested = false; _previewInfo = null; _previewStatus.Text = ""; _compressionPlan.Text = "";
        _preview.Image = _orientationPreview.Image = null; _previewBitmap?.Dispose(); _previewBitmap = null;
        _orientationPreview.IsVisible = false;
        StopPlayback(); _seconds.Value = 0; _cropOverlay.Selection = new(); _cropOverlay.InvalidateVisual();
        _mediaInfo.Text = "选择文件后点击读取预览帧；在原始画面中框选裁剪或水印区域。";
        UpdateRegion();
    }
    private void SetNumber(string key, double value)
    {
        if (_optionControls.GetValueOrDefault(key) is not VisualNumber number)
            throw new InvalidOperationException("请先选择快速剪辑、裁剪或去水印工具。");
        number.Value = (decimal)value;
    }
    private double GetNumber(string key) => _optionControls.GetValueOrDefault(key) is VisualNumber number ? (double)(number.Value ?? 0) : 0;
    private void SetRegion(Rect region)
    {
        if (_previewInfo is null || _previewStale || _activeFeature is not ("clip" or "crop" or "delogo")) return;
        var prefix = _activeFeature == "delogo" ? "delogo" : "crop";
        var width = _previewInfo["width"]!.GetValue<int>(); var height = _previewInfo["height"]!.GetValue<int>();
        var x = (int)region.X / 2 * 2; var y = (int)region.Y / 2 * 2;
        SetNumber(prefix + "X", x); SetNumber(prefix + "Y", y);
        SetNumber(prefix + "Width", Math.Min(width - x, (int)region.Width) / 2 * 2);
        SetNumber(prefix + "Height", Math.Min(height - y, (int)region.Height) / 2 * 2);
        UpdateRegion();
    }
    private void UpdateRegion()
    {
        var rotation = _optionControls.GetValueOrDefault("rotation") is ComboBox choice ? (choice.SelectedItem as JsonNode)?["value"]?.GetValue<int>() ?? 0 : 0;
        if (_activeFeature == "rotate" && SelectedPreviewPath is { } path) rotation = _rotationOverrides.GetValueOrDefault(path, rotation);
        var flip = _optionControls.GetValueOrDefault("flip") is CheckBox check && check.IsChecked == true;
        _orientationPreview.Rotation = rotation; _orientationPreview.Flip = flip;
        _orientationPreview.IsVisible = _previewBitmap is not null && (rotation != 0 || flip);
        _orientationPreview.InvalidateVisual();
        _cropOverlay.Enabled = !_previewStale && _previewBitmap is not null && _activeFeature is ("clip" or "crop" or "delogo");
        _cropActions.IsEnabled = _cropOverlay.Enabled;
        if (_previewInfo is null) { UpdateClipEditor(); return; }
        var width = _previewInfo["width"]?.GetValue<double>() ?? 0; var height = _previewInfo["height"]?.GetValue<double>() ?? 0;
        var prefix = _activeFeature == "delogo" ? "delogo" : "crop";
        _cropOverlay.SourceWidth = (int)width; _cropOverlay.SourceHeight = (int)height;
        if (_previewBitmap is not null && width > 0 && height > 0)
            _cropOverlay.PixelAspectRatio = _previewBitmap.Size.Width / _previewBitmap.Size.Height * height / width;
        _cropOverlay.Selection = width > 0 && height > 0
            ? new(GetNumber(prefix + "X"), GetNumber(prefix + "Y"), GetNumber(prefix + "Width"), GetNumber(prefix + "Height")) : new();
        if (_activeFeature == "delogo") UpdateDelogoOverlay();
        if (_activeFeature == "crop") RenderCropChips();
        UpdateCropRatio(); _cropOverlay.InvalidateVisual(); UpdateClipEditor();
    }
    private void UpdateCropRatio()
    {
        var ratio = _cropRatio.SelectedIndex switch { 1 => 16d / 9, 2 => 9d / 16, 3 => 4d / 3, 4 => 3d / 4, 5 => 1d, _ => 0 };
        _cropOverlay.AspectRatio = ratio / _cropOverlay.PixelAspectRatio;
    }

}
