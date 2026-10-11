using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

public sealed class DiscSourcePicker : UserControl
{
    private sealed record Source(string Path, string Label);
    private readonly ComboBox _drives = new() { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 160, MaxWidth = 280 };
    private readonly TextBlock _status = Ui.Text("", "caption");
    public event Action? Changed;
    public string? Path => (_drives.SelectedItem as Source)?.Path;

    public DiscSourcePicker(string? initial = null)
    {
        _drives.SelectionChanged += (_,_) => Changed?.Invoke();
        var root = new StackPanel { Spacing = 8 };
        var driveRow = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        driveRow.Children.Add(Ui.Text("选择光盘")); Grid.SetColumn(_drives, 1); _drives.HorizontalAlignment = HorizontalAlignment.Stretch; driveRow.Children.Add(_drives);
        _drives.ItemTemplate = new FuncDataTemplate<Source>((drive, _) =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new FeatureIcon { Kind = "disc", Width = 24, Height = 24 }); row.Children.Add(Ui.Text(drive?.Label ?? "")); return row;
        });
        var refresh = Ui.Button("刷新光驱", () => Refresh(initial)); Grid.SetColumn(refresh, 2); driveRow.Children.Add(refresh);
        var mounted = Ui.Button("选择已挂载的光盘…", async () =>
        {
            if (Avalonia.Controls.TopLevel.GetTopLevel(this) is not Window owner || await Ui.Folder(owner, "选择已挂载的光盘") is not { } folder) return;
            try
            {
                var device = System.IO.Path.GetPathRoot(folder)!;
                if (OperatingSystem.IsMacOS())
                {
                    var result = await ProcessRunner.Run("/usr/sbin/diskutil", ["info", "-plist", folder], CancellationToken.None);
                    if (result.ExitCode != 0) throw new IOException(result.Error);
                    var document = XDocument.Parse(result.Output);
                    string Value(string key) => document.Descendants("key").FirstOrDefault(node => node.Value == key)?.ElementsAfterSelf().FirstOrDefault()?.Value ?? "";
                    var identifier = Value("ParentWholeDisk"); if (identifier.Length == 0) identifier = Value("DeviceIdentifier");
                    if (!System.Text.RegularExpressions.Regex.IsMatch(identifier, "^disk[0-9]+$")) throw new IOException("无法读取光盘设备。");
                    var optical = Value("OpticalDeviceType"); var media = Value("MediaName");
                    if (optical.Length == 0 && !media.Contains("DVD", StringComparison.OrdinalIgnoreCase) && !media.Contains("CD", StringComparison.OrdinalIgnoreCase) && !media.Contains("BD", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("请选择已挂载的光盘。");
                    device = "/dev/r" + identifier;
                }
                else if (!OperatingSystem.IsWindows() || new DriveInfo(device).DriveType != DriveType.CDRom) throw new ArgumentException("请选择光驱中的光盘。");
                else device = device[..2];
                var source = new Source(device, System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(folder)));
                _drives.ItemsSource = (_drives.ItemsSource?.OfType<Source>() ?? []).Where(item => item.Path != source.Path).Append(source).ToArray(); _drives.SelectedItem = source; _status.Text = "";
            }
            catch (Exception error) { _status.Text = error.Message; }
        }); Grid.SetColumn(mounted, 3); driveRow.Children.Add(mounted); root.Children.Add(driveRow); root.Children.Add(_status); Content = root; Refresh(initial);
    }

    private void Refresh(string? initial)
    {
        var selected = Path ?? initial; var sources = new List<Source>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try { if (OperatingSystem.IsWindows() && drive.DriveType == DriveType.CDRom && drive.IsReady) sources.Add(new(drive.Name[..2], drive.VolumeLabel.Length > 0 ? drive.VolumeLabel : drive.Name)); }
            catch (IOException) { }
        }
        if (!string.IsNullOrWhiteSpace(selected) && sources.All(source => source.Path != selected)) sources.Add(new(selected, Localization.Text("已选择的光盘")));
        _drives.ItemsSource = sources; _drives.SelectedItem = sources.FirstOrDefault(source => source.Path == selected) ?? sources.FirstOrDefault();
        _status.Text = sources.Count == 0 ? Localization.Text("插入光盘后刷新，或选择已挂载的光盘。") : "";
    }
}
