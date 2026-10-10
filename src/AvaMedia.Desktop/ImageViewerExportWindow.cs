using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

/// <summary>Explicit selection and destination, with cancellable, atomic non-overwriting exports.</summary>
internal sealed class ImageViewerExportWindow : Window
{
    private readonly ImageViewerEntry[] _entries;
    private readonly List<CheckBox> _selected = [];
    private readonly ComboBox _format = new() { ItemsSource = new[] { "PNG", "JPEG", "WebP", "TIFF", "BMP", "AVIF" }, SelectedIndex = 0 };
    private readonly NumericUpDown _quality = new() { Minimum = 1, Maximum = 100, Value = 90, Increment = 1 };
    private readonly NumericUpDown _size = new() { Minimum = 0, Maximum = 32768, Value = 0, Increment = 100 };
    private readonly CheckBox _transform = new() { Content = "应用当前旋转与镜像", IsChecked = true };
    private readonly CheckBox _strip = new() { Content = "移除 EXIF / GPS 信息" };
    private readonly TextBox _folder = Ui.Input("");
    private readonly TextBlock _status = Ui.Text("", "caption");
    private readonly TextBlock _errors = Ui.Text("", "caption");
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 5 };
    private readonly Button _start = Ui.Button("开始转换", () => { });
    private readonly Button _cancel = Ui.Button("取消", () => { });
    private readonly StackPanel _settings = new() { Spacing = 8 };
    private readonly int _rotation;
    private readonly bool _flip;
    private CancellationTokenSource? _operation;
    private bool _running, _closeRequested;
    public ImageViewerExportWindow(ImageViewerEntry[] entries, int current, int rotation, bool flip)
    {
        _entries = entries; _rotation = rotation; _flip = flip;
        Title = "批量转换图片"; Width = 720; Height = 590; MinWidth = 620; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowArtwork.SetKind(this, "image");
        var root = new Grid { RowDefinitions = new("*,Auto,Auto"), RowSpacing = 10, Margin = new(16) };
        var body = new Grid { ColumnDefinitions = new("*,250"), ColumnSpacing = 12 };
        var selection = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 6 };
        var buttons = new WrapPanel();
        foreach (var (label, predicate) in new (string, Func<int, bool>)[] { ("全部", _ => true), ("当前图片", index => index == current), ("取消全选", _ => false) })
        {
            var button = Ui.Button(label, () => { for (var i = 0; i < _selected.Count; i++) _selected[i].IsChecked = predicate(i); });
            button.Margin = new(0, 0, 6, 0); buttons.Children.Add(button);
        }
        selection.Children.Add(buttons); var list = new StackPanel { Spacing = 6 };
        for (var index = 0; index < entries.Length; index++)
        {
            var check = new CheckBox { Content = entries[index].Name, IsChecked = index == current };
            Localization.SetIsUserText(check, true); _selected.Add(check); list.Children.Add(check);
        }
        var scroll = new ScrollViewer { Content = list }; Grid.SetRow(scroll, 1); selection.Children.Add(scroll); body.Children.Add(selection);
        _settings.Children.Add(Ui.Text("输出格式", "settingsHeading")); _settings.Children.Add(_format);
        _settings.Children.Add(Ui.Text("质量", "caption")); _settings.Children.Add(Ui.Adjust(_quality));
        _settings.Children.Add(Ui.Text("最长边像素，0 保持原尺寸", "caption")); _settings.Children.Add(Ui.Adjust(_size));
        _settings.Children.Add(_transform); _settings.Children.Add(_strip);
        _settings.Children.Add(Ui.Text("保存到", "settingsHeading")); Localization.SetIsUserText(_folder, true); _settings.Children.Add(_folder);
        _settings.Children.Add(Ui.Button("选择文件夹…", async () => { if (await Ui.Folder(this, "转换图片保存到") is { } folder) _folder.Text = folder; }));
        _settings.Children.Add(Ui.Text("动画导出当前文件的首帧", "caption"));
        Grid.SetColumn(_settings, 1); body.Children.Add(_settings); root.Children.Add(body);
        var progress = new StackPanel { Spacing = 5 }; progress.Children.Add(_progress); progress.Children.Add(_status);
        progress.Children.Add(new ScrollViewer { Content = _errors, MaxHeight = 70 }); Grid.SetRow(progress, 1); root.Children.Add(progress);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { _cancel, _start } };
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        _start.Click += async (_, _) => await RunAsync();
        _cancel.Click += (_, _) => { if (_running) _operation?.Cancel(); else Close(); };
        Closing += (_, args) => { if (_running) { args.Cancel = true; _closeRequested = true; _operation?.Cancel(); } };
    }
    private async Task RunAsync()
    {
        try { await RunCoreAsync(); }
        catch (Exception error) { await Ui.Message(this, "批量转换图片", error.Message); }
    }
    private async Task RunCoreAsync()
    {
        if (_running) return;
        var inputs = _entries.Where((_, index) => _selected[index].IsChecked == true).ToArray();
        if (inputs.Length == 0 || string.IsNullOrWhiteSpace(_folder.Text))
        { await Ui.Message(this, "批量转换图片", "请选择图片和保存文件夹。"); return; }
        ToolInputs.CommitNumber(_quality, integer: true); ToolInputs.CommitNumber(_size, integer: true);
        var folder = Path.GetFullPath(_folder.Text); var format = _format.SelectedIndex switch
        { 1 => "jpg", 2 => "webp", 3 => "tiff", 4 => "bmp", 5 => "avif", _ => "png" };
        var options = new ImageEncodingOptions(format, (int)(_quality.Value ?? 90), (int)(_size.Value ?? 0),
            _transform.IsChecked == true ? _rotation : 0, _transform.IsChecked == true && _flip, _strip.IsChecked == true);
        _running = true; _settings.IsEnabled = _start.IsEnabled = false; foreach (var item in _selected) item.IsEnabled = false;
        _errors.Text = ""; _progress.Value = 0; using var operation = new CancellationTokenSource(); _operation = operation;
        var done = 0; var errors = new List<string>();
        try
        {
            Directory.CreateDirectory(folder); var used = new HashSet<string>(inputs.Select(entry => entry.Container), BatchRename.PathComparer);
            for (var index = 0; index < inputs.Length; index++)
            {
                operation.Token.ThrowIfCancellationRequested(); var entry = inputs[index];
                _status.Text = $"{index + 1} / {inputs.Length} · {entry.Name}";
                try
                {
                    var name = Path.GetFileNameWithoutExtension(entry.Name.Replace('\\', '/').Split('/')[^1]);
                    var output = MediaEngine.UniqueOutput(folder, name + "_converted", format, used); used.Add(output);
                    await ImageCodec.ExportAsync(entry, output, options, operation.Token); done++;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { errors.Add(entry.Name + " · " + error.Message); _errors.Text = string.Join(Environment.NewLine, errors); }
                _progress.Value = (index + 1) * 100d / inputs.Length;
            }
            _status.Text = Localization.Format($"完成 {done} 个，失败 {errors.Count} 个");
        }
        catch (OperationCanceledException) { _status.Text = Localization.Text("已停止") + $" · {done} / {inputs.Length}"; }
        catch (Exception error) { _status.Text = error.Message; }
        finally
        {
            _operation = null; _running = false; _settings.IsEnabled = _start.IsEnabled = true;
            foreach (var item in _selected) item.IsEnabled = true;
            if (_closeRequested) Close();
        }
    }
}
