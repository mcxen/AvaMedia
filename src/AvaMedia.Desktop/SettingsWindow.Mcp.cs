using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Layout;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class SettingsWindow
{
    private readonly CheckBox _mcpEnabled = new() { Content = "启用 MCP 服务" };
    private readonly CheckBox _mcpLan = new() { Content = "允许局域网连接" };
    private readonly NumericUpDown _mcpPort = new() { Minimum = 1024, Maximum = 65535, Increment = 1, Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _mcpEndpoint = new() { MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _mcpStatus = Ui.Status("已关闭");
    private Func<string>? _readMcpStatus;
    private readonly DispatcherTimer _mcpStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    internal void SetMcpStatus(Func<string> status) { _readMcpStatus = status; _mcpStatus.Text = status(); }
    private void InitializeMcpSettings()
    {
        var copyAddress = new Button { Content = "复制地址" };
        var copyConfig = new Button { Content = "复制 MCP 配置" };
        copyAddress.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard && _mcpEndpoint.SelectedItem is string endpoint) await clipboard.SetTextAsync(endpoint);
        };
        copyConfig.Click += async (_, _) =>
        {
            if (Clipboard is not { } clipboard || _mcpEndpoint.SelectedItem is not string endpoint) return;
            var config = JsonSerializer.Serialize(new { mcpServers = new { AvaMedia = new { type = "http", url = endpoint } } }, new JsonSerializerOptions { WriteIndented = true });
            await clipboard.SetTextAsync(config);
        };
        _mcpPort.Name = "McpPortInput"; _mcpEnabled.Name = "McpEnabledInput"; _mcpLan.Name = "McpLanInput";
        AutomationProperties.SetName(_mcpPort, "MCP 端口"); AutomationProperties.SetName(_mcpEndpoint, "MCP 连接地址");
        var port = LifecycleField("端口", _mcpPort);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(copyAddress); buttons.Children.Add(copyConfig);
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(Ui.Text("MCP 服务", "settingsHeading"));
        panel.Children.Add(_mcpEnabled); panel.Children.Add(_mcpLan); panel.Children.Add(port);
        panel.Children.Add(new Separator()); panel.Children.Add(Ui.Text("客户端连接", "settingsHeading"));
        panel.Children.Add(LifecycleField("连接地址", _mcpEndpoint)); panel.Children.Add(buttons); panel.Children.Add(_mcpStatus);
        panel.Children.Add(new TextBlock { Text = "无需令牌，可访问本机全部文件与服务。", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "caption" } });
        McpSection.Child = panel;
        foreach (var input in new[] { _mcpEnabled, _mcpLan })
        {
            _values.Add(() => input.IsChecked);
            input.IsCheckedChanged += (_, _) => { MarkDirty(); UpdateMcpAddresses(); };
        }
        _values.Add(() => NumericDraftValue(_mcpPort));
        _mcpPort.PropertyChanged += (_, e) =>
        {
            if (e.Property == NumericUpDown.ValueProperty || e.Property == NumericUpDown.TextProperty) { MarkDirty(); UpdateMcpAddresses(); }
        };
        _mcpStatusTimer.Tick += (_, _) =>
        {
            if (_readMcpStatus is { } status) _mcpStatus.Text = status();
        };
        void ObserveStatus()
        {
            if (IsVisible && SettingsTabs.SelectedItem == ToolsTab && WindowState != WindowState.Minimized) _mcpStatusTimer.Start();
            else _mcpStatusTimer.Stop();
        }
        Opened += (_, _) => ObserveStatus();
        SettingsTabs.SelectionChanged += (_, _) => ObserveStatus();
        PropertyChanged += (_, args) => { if (args.Property == WindowStateProperty || args.Property == IsVisibleProperty) ObserveStatus(); };
        Closed += (_, _) => _mcpStatusTimer.Stop();
    }
    private void PopulateMcp(McpSettings settings)
    {
        _mcpEnabled.IsChecked = settings.Enabled; _mcpLan.IsChecked = settings.AllowLan; _mcpPort.Value = settings.Port;
        UpdateMcpAddresses();
    }
    private McpSettings ReadMcp()
    {
        var settings = new McpSettings { Enabled = _mcpEnabled.IsChecked == true, AllowLan = _mcpLan.IsChecked == true, Port = Number(_mcpPort, "MCP 端口") };
        settings.Validate(); return settings;
    }
    private void UpdateMcpAddresses()
    {
        var port = (int)(_mcpPort.Value ?? 18920); var previous = _mcpEndpoint.SelectedItem as string;
        var addresses = new List<string> { $"http://127.0.0.1:{port}/mcp" };
        if (_mcpLan.IsChecked == true)
        {
            try
            {
                addresses.AddRange(NetworkInterface.GetAllNetworkInterfaces().Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                    .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(address.Address))
                    .Select(address => $"http://{address.Address}:{port}/mcp").Distinct());
            }
            catch (NetworkInformationException) { }
        }
        _mcpEndpoint.ItemsSource = addresses; _mcpEndpoint.SelectedItem = previous is not null && addresses.Contains(previous) ? previous : addresses[0];
    }
}
