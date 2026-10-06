using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaMedia.Core;
using QRCoder;

namespace AvaMedia.Desktop;

internal sealed class WifiReceivedEntry(WifiTransferUpdate update) : Observable
{
    public string Id { get; } = update.Id;
    public string Path { get; } = update.Path;
    public string Name { get; } = update.Name;
    private WifiTransferUpdate _update = update;
    public bool Complete => _update.Complete;
    public bool Receiving => !Complete && _update.Error.Length == 0;
    public double Progress => _update.Total > 0 ? _update.Bytes * 100d / _update.Total : 0;
    public string Status => Complete ? Localization.Format($"已接收 · {ImageCompression.Bytes(_update.Bytes)}") :
        _update.Error.Length > 0 ? Localization.Text(_update.Error) :
        Localization.Format($"接收中 · {ImageCompression.Bytes(_update.Bytes)} / {ImageCompression.Bytes(_update.Total)}");
    public void Update(WifiTransferUpdate value) { _update = value; Refresh(); }
    public void Refresh() => Raise(string.Empty);
}

internal sealed record WifiShareEntry(WifiSharedFile File)
{
    public string Name => File.Name;
    public string Size => ImageCompression.Bytes(File.Bytes);
}

public partial class WifiTransferWindow : Window
{
    private readonly Func<string[], bool, Task> _import;
    private readonly ObservableCollection<WifiReceivedEntry> _received = [];
    private readonly ObservableCollection<WifiShareEntry> _shared = [];
    private readonly Dictionary<string, WifiReceivedEntry> _byId = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private WifiTransferService? _service;
    private Bitmap? _qr;
    private Task _operation = Task.CompletedTask;
    private Task? _shutdown;
    private bool _busy, _importing, _closeReady;

    public WifiTransferWindow() : this((_, _) => Task.CompletedTask) { }
    public WifiTransferWindow(Func<string[], bool, Task> import)
    {
        _import = import;
        InitializeComponent();
        ReceivedList.ItemsSource = _received; SharedList.ItemsSource = _shared;
        Opened += async (_, _) => await RunAsync(InitializeAsync);
        Closing += ClosingWindow;
        Localization.Changed += LanguageChanged;
        Closed += (_, _) => { Localization.Changed -= LanguageChanged; _qr?.Dispose(); _lifetime.Dispose(); };
    }

    private async Task InitializeAsync()
    {
        Directory.CreateDirectory(WifiTransferService.ReceiveFolder);
        var files = await Task.Run(() => Directory.EnumerateDirectories(WifiTransferService.ReceiveFolder)
            .Where(directory => Guid.TryParseExact(System.IO.Path.GetFileName(directory), "N", out _))
            .SelectMany(directory => Directory.EnumerateFiles(directory).Where(path => System.IO.Path.GetFileName(path) != ".uploading"))
            .Select(path => new FileInfo(path)).OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info => new WifiTransferUpdate(System.IO.Path.GetFileName(info.DirectoryName)!, info.FullName,
                info.Name, info.Length, info.Length, Complete: true)).ToArray(), _lifetime.Token);
        foreach (var file in files) ApplyUpdate(file);
        RefreshNetworks();
        await StartAsync();
    }

    private void RefreshNetworks()
    {
        var previous = (NetworkInput.SelectedItem as WifiNetwork)?.Address;
        var networks = WifiTransferService.Networks();
        NetworkInput.ItemsSource = networks;
        NetworkInput.SelectedItem = networks.FirstOrDefault(network => network.Address.Equals(previous)) ?? networks.FirstOrDefault();
        if (networks.Length == 0)
            ConnectionStatus.Text = Localization.Text("未找到局域网 IPv4 地址，请连接 WiFi 或有线网络后刷新。");
        RefreshControls();
    }

    private async Task StartAsync()
    {
        if (_service is not null || _lifetime.IsCancellationRequested) return;
        if (NetworkInput.SelectedItem is not WifiNetwork network)
        { ConnectionStatus.Text = Localization.Text("未找到局域网 IPv4 地址，请连接 WiFi 或有线网络后刷新。"); return; }
        ConnectionStatus.Text = Localization.Text("正在开启接收…");
        var service = new WifiTransferService();
        service.Updated += TransferUpdated;
        try
        {
            foreach (var entry in _shared) service.AddShare(entry.File);
            await service.StartAsync(network.Address, _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            using var code = QRCodeGenerator.GenerateQrCode(service.Url, QRCodeGenerator.ECCLevel.M);
            using var renderer = new PngByteQRCode(code);
            using var stream = new MemoryStream(renderer.GetGraphic(6));
            var bitmap = new Bitmap(stream);
            _qr?.Dispose(); _qr = bitmap; QrImage.Source = _qr;
            _service = service; AddressText.Text = service.Url;
            ConnectionStatus.Text = Localization.Text("接收已开启，手机扫码后用浏览器打开。");
        }
        catch
        {
            service.Updated -= TransferUpdated;
            await service.DisposeAsync();
            throw;
        }
        RefreshControls();
    }

    private async Task StopAsync()
    {
        var service = _service;
        _service = null;
        AddressText.Text = ""; QrImage.Source = null; _qr?.Dispose(); _qr = null;
        RefreshControls();
        if (service is not null)
        {
            try { await service.DisposeAsync(); }
            finally { service.Updated -= TransferUpdated; }
        }
        ConnectionStatus.Text = Localization.Text("接收已停止。再次开启会生成新的扫码地址。");
        RefreshControls();
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _lifetime.IsCancellationRequested) return;
        _busy = true; RefreshControls();
        // The stored task includes error handling, so closing can await all startup and shutdown work.
        _operation = ObserveAsync(action);
        await _operation;
    }

    private async Task ObserveAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ConnectionStatus.Text = Localization.Text("操作失败，请检查网络与接收目录。");
            ActivityText.Text = exception.Message;
        }
        finally { _busy = false; RefreshControls(); }
    }

    private void TransferUpdated(WifiTransferUpdate update) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closeReady) ApplyUpdate(update);
    });

    private void ApplyUpdate(WifiTransferUpdate update)
    {
        if (_byId.TryGetValue(update.Id, out var entry)) entry.Update(update);
        else { entry = new(update); _byId.Add(entry.Id, entry); _received.Insert(0, entry); }
        RefreshControls();
    }

    private void RefreshControls()
    {
        if (StartReceiveButton is null) return;
        var closing = _lifetime.IsCancellationRequested;
        NetworkInput.IsEnabled = RefreshNetworkButton.IsEnabled = !_busy && !closing && _service is null;
        StartReceiveButton.IsEnabled = !_busy && !closing && _service is null && NetworkInput.SelectedItem is WifiNetwork;
        StopReceiveButton.IsEnabled = !_busy && !closing && _service is not null;
        CopyAddressButton.IsEnabled = !closing && _service is not null;
        var selected = SelectedReceived();
        ImportReceivedButton.IsEnabled = !_importing && !closing && selected.Length > 0;
        CompressReceivedButton.IsEnabled = !_importing && !closing && selected.Any(VideoFolderScanner.IsVideoFile);
        SelectReceivedButton.IsEnabled = !closing && _received.Any(entry => entry.Complete);
        AddSharedButton.IsEnabled = !closing;
        RemoveSharedButton.IsEnabled = !closing && SharedList.SelectedItems?.Count > 0;
        ReceiveSummary.Text = Localization.Format($"已接收 {_received.Count(entry => entry.Complete)} 个 · 传输中 {_received.Count(entry => entry.Receiving)} 个");
    }

    private string[] SelectedReceived() => ReceivedList.SelectedItems?.OfType<WifiReceivedEntry>()
        .Where(entry => entry.Complete && File.Exists(entry.Path)).Select(entry => entry.Path).ToArray() ?? [];

    private void LanguageChanged(object? sender, EventArgs args)
    { foreach (var entry in _received) entry.Refresh(); RefreshControls(); }
    private void SelectionChanged(object? sender, SelectionChangedEventArgs args) => RefreshControls();
    private async void StartReceiveClick(object? sender, RoutedEventArgs args) => await RunAsync(StartAsync);
    private async void StopReceiveClick(object? sender, RoutedEventArgs args) => await RunAsync(StopAsync);
    private async void RefreshNetworkClick(object? sender, RoutedEventArgs args) => await RunAsync(() => { RefreshNetworks(); return Task.CompletedTask; });
    private async void CopyAddressClick(object? sender, RoutedEventArgs args)
    {
        try
        {
            if (_service is not null && Clipboard is { } clipboard)
            { await clipboard.SetTextAsync(_service.Url); ActivityText.Text = Localization.Text("扫码地址已复制。"); }
        }
        catch (Exception exception) { ActivityText.Text = exception.Message; }
    }

    private void SelectReceivedClick(object? sender, RoutedEventArgs args)
    {
        ReceivedList.SelectedItems?.Clear();
        foreach (var entry in _received.Where(entry => entry.Complete && File.Exists(entry.Path))) ReceivedList.SelectedItems?.Add(entry);
    }

    private async void ImportReceivedClick(object? sender, RoutedEventArgs args) => await ImportAsync(false);
    private async void CompressReceivedClick(object? sender, RoutedEventArgs args) => await ImportAsync(true);
    private async Task ImportAsync(bool compress)
    {
        if (_importing || _lifetime.IsCancellationRequested) return;
        var files = SelectedReceived();
        if (compress) files = files.Where(VideoFolderScanner.IsVideoFile).ToArray();
        if (files.Length == 0) return;
        _importing = true; RefreshControls();
        try { await _import(files, compress); }
        catch (Exception exception) { if (!_lifetime.IsCancellationRequested) await Ui.Message(this, "导入失败", exception.Message); }
        finally { _importing = false; if (!_closeReady) RefreshControls(); }
    }

    private async void OpenReceiveFolderClick(object? sender, RoutedEventArgs args)
    {
        try { Directory.CreateDirectory(WifiTransferService.ReceiveFolder); PlatformServices.OpenFolder(WifiTransferService.ReceiveFolder); }
        catch (Exception exception) { await Ui.Message(this, "打开目录失败", exception.Message); }
    }

    private async void AddSharedClick(object? sender, RoutedEventArgs args)
    {
        try
        {
            var paths = await Ui.Pick(this, "选择要发送到手机的文件");
            if (_lifetime.IsCancellationRequested) return;
            var known = _shared.Select(entry => entry.File.Path).ToHashSet(VideoFolderScanner.PathComparer);
            foreach (var path in paths.Where(File.Exists).Select(System.IO.Path.GetFullPath).Where(known.Add))
            {
                var file = WifiTransferService.Share(path);
                _shared.Add(new(file)); _service?.AddShare(file);
            }
            ActivityText.Text = Localization.Text(_service is null ? "文件已添加，开启接收后手机即可下载。" : "文件已分享，手机网页可刷新下载列表。");
        }
        catch (Exception exception) { await Ui.Message(this, "分享文件失败", exception.Message); }
    }

    private void RemoveSharedClick(object? sender, RoutedEventArgs args)
    {
        foreach (var entry in SharedList.SelectedItems?.OfType<WifiShareEntry>().ToArray() ?? [])
        { _shared.Remove(entry); _service?.RemoveShare(entry.File.Id); }
        RefreshControls();
    }

    private async void ClosingWindow(object? sender, WindowClosingEventArgs args)
    {
        if (_closeReady) return;
        args.Cancel = true;
        try { await ShutdownAsync(); Close(); }
        catch (Exception exception) { ActivityText.Text = exception.Message; }
    }

    public Task ShutdownAsync() => _shutdown ??= ShutdownCoreAsync();
    private async Task ShutdownCoreAsync()
    {
        _lifetime.Cancel(); RefreshControls();
        await _operation;
        await StopAsync();
        _closeReady = true;
    }
}
