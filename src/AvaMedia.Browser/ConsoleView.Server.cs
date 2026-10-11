using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;

namespace AvaMedia.Browser;

public sealed partial class ConsoleView
{
    private readonly StackPanel _server = new() { Spacing = 10, Margin = new(16), MaxWidth = 760, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
    private Control BuildServer() => new ScrollViewer { Content = _server };
    private void RenderServer()
    {
        _server.Children.Clear();
        _server.Children.Add(Label("服务器 · " + _runtime.Text("platform") + " · v" + _runtime.Text("version")));
        _server.Children.Add(PathLabel(_runtime.Text("outputFolder"), "输出目录 · "));
        _server.Children.Add(Label(_runtime.Flag("background") ? "后台服务运行中" : "桌面客户端服务运行中"));
        _server.Children.Add(Label("NSFW 内容识别 · " + (_runtime.Flag("nsfwEnabled") ? "已开启" : "已关闭")));
        var resources = _runtime?["resources"];
        var reserve = new NumericUpDown { Minimum = 0, Maximum = 80, Value = resources?["cpuReservePercent"]?.GetValue<decimal>() ?? 20, Width = 160, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var threads = new NumericUpDown { Minimum = 1, Maximum = 16, Value = resources?["cpuThreads"]?.GetValue<decimal>() ?? 4, Width = 160, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var multithread = new CheckBox { Content = "任务内部多线程", IsChecked = resources.Flag("multiThread") };
        Field(_server, "CPU 预留百分比", reserve); Field(_server, "CPU 线程数", threads); _server.Children.Add(multithread);
        _server.Children.Add(Action("保存资源设置", async () =>
        {
            _runtime = await RemoteApi.Call("set_resources", new() { ["settings"] = new JsonObject {
                ["cpuReservePercent"] = (int)(reserve.Value ?? 20), ["cpuThreads"] = (int)(threads.Value ?? 4), ["multiThread"] = multithread.IsChecked == true } });
            RenderServer(); _status.Text = "服务器资源设置已保存。";
        }));
        _server.Children.Add(new Expander { Header = "有效资源策略", Content = ReadOnly(resources?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "") });
        _server.Children.Add(Action("刷新服务器状态", async () => { _runtime = await RemoteApi.Call("get_runtime"); RenderServer(); }));
        _server.Children.Add(Action("查看模型状态", async () =>
        {
            var models = await RemoteApi.Call("list_models"); var budget = 1000;
            _server.Children.Add(RenderResult(models, ref budget));
        }));
    }
}
