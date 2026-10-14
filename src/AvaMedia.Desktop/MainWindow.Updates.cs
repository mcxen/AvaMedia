using Avalonia.Threading;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private DateTimeOffset? _savedUpdateCheck;
    private int _updateEntryPosted;

    internal async Task OpenSoftwareUpdatesAsync()
    {
        if (_closing) return;
        if (!IsVisible) RestoreFromTray();
        await ShowSettingsAsync(this, window => window.OpenSoftwareUpdates());
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
