using Avalonia.Interactivity;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private WifiTransferWindow? _wifiTransferWindow;

    private async void WifiTransferClick(object? sender, RoutedEventArgs args)
    {
        if (_closing) return;
        if (_wifiTransferWindow is { } existing)
        {
            Controls.WindowChrome.Present(existing); return;
        }
        try
        {
            var window = new WifiTransferWindow(ImportWifiFilesAsync);
            _wifiTransferWindow = window;
            window.Closed += (_, _) => _wifiTransferWindow = null;
            Controls.WindowChrome.ShowIndependent(window);
        }
        catch (Exception exception) { await Ui.Message(this, "WiFi 传文件", exception.Message); }
    }

    private async Task ImportWifiFilesAsync(string[] files, bool compressVideo)
    {
        if (_closing) return;
        if (compressVideo)
            await ConfigureVideoCompressionAsync(files, new()
            {
                Mode = VideoCompressionMode.Quality, Quality = 18, Codec = "hevc", Format = SourceOutput.Original,
                MaxDimension = 0, MaxFrameRate = 0, Speed = VideoEncodingSpeed.Slow, AudioBitrate = 192
            });
        else await RouteFilesAsync(files);
    }
}
