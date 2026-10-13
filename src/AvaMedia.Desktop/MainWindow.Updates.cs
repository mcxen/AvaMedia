using Avalonia.Threading;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private SettingsWindow? _softwareUpdatesWindow;
    private DateTimeOffset? _savedUpdateCheck;
    private int _updateEntryPosted;

    internal async Task OpenSoftwareUpdatesAsync()
    {
        if (_closing) return;
        if (_softwareUpdatesWindow is { IsVisible: true } existing)
        { existing.OpenSoftwareUpdates(); existing.Activate(); return; }
        if (!IsVisible) RestoreFromTray();
        _appliedSettings = _settings.Clone();
        var window = new SettingsWindow(_settings, _optionServices);
        window.SetMcpStatus(() => _mcp?.Status ?? "已关闭");
        window.SetMcpClients(() => _mcp?.RecentClients ?? []);
        _softwareUpdatesWindow = window;
        window.OpenSoftwareUpdates();
        window.Applied += (_, _) => ApplyOptions();
        try { await Controls.WindowChrome.ShowIndependentAsync(window); }
        finally { if (ReferenceEquals(_softwareUpdatesWindow, window)) _softwareUpdatesWindow = null; }
    }

    private void InitializeUpdateEntry()
    {
        ApplicationUpdater.Shared.ProgressChanged += UpdateEntryChanged;
        Closed += (_, _) => ApplicationUpdater.Shared.ProgressChanged -= UpdateEntryChanged;
    }

    private void UpdateEntryChanged(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref _updateEntryPosted, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _updateEntryPosted, 0);
            if (_closing) return;
            RefreshToolbarMenu();
            if (ApplicationUpdater.Shared.CheckState.CheckedAt is { } checkedAt && _savedUpdateCheck != checkedAt)
            {
                _savedUpdateCheck = checkedAt; _settings.LastUpdateCheck = checkedAt;
                try { _storage.SaveSettings(_settings); }
                catch (Exception error) { AppDiagnostics.Record("Save update check time", error); }
            }
        });
    }
}
