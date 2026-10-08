using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private readonly DownloadSpeedTracker _downloadSpeedTracker = new();
    private readonly double[] _downloadSpeedHistory = new double[60];
    private DownloadSpeedTotals _downloadSpeedTotals;
    private int _downloadSpeedRevision;
    private int _presentedDownloadSpeedRevision = -1;

    private void ResetDownloadSpeedMonitor()
    {
        _downloadSpeedTracker.Reset();
        Array.Clear(_downloadSpeedHistory);
        _downloadSpeedTotals = default;
        _downloadSpeedRevision++;
        PresentDownloadSpeedMonitor();
    }

    private void SampleDownloadSpeedMonitor()
    {
        _downloadSpeedTotals = _downloadSpeedTracker.Sample();
        Array.Copy(_downloadSpeedHistory, 1, _downloadSpeedHistory, 0, _downloadSpeedHistory.Length - 1);
        _downloadSpeedHistory[^1] = _downloadSpeedTotals.Current;
        _downloadSpeedRevision++;
        if (_backgroundWindowVisible) PresentDownloadSpeedMonitor();
    }

    private void PresentDownloadSpeedMonitor()
    {
        if (_presentedDownloadSpeedRevision == _downloadSpeedRevision) return;
        NetworkMonitor.Update(_downloadSpeedTotals, _downloadSpeedHistory);
        _presentedDownloadSpeedRevision = _downloadSpeedRevision;
    }
}
