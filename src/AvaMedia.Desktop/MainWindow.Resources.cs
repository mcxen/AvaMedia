using System.Globalization;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private readonly SystemResourceMonitor _systemResourceMonitor = new();
    private readonly DispatcherTimer _resourceTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _resourceSampling, _resetResourceSample = true, _resourceMonitorClosed;

    private void InitializeSystemResourceMonitor()
    {
        _resourceTimer.Tick += (_, _) => _ = RefreshSystemResourcesAsync();
        JobPresentationChanged += ResourcePresentationChanged;
        Opened += (_, _) => ResourcePresentationChanged(IsQueuePresentationVisible);
        Closed += (_, _) =>
        {
            _resourceMonitorClosed = true; _resourceTimer.Stop();
            JobPresentationChanged -= ResourcePresentationChanged;
            _ = Task.Run(_systemResourceMonitor.Dispose);
        };
    }

    private void ResourcePresentationChanged(bool visible)
    {
        if (visible && !_resourceMonitorClosed)
        { _resourceTimer.Start(); _ = RefreshSystemResourcesAsync(); }
        else { _resourceTimer.Stop(); _resetResourceSample = true; }
    }

    private async Task RefreshSystemResourcesAsync()
    {
        if (_resourceSampling || _closing || _resourceMonitorClosed || !IsQueuePresentationVisible) return;
        _resourceSampling = true;
        var reset = _resetResourceSample; _resetResourceSample = false;
        try
        {
            var usage = await Task.Run(() => _systemResourceMonitor.Sample(reset));
            if (!_closing && !_resourceMonitorClosed && IsQueuePresentationVisible)
                ResourceUsageText.Text = $"CPU {FormatResourceUsage(usage.Cpu)}   GPU {FormatResourceUsage(usage.Gpu)}";
        }
        finally { _resourceSampling = false; }
    }

    private static string FormatResourceUsage(double? value) => value is { } percent
        ? percent.ToString("0", CultureInfo.InvariantCulture) + "%" : "—";
}
