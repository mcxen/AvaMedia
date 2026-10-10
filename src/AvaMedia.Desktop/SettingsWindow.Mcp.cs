using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class SettingsWindow
{
    private readonly CheckBox _mcpEnabled = new() { Content = "启用 MCP 服务" };
    private readonly CheckBox _mcpLan = new() { Content = "允许局域网连接" };
    private readonly NumericUpDown _mcpPort = new() { Minimum = 1024, Maximum = 65535, Increment = 1, Width = 120 };
    private readonly ComboBox _mcpEndpoint = new() { MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _mcpStatus = new() { Text = "已关闭", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
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
        var port = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        port.Children.Add(new TextBlock { Text = "端口", VerticalAlignment = VerticalAlignment.Center }); port.Children.Add(_mcpPort);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(copyAddress); buttons.Children.Add(copyConfig);
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(_mcpEnabled); panel.Children.Add(_mcpLan); panel.Children.Add(port); panel.Children.Add(_mcpStatus);
        panel.Children.Add(_mcpEndpoint); panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock { Text = "无需令牌，可访问本机全部文件与服务。", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "caption" } });
        var border = new Border { Child = panel, Classes = { "settingsPage" } };
        SettingsTabs.Items.Add(new TabItem { Header = "MCP", Content = border });
        foreach (var input in new[] { _mcpEnabled, _mcpLan })
        {
            _values.Add(() => input.IsChecked);
            input.IsCheckedChanged += (_, _) => { MarkDirty(); UpdateMcpAddresses(); };
        }
        _values.Add(() => _mcpPort.Text);
        _mcpPort.PropertyChanged += (_, e) =>
        {
            if (e.Property == NumericUpDown.ValueProperty || e.Property == NumericUpDown.TextProperty) { MarkDirty(); UpdateMcpAddresses(); }
        };
        _mcpStatusTimer.Tick += (_, _) =>
        {
            if (_readMcpStatus is { } status) _mcpStatus.Text = status();
        };
        Opened += (_, _) => _mcpStatusTimer.Start();
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
