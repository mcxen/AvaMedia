using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaMedia.FileUi;

namespace AvaMedia.Browser;

public sealed partial class ConsoleView
{
    private readonly TabControl _workspaceTabs = new();
    private readonly ComboBox _previewInput = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly VideoRegionPreview _preview = new() { MinHeight = 180, Height = 360, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly VideoRegionPreview _orientationPreview = new() { Height = 220, IsVisible = false };
    private readonly VisualNumber _seconds = new() { Minimum = 0, Maximum = 1000000, Increment = .1m, Value = 0 };
    private readonly DispatcherTimer _seekTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _settingPosition;
    private readonly TextBlock _mediaInfo = Label("添加视频后，选择文件并读取预览。裁剪和水印区域在原始画面上框选。");
    private JsonNode? _previewInfo;
    private int _previewGeneration;
    private Bitmap? _previewBitmap;
    private readonly SemaphoreSlim _previewFlight = new(1, 1);
    private CancellationTokenSource? _previewCancellation;
    private readonly TextBlock _previewStatus = Label("");
    private bool _pageVisible = true, _editorAttached, _previewRequested, _previewStale = true;
    private double _displayedSeconds;
    private bool PreviewActive => _editorAttached && _pageVisible && _tabs.SelectedIndex == 0 && _workspaceTabs.SelectedIndex == 1;

    private Control BuildEditor()
    {
        var panel = new StackPanel { Spacing = 10, Margin = new(12) };
        _previewInput.ItemTemplate = new FuncDataTemplate<FileBrowserItem>((item, _) => Label(item?.Name ?? ""));
        _previewInput.SelectionChanged += (_, _) => ClearPreview();
        Field(panel, "预览文件", _previewInput);
        panel.Children.Add(Actions(Action("读取预览帧", RefreshPreview), Action("浏览器播放", () =>
        {
            var path = PreviewPath(); BrowserHost.Open("/web/media?path=" + Uri.EscapeDataString(path)); return Task.CompletedTask;
        }), Action("媒体信息", async () =>
        {
            var path = PreviewPath(); var info = await RemoteApi.Call("probe", new() { ["path"] = path });
            if (path == (_previewInput.SelectedItem as FileBrowserItem)?.Address) ShowMediaInfo(info);
        })));
        panel.Children.Add(_mediaInfo); panel.Children.Add(_previewStatus); panel.Children.Add(_preview);
        Field(panel, "旋转 / 翻转后的方向", _orientationPreview);
        Field(panel, "时间轴（秒）", _seconds);
        _seconds.ValueChanged += (_, _) =>
        {
            if (_settingPosition || !_previewRequested) return;
            CancelPreview();
            _previewStatus.Text = $"定位中 · 当前画面 {_displayedSeconds:0.###} 秒";
            if (PreviewActive) _seekTimer.Start();
        };
        _seekTimer.Tick += async (_, _) => { _seekTimer.Stop(); await Guard(RefreshPreview); };
        _workspaceTabs.SelectionChanged += (_, e) => { if (e.Source == _workspaceTabs) UpdatePreviewActivity(); };
        _tabs.SelectionChanged += (_, e) => { if (e.Source == _tabs) UpdatePreviewActivity(); };
        AttachedToVisualTree += (_, _) =>
        {
            _editorAttached = true;
            BrowserHost.ObserveVisibility(visible => { _pageVisible = visible; UpdatePreviewActivity(); });
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _editorAttached = false; BrowserHost.StopObservingVisibility(); CancelPreview();
        };
        panel.Children.Add(Actions(Action("设为开始", () => { SetNumber("start", (double)(_seconds.Value ?? 0)); return Task.CompletedTask; }),
            Action("设为结束", () => { SetNumber("end", (double)(_seconds.Value ?? 0)); return Task.CompletedTask; }),
            Action("清除选区", () => { SetRegion(new Rect()); return Task.CompletedTask; })));
        panel.Children.Add(Label("停止拖动后更新单帧；切换页面暂停预览。裁剪在当前帧加载完成后框选。"));
        _preview.RegionSelected += SetRegion;
        return new ScrollViewer { Content = panel };
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
                var result = await RemotePreview.Read(path, seconds, _preview.Bounds.Width > 640 ? 960 : 640, ct);
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
                _previewStatus.Text = $"当前画面 {_displayedSeconds:0.###} 秒";
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
    private void CancelPreview()
    {
        ++_previewGeneration; _seekTimer.Stop(); _previewCancellation?.Cancel();
        _previewStale = true; _preview.CanSelect = false;
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
        _mediaInfo.Text = $"{info?["width"]} × {info?["height"]} · {duration:0.###} 秒 · {info.Text("videoCodec")} · {info.Text("audioCodec")}";
        _seconds.Maximum = (decimal)Math.Max(.01, duration);
        foreach (var key in new[] { "start", "end" })
            if (_optionControls.GetValueOrDefault(key) is VisualNumber timeline) timeline.Maximum = (decimal)Math.Max(.01, duration);
        foreach (var prefix in new[] { "crop", "delogo" })
            foreach (var suffix in new[] { "X", "Y", "Width", "Height" })
                if (_optionControls.GetValueOrDefault(prefix + suffix) is VisualNumber dimension)
                    dimension.Maximum = Math.Max(2, (info?[suffix is "X" or "Width" ? "width" : "height"]?.GetValue<int>() ?? 2) / 2 * 2);
    }
    private void ClearPreview()
    {
        CancelPreview(); _previewRequested = false; _previewInfo = null; _previewStatus.Text = "";
        _preview.Image = _orientationPreview.Image = null; _previewBitmap?.Dispose(); _previewBitmap = null;
        _orientationPreview.IsVisible = false;
        _seconds.Value = 0; _preview.Region = new();
        _mediaInfo.Text = "选择文件后点击读取预览帧；在原始画面中框选裁剪或水印区域。";
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
        // Map display-relative coordinates onto the autorotated editing grid; SAR cancels in the ratio.
        var x = (int)(region.X * width) / 2 * 2; var y = (int)(region.Y * height) / 2 * 2;
        SetNumber(prefix + "X", x); SetNumber(prefix + "Y", y);
        SetNumber(prefix + "Width", Math.Min(width - x, (int)(region.Width * width)) / 2 * 2);
        SetNumber(prefix + "Height", Math.Min(height - y, (int)(region.Height * height)) / 2 * 2);
        UpdateRegion();
    }
    private void UpdateRegion()
    {
        var rotation = _optionControls.GetValueOrDefault("rotation") is ComboBox choice ? (choice.SelectedItem as JsonNode)?["value"]?.GetValue<int>() ?? 0 : 0;
        var flip = _optionControls.GetValueOrDefault("flip") is CheckBox check && check.IsChecked == true;
        _orientationPreview.Rotation = rotation; _orientationPreview.Flip = flip;
        _orientationPreview.IsVisible = _previewBitmap is not null && (rotation != 0 || flip);
        _orientationPreview.InvalidateVisual();
        _preview.CanSelect = !_previewStale && _previewBitmap is not null && _activeFeature is ("clip" or "crop" or "delogo");
        if (_previewInfo is null) return;
        var width = _previewInfo["width"]?.GetValue<double>() ?? 0; var height = _previewInfo["height"]?.GetValue<double>() ?? 0;
        var prefix = _activeFeature == "delogo" ? "delogo" : "crop";
        _preview.Region = width > 0 && height > 0 && _preview.CanSelect
            ? new(GetNumber(prefix + "X") / width, GetNumber(prefix + "Y") / height, GetNumber(prefix + "Width") / width, GetNumber(prefix + "Height") / height) : new();
    }
}
