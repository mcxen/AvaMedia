using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private MenuItem[] _standardMenus = [];
    private object[] _fileCommands = [];
    private object[] _batchCommands = [];
    private bool _platinumMenus;
    private readonly MenuItem _editMenu = new() { Header = "编辑" };
    private readonly MenuItem _viewMenu = new() { Header = "显示" };
    private readonly MenuItem _windowMenu = new() { Header = "窗口" };

    private void InitializePlatinumPresentation()
    {
        _standardMenus = ApplicationMenu.Items.OfType<MenuItem>().ToArray();
        _fileCommands = _standardMenus[0].Items.Cast<object>().ToArray();
        _batchCommands = _standardMenus[4].Items.Cast<object>().ToArray();
        var zoom = new MenuItem { Header = "缩放窗口" };
        zoom.Click += (_, _) => Skin.Zoom(this);
        var shade = new MenuItem { Header = "收起 / 展开标题栏" };
        shade.Click += (_, _) => Skin.ToggleShade(this);
        var minimize = new MenuItem { Header = "最小化窗口" };
        minimize.Click += (_, _) => WindowState = WindowState.Minimized;
        _windowMenu.Items.Add(zoom); _windowMenu.Items.Add(shade); _windowMenu.Items.Add(new Separator()); _windowMenu.Items.Add(minimize);
        ActualThemeVariantChanged += PlatinumPresentationChanged;
        Opened += PlatinumPresentationChanged;
        Closed += (_, _) => { ActualThemeVariantChanged -= PlatinumPresentationChanged; Opened -= PlatinumPresentationChanged; };
        PlatinumPresentationChanged(this, EventArgs.Empty);
    }

    private void PlatinumPresentationChanged(object? sender, EventArgs args)
    {
        RefreshFeatureMetrics();
        var enabled = ActualThemeVariant == Skin.MacOS9;
        if (enabled == _platinumMenus) return;
        ApplicationMenu.Close();
        ApplicationMenu.Items.Clear();
        _standardMenus[0].Items.Clear(); _standardMenus[4].Items.Clear();
        _editMenu.Items.Clear(); _viewMenu.Items.Clear();
        if (enabled)
        {
            _standardMenus[0].Header = Localization.Text("文件");
            _standardMenus[4].Header = Localization.Text("转换");
            _standardMenus[3].Header = Localization.Text("偏好设置…");
            for (var index = 0; index < _fileCommands.Length; index++)
                if (index is not (1 or 2)) _standardMenus[0].Items.Add(_fileCommands[index]);
            _standardMenus[4].Items.Add(_fileCommands[1]); _standardMenus[4].Items.Add(_fileCommands[2]);
            _standardMenus[4].Items.Add(new Separator());
            foreach (var command in _batchCommands) _standardMenus[4].Items.Add(command);
            _editMenu.Items.Add(_standardMenus[3]);
            _viewMenu.Items.Add(_standardMenus[1]); _viewMenu.Items.Add(_standardMenus[2]);
            ApplicationMenu.Items.Add(_standardMenus[0]); ApplicationMenu.Items.Add(_editMenu); ApplicationMenu.Items.Add(_viewMenu);
            ApplicationMenu.Items.Add(_standardMenus[4]); ApplicationMenu.Items.Add(_windowMenu); ApplicationMenu.Items.Add(_standardMenus[5]);
        }
        else
        {
            _standardMenus[0].Header = Localization.Text("任务");
            _standardMenus[4].Header = Localization.Text("批量");
            _standardMenus[3].Header = Localization.Text("选项");
            foreach (var command in _fileCommands) _standardMenus[0].Items.Add(command);
            foreach (var command in _batchCommands) _standardMenus[4].Items.Add(command);
            foreach (var menu in _standardMenus) ApplicationMenu.Items.Add(menu);
        }
        _platinumMenus = enabled;
    }
}
