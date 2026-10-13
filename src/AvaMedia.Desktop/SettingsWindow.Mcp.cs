using Avalonia.Markup.Xaml.MarkupExtensions;
using System.Net.NetworkInformation;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Layout;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;
using AvaMedia.Mcp;

namespace AvaMedia.Desktop;

public sealed partial class SettingsWindow
{
    private readonly ToggleSwitch _mcpEnabled = new() { OnContent = null, OffContent = null };
    private readonly ToggleSwitch _mcpLan = new() { OnContent = null, OffContent = null };
    private readonly NumericUpDown _mcpPort = new() { Minimum = 1024, Maximum = 65535, Increment = 1, [!WidthProperty] = new DynamicResourceExtension("StPortWidth"), HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ComboBox _mcpEndpoint = new() { [!WidthProperty] = new DynamicResourceExtension("StEndpointWidth"), HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBox _mcpAddress = new() { IsReadOnly = true, [!WidthProperty] = new DynamicResourceExtension("StEndpointWidth"), HorizontalAlignment = HorizontalAlignment.Right };
    private readonly AvaMedia.Controls.StatusPill _mcpStatusPill = new();
    private readonly StackPanel _mcpClients = new();
    private Func<string>? _readMcpStatus;
    private Func<McpClientView[]>? _readMcpClients;
    private string _mcpClientSignature = "";
    private readonly DispatcherTimer _mcpStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    internal void SetMcpStatus(Func<string> status) { _readMcpStatus = status; RefreshMcpStatus(); }
    internal void SetMcpClients(Func<McpClientView[]> clients) { _readMcpClients = clients; RefreshMcpStatus(); }

    private void InitializeMcpSettings()
    {
        var copyAddress = new Button { Content = "复制", Classes = { "ac", "small" } };
        var copyConfig = new Button { Content = "复制配置", Classes = { "ac", "small" } };
        var openWeb = new Button { Content = "打开", Classes = { "ac", "small" } };
        var copyWeb = new Button { Content = "复制", Classes = { "ac", "small" } };
        string? WebAddress() => _mcpEndpoint.SelectedItem as string;
        string? McpAddress() => WebAddress() is { } web ? web + "mcp" : null;
        openWeb.Click += (_, _) =>
        {
            if (WebAddress() is not { } url) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception error) { _mcpStatusPill.Text = error.Message; _mcpStatusPill.State = "failed"; }
        };
        copyWeb.Click += async (_, _) => { if (Clipboard is { } clipboard && WebAddress() is { } url) await clipboard.SetTextAsync(url); };
        copyAddress.Click += async (_, _) => { if (Clipboard is { } clipboard && McpAddress() is { } endpoint) await clipboard.SetTextAsync(endpoint); };
        copyConfig.Click += async (_, _) =>
        {
            if (Clipboard is not { } clipboard || McpAddress() is not { } endpoint) return;
            var config = JsonSerializer.Serialize(new { mcpServers = new { AvaMedia = new { type = "http", url = endpoint } } }, new JsonSerializerOptions { WriteIndented = true });
            await clipboard.SetTextAsync(config);
        };
        _mcpEndpoint.SelectionChanged += (_, _) => _mcpAddress.Text = McpAddress() ?? "";
        _mcpPort.Name = "McpPortInput"; _mcpEnabled.Name = "McpEnabledInput"; _mcpLan.Name = "McpLanInput";
        AutomationProperties.SetName(_mcpEnabled, "启用远程管理服务"); AutomationProperties.SetName(_mcpLan, "允许局域网连接");
        AutomationProperties.SetName(_mcpPort, "MCP 端口"); AutomationProperties.SetName(_mcpEndpoint, "网页连接地址");
        AutomationProperties.SetName(_mcpAddress, "MCP 连接地址");

        _mcpEndpoint.Classes.Add("ac"); _mcpAddress.Classes.Add("ac"); _mcpPort.Classes.Add("ac");
        var panel = new StackPanel();
        var portRow = Row("端口", "1024–65535", _mcpPort); portRow.Bind(MarginProperty, new DynamicResourceExtension("StChildRowMargin"));
        var lanRow = Row("允许局域网连接", "同一网络中的设备可管理本机文件与任务", _mcpLan); lanRow.Bind(MarginProperty, new DynamicResourceExtension("StChildRowMargin"));
        var warning = new Border { Classes = { "ac-notice", "warning" }, [!MarginProperty] = new DynamicResourceExtension("StWarningMargin"), [!Border.PaddingProperty] = new DynamicResourceExtension("StWarningPadding"),
            Child = new TextBlock { Text = "服务不要求令牌，只在可信网络中开启。", [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("UiFontCaption"), TextWrapping = TextWrapping.Wrap } };
        warning.Child.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AcBannerText"));
        void UpdateDependencies() { portRow.IsEnabled = lanRow.IsEnabled = _mcpEnabled.IsChecked == true; warning.IsVisible = _mcpLan.IsChecked == true; }
        _mcpEnabled.IsCheckedChanged += (_, _) => UpdateDependencies(); _mcpLan.IsCheckedChanged += (_, _) => UpdateDependencies(); UpdateDependencies();
        panel.Children.Add(GroupTitle("服务"));
        panel.Children.Add(Group(Row("启用远程管理服务", "网页与 MCP 共用当前任务队列", _mcpStatusPill, _mcpEnabled), portRow, lanRow, warning));
        panel.Children.Add(GroupTitle("连接地址"));
        panel.Children.Add(Group(Row("网页", null, _mcpEndpoint, copyWeb, openWeb), Row("MCP", null, _mcpAddress, copyAddress, copyConfig)));
        panel.Children.Add(GroupTitle("客户端")); panel.Children.Add(Group(_mcpClients));
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
        _mcpStatusTimer.Tick += (_, _) => RefreshMcpStatus();
        void ObserveStatus()
        {
            if (IsVisible && SettingsTabs.SelectedItem == RemoteTab && WindowState != WindowState.Minimized) { RefreshMcpStatus(); _mcpStatusTimer.Start(); }
            else _mcpStatusTimer.Stop();
        }
        Opened += (_, _) => ObserveStatus();
        SettingsTabs.SelectionChanged += (_, _) => ObserveStatus();
        PropertyChanged += (_, args) => { if (args.Property == WindowStateProperty || args.Property == IsVisibleProperty) ObserveStatus(); };
        Closed += (_, _) => _mcpStatusTimer.Stop();
        RefreshMcpStatus();
    }

    private Border Group(params Control[] rows)
    {
        var stack = new StackPanel();
        for (var index = 0; index < rows.Length; index++)
        { if (index > 0 && rows[index] is Grid) stack.Children.Add(Hairline()); stack.Children.Add(rows[index]); }
        return new Border { Child = stack, Classes = { "ac-group" } };
    }
    private Border Hairline()
    {
        var line = new Border { [!HeightProperty] = new DynamicResourceExtension("StHairlineHeight"), [!MarginProperty] = new DynamicResourceExtension("StHairlineMargin") };
        line.Bind(Border.BackgroundProperty, this.GetResourceObservable("AcStageLineBrush")); return line;
    }
    private TextBlock GroupTitle(string text) => new() { Text = text, Classes = { "settingsHeading" } };
    private TextBlock Secondary(TextBlock text) { text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("UiTextSecondary")); return text; }
    private Grid Row(string? label, string? caption, params Control[] trailing)
    {
        var grid = new Grid { ColumnDefinitions = new("*,Auto"), [!Grid.ColumnSpacingProperty] = new DynamicResourceExtension("StPropertyGap"), [!Grid.MinHeightProperty] = new DynamicResourceExtension("UiSettingRowHeight") };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, [!StackPanel.SpacingProperty] = new DynamicResourceExtension("StTextGap"), [!MarginProperty] = new DynamicResourceExtension("StRowLabelMargin") };
        if (label is not null) text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (caption is not null) text.Children.Add(Secondary(new TextBlock { Text = caption, [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("StDescriptionFont"), TextWrapping = TextWrapping.Wrap }));
        grid.Children.Add(text);
        var right = new StackPanel { Orientation = Orientation.Horizontal, [!StackPanel.SpacingProperty] = new DynamicResourceExtension("StControlGap"), VerticalAlignment = VerticalAlignment.Center, [!MarginProperty] = new DynamicResourceExtension("StRowControlMargin") };
        foreach (var control in trailing) right.Children.Add(control);
        Grid.SetColumn(right, 1); grid.Children.Add(right); return grid;
    }

    private void RefreshMcpStatus()
    {
        var status = _readMcpStatus?.Invoke() ?? "已关闭";
        var clients = _readMcpClients?.Invoke() ?? [];
        var running = status.StartsWith("运行中", StringComparison.Ordinal);
        var failed = status.StartsWith("启动失败", StringComparison.Ordinal);
        // Only peers seen within the last five minutes count as connected; stateless HTTP has no sessions.
        var active = clients.Count(client => DateTime.UtcNow - client.LastSeenUtc < TimeSpan.FromMinutes(5));
        var statusText = Localization.Text(status);
        _mcpStatusPill.Text = running && active > 0 ? Localization.Format($"{statusText} · {active} 个客户端") : statusText;
        _mcpStatusPill.State = running ? "completed" : failed ? "failed" : "waiting";
        var signature = string.Join("|", clients.Select(client => $"{client.Address}{client.Kind}{client.Agent}{Relative(client.LastSeenUtc)}"));
        if (signature == _mcpClientSignature && _mcpClients.Children.Count > 0) return;
        _mcpClientSignature = signature; _mcpClients.Children.Clear();
        if (clients.Length == 0)
        {
            _mcpClients.Children.Add(Row(null, Localization.Text(running ? "尚无客户端连接。网页或 MCP 客户端访问后显示在这里。" : "服务未运行。")));
            return;
        }
        foreach (var (client, index) in clients.Take(8).Select((client, index) => (client, index)))
        {
            if (index > 0) _mcpClients.Children.Add(Hairline());
            var icon = new LucideIcon { Kind = client.Kind == "MCP" ? "cpu" : "wifi", [!WidthProperty] = new DynamicResourceExtension("StSwatchSize"), [!HeightProperty] = new DynamicResourceExtension("StSwatchSize"), VerticalAlignment = VerticalAlignment.Center }; icon.Bind(LucideIcon.ForegroundProperty, this.GetResourceObservable("UiText"));
            var row = Row(null, null);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            text.Children.Add(new TextBlock { Text = (client.Kind == "MCP" ? "MCP" : Localization.Text("网页")) + " · " + Describe(client) });
            text.Children.Add(Secondary(new TextBlock { Text = Localization.Format($"{client.Address} · {Relative(client.LastSeenUtc)} · {client.Requests} 次请求"), [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("UiFontCaption") }));
            row.Children.Clear();
            row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, [!StackPanel.SpacingProperty] = new DynamicResourceExtension("StRowGap"), Children = { icon, text } });
            _mcpClients.Children.Add(row);
        }
    }
    /// <summary>Derived only from the User-Agent the client sent.</summary>
    internal static string Describe(McpClientView client)
    {
        var agent = client.Agent;
        if (agent.Length == 0) return Localization.Text("未知客户端");
        if (client.Kind == "Web")
        {
            var device = agent.Contains("iPhone") ? "iPhone" : agent.Contains("iPad") ? "iPad" : agent.Contains("Android") ? "Android"
                : agent.Contains("Mac OS X") ? "Mac" : agent.Contains("Windows") ? "Windows" : agent.Contains("Linux") ? "Linux" : Localization.Text("浏览器");
            var browser = agent.Contains("Edg/") ? "Edge" : agent.Contains("Chrome/") ? "Chrome" : agent.Contains("Firefox/") ? "Firefox" : agent.Contains("Safari/") ? "Safari" : "";
            return browser.Length > 0 ? $"{device} · {browser}" : device;
        }
        var product = agent.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return product.Length > 48 ? product[..48] : product;
    }
    private static string Relative(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        return age < TimeSpan.FromSeconds(30) ? Localization.Text("刚刚") : age < TimeSpan.FromHours(1) ? Localization.Format($"{(int)Math.Max(1, age.TotalMinutes)} 分钟前") : utc.ToLocalTime().ToString("HH:mm");
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
        var addresses = new List<string> { $"http://127.0.0.1:{port}/" };
        if (_mcpLan.IsChecked == true)
        {
            try
            {
                addresses.InsertRange(0, NetworkInterface.GetAllNetworkInterfaces().Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                    .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(address.Address))
                    .Select(address => $"http://{address.Address}:{port}/").Distinct());
            }
            catch (NetworkInformationException) { }
        }
        _mcpEndpoint.ItemsSource = addresses; _mcpEndpoint.SelectedItem = previous is not null && addresses.Contains(previous) ? previous : addresses[0];
        _mcpAddress.Text = _mcpEndpoint.SelectedItem is string web ? web + "mcp" : "";
    }
}
