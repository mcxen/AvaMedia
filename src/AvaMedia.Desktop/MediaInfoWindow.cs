using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed class MediaInfoWindow : Window
{
    private readonly IMediaEngine _engine;
    private readonly ListBox _files = new();
    private readonly StackPanel _details = new() { Spacing = 12 };
    private readonly TextBlock _status = Ui.Text("", "caption");
    private readonly Button _export;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _probe;
    private string? _json;
    public MediaInfoWindow(IMediaEngine engine, IEnumerable<string> files)
    {
        _engine = engine; Title = "媒体信息"; Width = 900; Height = 620; MinWidth = 700; MinHeight = 440; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 12, Margin = new(20) };
        root.Children.Add(Ui.Button("添加文件…", async () => { try { AddFiles(await Ui.Pick(this, "选择媒体文件")); } catch(Exception error) { _status.Text = error.Message; } }));
        var body = new Grid { ColumnDefinitions = new("260,*"), ColumnSpacing = 20 }; body.Children.Add(_files);
        body.Children.Add(new ScrollViewer { Content = _details, [Grid.ColumnProperty] = 1 }); Grid.SetRow(body, 1); root.Children.Add(body);
        var footer = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 10 }; footer.Children.Add(_status);
        _export = Ui.Button("导出 JSON…", async () =>
        {
            var json = _json; if(json is null) return;
            try
            {
                var file = await StorageProvider.SaveFilePickerAsync(new() { Title = Localization.Text("导出媒体信息"), SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_files.SelectedItem as string) + ".json", DefaultExtension = "json" });
                if(file is null) return; await using var stream = await file.OpenWriteAsync(); stream.SetLength(0);
                await using var writer = new StreamWriter(stream); await writer.WriteAsync(json);
            }
            catch(Exception error) { _status.Text = error.Message; }
        }); Grid.SetColumn(_export, 1); footer.Children.Add(_export);
        var close = Ui.DialogButton("关闭", Close); Grid.SetColumn(close, 2); footer.Children.Add(close); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        _files.ItemTemplate = new FuncDataTemplate<string>((path, _) => { var text = Ui.Text(System.IO.Path.GetFileName(path)); Localization.SetIsUserText(text, true); text.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis; return text; });
        _files.SelectionChanged += async (_, _) => await ReadAsync();
        Closed += (_, _) => { _lifetime.Cancel(); _probe?.Cancel(); }; AddFiles(files);
    }
    private void AddFiles(IEnumerable<string> files)
    {
        _files.ItemsSource = (_files.ItemsSource?.OfType<string>() ?? []).Concat(files.Where(File.Exists)).Select(System.IO.Path.GetFullPath).Distinct(VideoFolderScanner.PathComparer).ToArray();
        if(_files.SelectedItem is null) _files.SelectedIndex = 0;
    }
    private async Task ReadAsync()
    {
        _probe?.Cancel(); _json = null; _export.IsEnabled = false; _details.Children.Clear();
        if(_files.SelectedItem is not string path) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); operation.CancelAfter(TimeSpan.FromSeconds(30)); _probe = operation;
        _status.Text = Localization.Text("正在读取媒体信息…");
        try
        {
            var info = await _engine.Probe(path, operation.Token); if(operation.IsCancellationRequested || _probe != operation) return;
            using var json = JsonDocument.Parse(info.RawJson); var root = json.RootElement;
            var title = Ui.Text(System.IO.Path.GetFileName(path), "heading"); Localization.SetIsUserText(title, true); _details.Children.Add(title);
            Row("时长", MediaTime.Format(info.Duration)); Row("文件大小", $"{new FileInfo(path).Length / 1000000d:0.##} MB");
            if(info.HasVideo) { Row("画面", $"{info.Width} × {info.Height}"); Row("帧率", $"{info.FrameRate:0.###} fps"); }
            if(root.TryGetProperty("format", out var format))
            { if(format.TryGetProperty("format_long_name", out var name)) Row("容器", name.GetString() ?? ""); if(format.TryGetProperty("bit_rate", out var rate) && long.TryParse(rate.GetString(), out var bitrate)) Row("总码率", $"{bitrate / 1000d:0.##} kbps"); }
            foreach(var type in new[] { "video", "audio", "subtitle" })
            {
                var tracks = root.GetProperty("streams").EnumerateArray().Where(stream => stream.TryGetProperty("codec_type", out var kind) && kind.GetString() == type).ToArray();
                if(tracks.Length == 0) continue;
                _details.Children.Add(Ui.Text(type == "video" ? "视频轨" : type == "audio" ? "音轨" : "字幕轨", "heading"));
                for(var i = 0; i < tracks.Length; i++) { var text = Ui.Text(TrackSelector.Label(tracks[i], i)); Localization.SetIsUserText(text, true); _details.Children.Add(text); }
            }
            _json = info.RawJson; _export.IsEnabled = true; _status.Text = "";
        }
        catch(OperationCanceledException) { if(!_lifetime.IsCancellationRequested && _probe == operation) _status.Text = Localization.Text("读取已取消或超时"); }
        catch(Exception error) { if(!_lifetime.IsCancellationRequested && _probe == operation) _status.Text = error.Message; }
        finally { if(_probe == operation) _probe = null; }
    }
    private void Row(string label, string value)
    {
        var row = new Grid { ColumnDefinitions = new("110,*"), ColumnSpacing = 8 }; row.Children.Add(Ui.Text(label, "caption"));
        var text = Ui.Text(value); Localization.SetIsUserText(text, true); Grid.SetColumn(text, 1); row.Children.Add(text); _details.Children.Add(row);
    }
}
