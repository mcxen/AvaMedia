using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace AvaMedia.Desktop.Player;

internal sealed class DiscOpenWindow : Window
{
    private readonly TextBox _path = Ui.Input();
    private readonly ComboBox _kind = Ui.Combo(["蓝光", "DVD"], "蓝光");
    private readonly NumericUpDown _title = new() { Minimum = 0, Maximum = 999, Value = 0, Increment = 1 };
    private readonly TextBlock _error = Ui.Text("", "error");
    public DiscOpenWindow(string? initial = null)
    {
        Title = "打开原盘"; Width = 560; SizeToContent = SizeToContent.Height; CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _path.IsReadOnly = true; Localization.SetIsUserText(_path, true); _path.Text = initial ?? "";
        if (initial is not null && NativePlayerRunner.Detect(initial) is { Kind: DiscKind.Dvd }) _kind.SelectedIndex = 1;
        var content = new StackPanel { Spacing = 8, Margin = new(12) };
        Avalonia.Automation.AutomationProperties.SetName(_path, "原盘目录、ISO 镜像或光驱路径");
        var source = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8 };
        source.Children.Add(_path);
        var folder = Ui.Button("选择目录 / 光驱…", async () =>
        {
            if (await Ui.Folder(this, "选择原盘目录或光驱") is { } path)
            { _path.Text = path; if (NativePlayerRunner.Detect(path) is { } detected) _kind.SelectedIndex = detected.Kind == DiscKind.Bluray ? 0 : 1; }
        }); Grid.SetColumn(folder, 1); source.Children.Add(folder);
        var iso = Ui.Button("选择 ISO…", async () =>
        {
            var selected = await ToolFilePicker.OpenFilesAsync(this, new() { Title = Localization.Text("选择 ISO 镜像"), AllowMultiple = false,
                FileTypeFilter = [new("ISO") { Patterns = ["*.iso"] }] });
            if (selected.FirstOrDefault()?.TryGetLocalPath() is { } path) _path.Text = path;
        }); Grid.SetColumn(iso, 2); source.Children.Add(iso); content.Children.Add(source);
        var title = new Grid { ColumnDefinitions = new("Auto,120,*"), ColumnSpacing = 8 };
        title.Children.Add(Ui.Text("光盘类型")); Grid.SetColumn(_kind, 1); title.Children.Add(_kind);
        var number = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8 };
        number.Children.Add(Ui.Text("标题编号")); Avalonia.Automation.AutomationProperties.SetName(_title, "标题编号（0 为最长标题）");
        var adjusted = Ui.Adjust(_title); ToolTip.SetTip(adjusted, "0 为最长标题"); Grid.SetColumn(adjusted, 1); number.Children.Add(adjusted);
        Grid.SetColumn(number, 2); title.Children.Add(number); content.Children.Add(title);
        content.Children.Add(Ui.Text("播放正片标题；不提供光盘菜单。", "caption")); content.Children.Add(_error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        actions.Children.Add(Ui.DialogButton("取消", () => Close()));
        actions.Children.Add(Ui.DialogButton("播放", () =>
        {
            var path = _path.Text?.Trim() ?? "";
            if (!File.Exists(path) && !Directory.Exists(path) && !(OperatingSystem.IsWindows() && path.StartsWith(@"\\.\", StringComparison.Ordinal)))
            { _error.Text = Localization.Text("请选择存在的原盘目录、ISO 镜像或光驱路径。"); return; }
            if (!decimal.TryParse(_title.Text, System.Globalization.NumberStyles.Number, _title.NumberFormat, out var title) || title < 0 || title > 999 || title != decimal.Truncate(title))
            { _error.Text = Localization.Text("标题编号须为 0 到 999 的整数。"); return; }
            path = Path.GetFullPath(path);
            if (_kind.SelectedIndex == 0 && Directory.Exists(path) && Path.GetFileName(Path.TrimEndingDirectorySeparator(path)).Equals("BDMV", StringComparison.OrdinalIgnoreCase))
                path = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))!;
            Close(new DiscPlayback(path, _kind.SelectedIndex == 0 ? DiscKind.Bluray : DiscKind.Dvd, (int)title));
        }, isDefault: true)); content.Children.Add(actions); Content = content;
    }
}
