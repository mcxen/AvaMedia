using Avalonia.Controls;

namespace AvaMedia.Desktop;

public sealed record TrayTaskState(string Summary, bool CanStart, bool CanStop, bool HasResult, bool IsBackground);
public sealed record TrayTaskActions(Action Start, Action Stop, Action OpenOutput, Action ShowResult, Action Background);

public interface IBackgroundTaskTray
{
    void SetTaskActions(TrayTaskActions actions);
    void UpdateTaskState(TrayTaskState state);
}

public sealed partial class AppOptionsServices : IBackgroundTaskTray
{
    private TrayTaskActions? _taskActions;
    private TrayTaskState _taskState = new("就绪", false, false, false, false);
    private NativeMenuItem? _trayRestore, _trayExit, _traySummary, _trayStart, _trayStop, _trayOutput, _trayResult, _trayBackground;
    private Func<string, string> _trayTranslate = text => text;
    private string _trayCaption = "AvaMedia";

    private NativeMenu CreateTaskTrayMenu(Action restore, Action exit)
    {
        NativeMenuItem Item(string label, Action action)
        { var item = new NativeMenuItem(label); item.Click += (_, _) => action(); return item; }
        _traySummary = new NativeMenuItem(_taskState.Summary) { IsEnabled = false };
        _trayRestore = Item("显示 AvaMedia", restore);
        _trayBackground = Item("转入后台", () => _taskActions?.Background());
        _trayStart = Item("开始等待任务", () => _taskActions?.Start());
        _trayStop = Item("停止当前任务", () => _taskActions?.Stop());
        _trayOutput = Item("打开输出文件夹", () => _taskActions?.OpenOutput());
        _trayResult = Item("查看上次结果", () => _taskActions?.ShowResult());
        _trayExit = Item("退出", exit);
        var menu = new NativeMenu();
        menu.Items.Add(_traySummary); menu.Items.Add(_trayRestore); menu.Items.Add(_trayBackground);
        menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(_trayStart); menu.Items.Add(_trayStop);
        menu.Items.Add(_trayOutput); menu.Items.Add(_trayResult); menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(_trayExit);
        RefreshTaskTray(); return menu;
    }

    public void SetTaskActions(TrayTaskActions actions)
    { _taskActions = actions; RefreshTaskTray(); }
    public void UpdateTaskState(TrayTaskState state)
    { if (_taskState == state) return; _taskState = state; RefreshTaskTray(); }
    private void UpdateBackgroundLanguage(Func<string, string> translate, string caption)
    { _trayTranslate = translate; _trayCaption = caption; RefreshTaskTray(); }
    private void RefreshTaskTray()
    {
        if (_traySummary is null) return;
        _traySummary.Header = _trayTranslate(_taskState.Summary);
        _trayRestore!.Header = _trayTranslate("显示 AvaMedia").Replace("AvaMedia", _trayCaption, StringComparison.Ordinal);
        _trayBackground!.Header = _trayTranslate("转入后台"); _trayBackground.IsEnabled = _taskActions is not null && !_taskState.IsBackground;
        _trayStart!.Header = _trayTranslate("开始等待任务"); _trayStart.IsEnabled = _taskActions is not null && _taskState.CanStart;
        _trayStop!.Header = _trayTranslate("停止当前任务"); _trayStop.IsEnabled = _taskActions is not null && _taskState.CanStop;
        _trayOutput!.Header = _trayTranslate("打开输出文件夹"); _trayOutput.IsEnabled = _taskActions is not null;
        _trayResult!.Header = _trayTranslate("查看上次结果"); _trayResult.IsEnabled = _taskActions is not null && _taskState.HasResult;
        _trayExit!.Header = _trayTranslate("退出");
        if (_tray is not null) _tray.ToolTipText = _trayCaption + " — " + _trayTranslate(_taskState.Summary);
    }
}
