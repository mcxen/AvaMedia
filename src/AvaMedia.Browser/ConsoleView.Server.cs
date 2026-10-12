using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AvaMedia.Browser;

public sealed partial class ConsoleView
{
    private readonly StackPanel _server = new() { Spacing = 8, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _models = new() { Spacing = 8 };
    private Control BuildServer()
    {
        var scroll = new ScrollViewer { Content = new Border { Child = _server, Padding = new(18, 6, 18, 24) }, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        return scroll;
    }
    private static TextBlock GroupTitle(string text) { var title = Ui.Text(text, "section"); title.Margin = new(4, 12, 0, 2); return title; }
    private static Control Value(string text) { var value = Ui.Text(text, "secondary", raw: true, trim: true); value.FontSize = 13; value.MaxWidth = 380; ToolTip.SetTip(value, text); return value; }
    private void RenderServer()
    {
        _server.Children.Clear();
        var title = Ui.Text("服务器", "largeTitle"); title.Margin = new(4, 4, 0, 4); _server.Children.Add(title);
        _server.Children.Add(Ui.Group(
            Ui.Line("平台", Value(_runtime.Text("platform")), 42),
            Ui.Line("版本", Value("v" + _runtime.Text("version")), 42),
            Ui.Line("输出目录", Value(_runtime.Text("outputFolder")), 42),
            Ui.Line("运行方式", Value(L.T(_runtime.Flag("background") ? "后台服务运行中" : "桌面客户端服务运行中")), 42),
            Ui.Line("NSFW 内容识别", Value(L.T(_runtime.Flag("nsfwEnabled") ? "已开启" : "已关闭")), 42)));
        var resources = _runtime?["resources"];
        var reserve = new VisualNumber { Minimum = 0, Maximum = 80, Value = resources?["cpuReservePercent"]?.GetValue<decimal>() ?? 20, Suffix = "%" };
        var threads = new VisualNumber { Minimum = 1, Maximum = 16, Value = resources?["cpuThreads"]?.GetValue<decimal>() ?? 4 };
        var multithread = new ToggleSwitch { IsChecked = resources.Flag("multiThread") };
        _server.Children.Add(GroupTitle("资源"));
        _server.Children.Add(Ui.Group(Ui.Line("CPU 预留百分比", reserve, 44), Ui.Line("CPU 线程数", threads, 44), Ui.Line("任务内部多线程", multithread, 44)));
        var save = Action("保存资源设置", async () =>
        {
            _runtime = await RemoteApi.Call("set_resources", new() { ["settings"] = new JsonObject {
                ["cpuReservePercent"] = (int)(reserve.Value ?? 20), ["cpuThreads"] = (int)(threads.Value ?? 4), ["multiThread"] = multithread.IsChecked == true } });
            RenderServer(); _status.Text = "服务器资源设置已保存。";
        }, primary: true);
        var refresh = Action("刷新服务器状态", async () => { _runtime = await RemoteApi.Call("get_runtime"); RenderServer(); }, "refresh-cw");
        var models = Action("查看模型状态", async () =>
        {
            var result = await RemoteApi.Call("list_models"); var budget = 1000;
            _models.Children.Clear(); _models.Children.Add(GroupTitle("模型状态"));
            _models.Children.Add(Ui.Card(new Border { Padding = new(12), Child = RenderResult(result, ref budget) }));
        }, "sparkles");
        var buttons = Actions(save, refresh, models); buttons.Margin = new(0, 10, 0, 0);
        _server.Children.Add(buttons);
        _server.Children.Add(new OptionSection("有效资源策略", new StackPanel { Children = { ReadOnly(resources?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "") } }, false));
        _server.Children.Add(_models);
    }
}
